# 修复方案:HookEvent Window 并发崩溃


---

## 1. Bug 原因(根因)

`Session/BalanceSession.cs` 中的 `OnHookReceived`(line 196-204)使用了错误的 marshaling 逻辑:

```csharp
var dispatcher = System.Windows.Threading.Dispatcher.FromThread(Thread.CurrentThread);
if (dispatcher != null && !dispatcher.CheckAccess())
{
    dispatcher.BeginInvoke(() => OnHookReceived(payload));
    return;
}
```

- `Dispatcher.FromThread(Thread.CurrentThread)` 取的是**当前调用线程**的 dispatcher。
- `OnHookReceived` 由 `HookEventServer.HookReceived?.Invoke(payload)` 同步触发,而 `HookReceived` 在 `HookEventServer.HandleAsync` 中,`HandleAsync` 又被 `_ = Task.Run(() => HandleAsync(ctx))` 调度到**线程池线程**(`Services/HookEventServer.cs:95`)。
- **线程池线程没有 Dispatcher** → `dispatcher == null` → `if` 条件为 false → **跳过 marshaling**,直接落到下面 `_hookEventWindow.Dequeue()` / `Enqueue(hook)`(`Session/BalanceSession.cs:217-221`)。
- 注释(`Session/BalanceSession.cs:107-108, 179-181`)明确写了"HookEventServer 接收 POST 后在 UI 线程 marshal 入队",但实现**从未生效**。

**结果**:后台线程直接操作 `Queue<HookEvent>`,与 UI 线程(`PollAsync` → `UpdateSnapshot` → `Build()` → `HookEventWindow.ToArray()`)并发,破坏 `Queue<T>` 的 `_array` / `_size` / `_head` / `_tail` 内部不变量。

---

## 2. 最近导致崩溃的原因

`Queue<T>` **不是线程安全的**。两条崩溃堆栈是同一根因在不同时刻的两个表现:

### 崩溃 A — 22:02:06

```
System.ArgumentException: Destination array was not long enough ...
   at System.Array.CopyImpl(...)
   at System.Array.Copy(...)
   at System.Collections.Generic.Queue`1.ToArray()
   at ViewPayloadBuilder.Build()
   at BalanceSession.UpdateSnapshot(...)
   at BalanceSession.PollAsync()
```

- 后台线程 `Enqueue` 触发 `Queue<T>.Grow`:**`_array` 已被替换为新数组(长度变大),`_tail` 暂未更新到 `_size`**,`_head` 也未复位为 0。
- 同一瞬间 UI 线程进入 `ToArray`:
  1. 读 `_size`(旧值),分配 `arr = new T[_size]`(小数组)。
  2. 读 `_head = 0`,`_tail = 旧值`(未更新)。
  3. `_head < _tail` 不成立 → 走 `else` 分支。
  4. `Array.Copy(_array=新数组, _head=0, arr, 0, _array.Length - _head)` —— 此时 `_array.Length` 已是新长度(8),但 `arr.Length` 是旧值(4),destination 不够长 → **`ArgumentException`**。

### 崩溃 B — 22:08:34

```
System.NullReferenceException: Object reference not set to an instance of an object.
   at ViewPayloadBuilder.BuildHooks(IReadOnlyList<HookEvent> window)
   at ViewPayloadBuilder.Build()
   at BalanceSession.UpdateSnapshot(...)
```

- 同一并发,在另一个时间窗口下,`ToArray()` 没有命中越界,但 `arr` 中部分 slot 是 grow 后未填充的 `null`(`HookEvent` 是引用类型)。
- `BuildHooks` 拿到 `h = window[i]` 为 `null` → `h.EventName` → **NRE**。

两次崩溃都属于"`Queue<T>` 内部状态被并发破坏"。

---

## 3. 相关文件清单

### 直接相关(本方案要修改)

| 文件 | 行 | 关键符号 / 作用 |
|---|---|---|
| `Session/BalanceSession.cs` | 130-176 | 构造函数 —— 需要新增 `_uiDispatcher` 字段并在 STA 上下文中初始化 |
| `Session/BalanceSession.cs` | 109-110 | 字段 `_latestHookEvent` / `_hookEventWindow` —— 需要新增锁对象与加锁 |
| `Session/BalanceSession.cs` | 196-236 | `OnHookReceived` —— 修复 marshaling + Queue 加锁(主修复点) |
| `Session/BalanceSession.cs` | 84, 86 | `LatestHookEvent` / `HookEventWindow` getter —— getter 内加锁 |

### 间接相关(本方案不修改,但要确认不破现有功能)

| 文件 | 作用 | 验证点 |
|---|---|---|
| `Payload/ViewPayloadBuilder.cs:77` | `BuildHooks(_session.HookEventWindow)` —— 仅消费 getter | getter 加锁不影响调用 |
| `Payload/ViewPayloadBuilder.cs:113-128` | `BuildHooks` 内部 `window[i].EventName` —— NRE 触点 | 修好根因后,NRE 不再出现 |
| `Session/BalanceSession.cs:243-375` | `ApplyHookOverlayToCapsules` —— view 内部 dispatcher 二次 marshal | 保持原状,本方案不破坏 |
| `Session/BalanceSession.cs:389-391, 875-877` | `BuildCapsuleText` ToolTip 消费 `_latestHookEvent` —— 仅消费 getter | marshal 修复后字段写入仍在 UI 线程,行为不变 |
| `Services/HookEventServer.cs:79-144` | `ListenLoopAsync` / `HandleAsync` —— 后台线程来源 | 保持原状,本方案不动 |
| `Services/HookEventServer.cs:133` | `HookReceived?.Invoke(payload)` —— 同步触发订阅者 | 本方案仅改订阅者侧,事件本身不变 |
| `Models/HookEvent.cs` | record 定义 | 无需修改 |
| `Services/HookTrace.cs` | 诊断日志 | 无需修改 |
| `Rendering/CapsuleView.Ring.cs:111`, `Rendering/CapsuleView.Dot.cs:127` | view 侧 `SetHookOverlay` | 仅消费传入参数,不受本方案影响 |

### 潜在相关文件(待二次确认)

| 文件 | 作用 | 是否需要修改 |
|---|---|---|
| `WebPanel/WebViewPanelHost.cs` | 接收 `PostSnapshot` 并推到 WebView2 | **不修改** —— `PostSnapshot` 仍由 OnHookReceived 在 UI 线程调,数据流不变 |
| `Rendering/MiniView.*.cs` | 1.8 边缘预览视图,目前未消费 hook 字段 | **不修改** |
| `Tests/` 下相关单测 | 项目未找到覆盖 hook 路径的单测(grep 结果为空) | **不修改**,但建议补一个回归用例(见 §6) |
| `WebPanel/*.html`(前端) | `window.__renderBalance(data.hooks)` 消费 | **不修改** —— JSON 形状不变 |
| `PaperTodo.Plugin.ApiBalanceMonitor.csproj` | 项目文件 | **不修改** |

---

## 4. 潜在影响范围(Blast Radius)

### 数据流影响

`OnHookReceived` 的执行路径:

```
后台线程                                  UI 线程
─────────                                  ──────
HookReceived?.Invoke(payload)
  → OnHookReceived                          ←─── BeginInvoke marshal(本方案修复)
                                              IsHookEnabled 过滤
                                              _latestHookEvent = hook
                                              Queue.Dequeue / Enqueue(本方案加锁)
                                              PostSnapshot(Build())
                                              HookTrace.Write
                                              ApplyHookOverlayToCapsules(二次 marshal)
```

修复后所有 mutation 仍然发生在 UI 线程(与设计意图一致),`PostSnapshot` 调用时机、`HookTrace` 调用时机、`ApplyHookOverlayToCapsules` 触发时机都**不变**。

### View 层影响

| 消费方 | 当前读取 | 修复后读取 | 行为差异 |
|---|---|---|---|
| `BuildCapsuleText` ToolTip(line 875-877) | `_latestHookEvent` | 同 | 无 |
| `BuildHooks`(payload.hooks 字段) | `HookEventWindow` getter | 同(加锁) | 无(返回数组内容相同) |
| `ApplyHookOverlayToCapsules`(line 243) | 仅读 `payload.Overlay` | 同 | 无 |
| Web 面板(前端) | `data.hooks` 数组 | 同 | 无 |

### 性能影响

- **dispatcher 跳转**:从后台线程 `BeginInvoke` 到 UI 线程,本来就是设计意图;无新增开销。
- **lock 粒度**:Queue 容量固定 5(`HookEventWindowCapacity = 5`),`Dequeue` + `Enqueue` + `ToArray` 都是常数时间。`lock` 持有时间 < 1ms。
- **并发风险**:`OnHookReceived` 持锁期间**不调用** `_payloadBuilder.Build()`(把 Build 移到锁外),避免 `Build()` → `HookEventWindow` getter → 同一把锁的重入(无死锁风险,但仍遵循先释放再调用的最稳模式)。

### 不修改但需保护的现有功能

1. **胶囊 ToolTip 第二行**(BuildCapsuleText line 875-877):消费 `_latestHookEvent`。
2. **Web 面板"Claude 活动"列表**(前端 `data.hooks`):消费 `HookEventWindow` getter。
3. **胶囊 overlay**(ApplyHookOverlayToCapsules):消费 `payload.Overlay`,内部已自带 view dispatcher 跳转。
4. **Color overlay 倒计时**(`_activeOverlayTimer`):与本方案无关。
5. **Hook 事件过滤**(`IsHookEnabled`):保持不变。

---

## 5. 修复方案

### 5.1 设计原则

- **最小变更**:只动 `Session/BalanceSession.cs`,其他文件零改动。
- **深度防御**:既修复 marshaling(根因),又给 Queue 加锁(兜底)。
- **零功能损失**:所有现有读取/写入时机保持不变。
- **不引入新依赖**:只用 `System.Threading` / `System.Windows.Threading`。

### 5.2 改动点 1 —— `Session/BalanceSession.cs` 字段区

新增 2 个字段:

```csharp
// UI 线程 dispatcher:OnHookReceived 在后台线程触发时,统一 marshal 进来。
// BalanceSession 由宿主在 STA UI 线程创建,Dispatcher.CurrentDispatcher 在此返回 UI dispatcher。
// 缓存下来避免每次重新查询(也避免再次踩中"Dispatcher.FromThread(Thread.CurrentThread) 返回 null"的坑)。
private readonly Dispatcher _uiDispatcher;
// _hookEventWindow / _latestHookEvent 的锁:深度防御,即便 marshaling 漏改某处,
// 也不会让 Queue 内部状态被并发破坏(Queue<T> 不是线程安全的)。
private readonly object _hookLock = new();
```

### 5.3 改动点 2 —— 构造函数(line 130-176)

在构造函数**最开头**(所有字段赋值之前)插入:

```csharp
_uiDispatcher = Dispatcher.CurrentDispatcher;
```

> **为何必须在构造函数最开头**:此时线程上下文是 STA UI 线程(否则 `DispatcherTimer` 创建会抛异常,line 154 已隐含此约束)。任何后续逻辑(如 `ApplySettings`)如果内部走异步或 marshal,都不能再依赖"当前线程就是 UI 线程"。

### 5.4 改动点 3 —— `OnHookReceived` 主体(line 196-236)

替换 196-204 的 marshaling 判断,改为:

```csharp
private void OnHookReceived(HookEventPayload payload)
{
    // 修复:用缓存的 UI dispatcher 判断,而不是 Dispatcher.FromThread(Thread.CurrentThread)。
    // 后者在线程池线程上永远返回 null,导致 marshaling 形同虚设。
    if (!_uiDispatcher.CheckAccess())
    {
        _uiDispatcher.BeginInvoke(() => OnHookReceived(payload));
        return;
    }
    // 按 settings 过滤:未启用的 hook 事件直接丢弃,不写缓存、不推面板。
    if (!IsHookEnabled(payload.EventName))
    {
        return;
    }
    var hook = new HookEvent(
        EventName: payload.EventName,
        ToolName: payload.ToolName,
        Summary: payload.Summary,
        ReceivedAt: payload.ReceivedAt,
        Overlay: payload.Overlay);
    _latestHookEvent = hook;
    // 锁内只做 Queue mutation;把 _payloadBuilder.Build() 放到锁外,避免重入。
    lock (_hookLock)
    {
        if (_hookEventWindow.Count >= HookEventWindowCapacity)
        {
            _hookEventWindow.Dequeue();
        }
        _hookEventWindow.Enqueue(hook);
    }
    // 推 Web 面板:payload 自动包含最近 5 条 hook 事件(ViewPayloadBuilder 会读)。
    _panel.PostSnapshot(_payloadBuilder.Build());
    Services.HookTrace.Write($"post-snapshot event={payload.EventName} kind={payload.Overlay} regularRing={_regularRingCapsuleView != null} regularDot={_regularDotCapsuleView != null}");
    // 胶囊 overlay:ApplyHookOverlayToCapsules 内部已 RepushCapsulePresentation 用 overlay 文本
    // 推 host,避免胶囊按余额文本宽度省略。
    try
    {
        ApplyHookOverlayToCapsules(payload.Overlay);
        Services.HookTrace.Write($"overlay-applied activeText={_activeOverlayText ?? "null"} pendingKind={_pendingOverlayKind}");
    }
    catch (Exception ex)
    {
        Services.HookTrace.Write($"ApplyHookOverlayToCapsules THREW: {ex.GetType().Name}: {ex.Message}");
    }
}
```

### 5.5 改动点 4 —— `HookEventWindow` getter(line 86)

```csharp
/// <summary>滑窗:最近 5 条 hook 事件;用于 Web 面板"Claude 活动"列表。</summary>
internal IReadOnlyList<HookEvent> HookEventWindow
{
    get
    {
        lock (_hookLock)
        {
            return _hookEventWindow.ToArray();
        }
    }
}
```

> `LatestHookEvent` getter(line 84)是引用类型字段读,原子性已经由 .NET 内存模型保证(`HookEvent` 是 `record class`),无需加锁。

### 5.6 改动点 5 —— 单元注释 / XML doc 微调

把 line 107-108、line 179-181、line 198 这三处注释里的"marshal"语义描述更新为"`_uiDispatcher` marshal",与新实现对齐。

---

## 6. 回归验证(方案实施后建议跑)

虽然项目当前没有覆盖 hook 路径的单测(grep 显示 `⚠️ no covering tests found`),但建议补一个最小回归用例:

- **回归用例 1**(并发触发):
  - 启动 mock `HookEventServer`,并发发 100 个 POST。
  - 每次 POST 后立即在 UI 线程调 `_payloadBuilder.Build()`。
  - 验证不抛 `ArgumentException` / `NullReferenceException`,且最终 `HookEventWindow` 内容正确。

- **回归用例 2**(线程验证):
  - 给 `_hookEventWindow` 写入 hook 时记录 `Thread.CurrentThread.ManagedThreadId`。
  - 读取时记录相同 ID。
  - 验证二者一致(说明 marshaling 生效)。

- **手工烟测**(沿用现有):
  - 启动 PaperTodo,触发 Claude Code 的 `PostToolUse` / `PreToolUse` hook 各 10 次。
  - 确认 WebView2 面板"Claude 活动"列表正确显示。
  - 确认胶囊 ToolTip 第二行出现 hook summary。

---

## 7. 风险评估

| 风险 | 等级 | 缓解措施 |
|---|---|---|
| `Dispatcher.CurrentDispatcher` 在非 STA 抛异常 | 极低 | BalanceSession 构造函数已经隐含 STA(line 154 `DispatcherTimer` 构造) |
| `lock` 嵌套死锁 | 极低 | `_payloadBuilder.Build()` 移到锁外 |
| 性能回退 | 极低 | Queue 容量 = 5,常数时间操作;`BeginInvoke` 本就是设计意图 |
| 现有行为变化 | 极低 | marshaling 修复让 OnHookReceived 真正在 UI 线程跑,与注释承诺一致;功能等价于"加一个早该有的 lock" |
| view 侧线程问题 | 无 | view 侧 `SetHookOverlay` 已被 `ApplyHookOverlayToCapsules` 内部的 dispatcher 跳转保护,本方案不动该路径 |

---

## 8. 备选方案(不推荐,仅记录)

- **方案 B1**:把 `Queue<HookEvent>` 换成 `ConcurrentQueue<HookEvent>`。
  - 缺点:`ConcurrentQueue<T>.ToArray()` 在并发环境下是快照语义,但容量限 5 + 滑窗语义下 FIFO 顺序与 `Queue<T>.ToArray()` 一致;同时 `Dequeue` 是破坏性操作,无法用 `ConcurrentQueue` 表达"满了弹一个再入一个"的语义。
  - **不推荐**。

- **方案 B2**:把 `HookEventWindow` 改为不可变快照(每次 Enqueue 时重建 `HookEvent[]`)。
  - 缺点:`_latestHookEvent` 是引用类型,赋值即发布,但 `HookEvent` 是 `record class`,整体替换是原子读;不增加额外开销。
  - 但 `_hookEventWindow` 的 ToArray 路径需要锁保护,本质等于"加锁"。
  - **不推荐**(等价于本方案 + 增加内存分配)。

---

## 9. 验收标准

- [ ] 实施完成后,任意 5 分钟内并发触发 100+ hook POST,PaperTodo 不再抛出 `ArgumentException` 或 `NullReferenceException`。
- [ ] `%TEMP%\api-balance-hook.log` 不再出现与 `_hookEventWindow` 相关的崩溃栈帧。
- [ ] 现有功能验证清单:
  - 胶囊 ToolTip 第二行 hook summary 正常显示
  - Web 面板"Claude 活动"列表按 FIFO 显示最近 5 条
  - 胶囊 color overlay(Stop / Permission / Failure)正常显示并自动恢复
  - 胶囊 spinner overlay(PreTool / PostTool)正常显示并由下次 Update 清掉
  - `_activeOverlayTimer` 倒计时正常
- [ ] 代码静态检查:`grep -nE 'Dispatcher\.FromThread\(Thread\.CurrentThread\)' Session/BalanceSession.cs` 应无结果。

---

## 10. 实施时需要回滚的备份点

- `Session/BalanceSession.cs` —— 改前 git stash 或备份原文件
- 实施完成后跑一次 `dotnet build PaperTodo.Plugin.ApiBalanceMonitor.csproj`,确认无编译错误
- 实施完成后跑一次 `dotnet test`(如有单测),确认无回归
