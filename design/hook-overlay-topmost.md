# hook 动画只绘制到队列最顶部余额监控胶囊 — 方案评估

## Context

**用户需求**(基于截图 `image/b53efe92-74d0-4741-973a-10e1d8b156aa.png`):

宿主 PaperTodo 里同屏出现 5 个胶囊(挂在右侧),其中第 1 和第 3 是本插件的余额监控胶囊(分别 MiniMax / DeepSeek 样式)。当 Claude Code hook 触发时,动画和文字**只画在最顶部那个余额监控胶囊上**(当前是第 1 个);如果用户拖动调换两个余额监控胶囊的位置,**自动跟随到新的最顶**的那个上;如果非余额监控胶囊插入到两个余额监控之间,行为不变(仍是余额监控队列里最顶)。

**关键约束**(AGENTS.md 第 197-203 行):
- 不得以任何形式修改、补丁、注入、反射修改宿主代码
- 不得通过反射、`AssemblyLoadContext` 干预、动态代理、共享内存、命名管道、文件锁竞争等方式间接改宿主状态
- 如果宿主当前没有插件侧的合法接口,在向用户确认前不要硬上替代方案
- 新增宿主能力必须先在宿主仓库实现并发布新版本

## 结论

**可在插件端部分实现,但语义需要近似**。完整精确实现需要先在宿主仓库加接口。

---

## 宿主侧事实

### 宿主内部数据(`src/EdgeCapsuleQueueCoordinator.cs`)

```csharp
internal sealed class EdgeCapsuleQueueCoordinator
{
    public static EdgeCapsuleQueuePlan Build(
        IEnumerable<EdgeCapsuleQueueMember> members, bool showMaster)
    {
        // ... 按 QueueKey(屏幕边缘键)分组,每组 papers 按加入顺序排
        // ... 每 paper 分配 EdgeCapsulePlacement(index, visualOffset, slotCount)
    }
}
```

- `EdgeCapsuleQueuePlan.Queues`:每个 queue 一组 `EdgeCapsuleQueue { Key, Papers[], HasMaster }`
- `EdgeCapsuleQueuePlan.Placements`:`PaperId → EdgeCapsulePlacement(index, visualOffset, slotCount)`
- `index = 0` 即每个队列的"最顶部"

**这是纯宿主内部,没有暴露给插件。**

### 宿主给 native 插件的接口(`IPaperTodoHostApi` in `PaperTodoHostContracts.cs:256-278`)

```csharp
public interface IPaperTodoHostApi
{
    IReadOnlySet<string> GrantedPermissions { get; }
    IReadOnlyList<PaperSnapshot> ListPapers(string? type = null);
    PaperSnapshot? GetPaper(string paperId);
    IReadOnlyList<TodoSnapshot> ListTodos(string? paperId = null, bool includeBlank = false);
    NoteSnapshot? GetNote(string paperId);
    PaperMutationResult CreatePaper(...);
    AppendTodosResult CreateTodoAppendResult(...);
    TodoMutationResult UpdateTodo(...);
    TodoMutationResult SetTodoReminder(...);
    NoteMutationResult WriteNote(...);
    DeleteMutationResult DeleteTodo(...);
    DeleteMutationResult DeletePaper(...);
    IDisposable Subscribe(PaperTodoEventFilter filter, Action<PaperTodoEvent> handler);
}
```

**关键缺口**:
- `PaperSnapshot` 字段:Title / Id / IsVisible / IsCollapsed / AlwaysOnTop / BodyProviderId — **没有队列位置 / topmost 字段**
- `PaperChangedFields` 枚举:`Title | Visibility | Collapsed | AlwaysOnTop | BodyProvider` — **没有队列变化 / 激活变化字段**
- `Subscribe()` 事件流:`PaperCreatedEvent` / PaperChangedEvent / PaperDeletedEvent + todo / note 事件 — **没有 TopMostChanged 这种事件**

### 宿主给插件的 own-paper 操作(`PaperBodyPluginHostApi.Presentation.cs`)

-`Show / Hide / ToggleVisibility / Expand / Collapse / ToggleCollapsed / Activate`
- **严格限定在 `_hostPaperId`**(每个 BalanceSession 只能操控自己那个 paper)

### `IPaperBodySession` 回调(PaperBodyPluginContracts.cs:391-413)

```csharp
public interface IPaperBodySession : IDisposable
{
    void OnActivated() { }       // 自己 paper 被激活(成为前台 / 焦点)
    void OnDeactivated() { }     // 自己 paper 失活
    void OnVisibilityChanged(bool visible) { }     // paper 可见性(胶囊折叠也 true)
    void OnPresentationChanged(bool visible) { }  // paper body 展开状态
    // ... OnThemeChanged / OnTypographyChanged / OnDpiChanged / OnSettingsChanged
}
```

**关键发现**:插件能收到自己 paper 的 `OnActivated / OnDeactivated`。这是宿主告诉插件"你的 paper 已成为最前"的最直接信号。

---

## 当前 hook overlay 实现

### 派发路径(`Services/HookOverlayController.cs:208-222`)

```csharp
private void DispatchToViews(HookOverlayPlan plan)
{
    // 4 个 view 派发:Color overlay 走 Ring 双视图(避免对勾动画干扰 Dot 圆点);
    // Spinner 走 4 个全部。全部 marshal 到 view dispatcher。
    DispatchToView(_regularRing, plan);
    DispatchToView(_dockedRing, plan);
    var isColorOverlay = plan.Kind is HookOverlayKind.StopImage
        or HookOverlayKind.PermissionImage
        or HookOverlayKind.FailureImage;
    if (!isColorOverlay)
    {
        DispatchToView(_regularDot, plan);
        DispatchToView(_dockedDot, plan);
    }
}
```

`_regularRing` / `_dockedRing` / `_regularDot` / `_dockedDot` 由 `BalanceSession.AttachViews(...)` 注入(`WebPanel/HookOverlayController.cs:55-72`),都来自**同一 BalanceSession = 同一 paper**。

### 当前问题

每张余额监控 paper 一个 `BalanceSession`,每 session 派发到 4 个 view。如果用户有 3 张余额监控 paper,会有 3 个 session,每个 session 独立收到 hook 事件并派发 overlay。**结果是所有余额监控胶囊同时显示 overlay**,无法识别哪个是"最顶部"那个。

---

## 评估结论

### **精确实现"队列最顶部"在当前宿主接口下不可行**

理由:
1. 宿主 `IPaperTodoHostApi` / `IPaperBodySession` 都没有"队列位置 / topmost"概念
2. `PaperSnapshot` / `PaperChangedFields` 都没有"队列位置变化"事件
3. 插件不能用反射 / 共享内存等手段绕开(AGENTS.md 明确禁止)

### 但可以**近似**实现 — 方案 A:基于"激活"状态

**核心思路**:
- 把"队列最顶部"近似看作"被激活的 paper"(在 dock 队列里这两者高度相关)
- 用 `IPaperBodySession.OnActivated() / OnDeactivated()` 维护每个 session 的 `_isActivated` 标志
- `HookOverlayController` 派发 overlay 时,**只推到 `_isActivated == true` 的 session**

**边界 / 失效场景**:
- 如果用户开了 AlwaysOnTop 把非激活的 paper 顶到队列顶,失效
- 截图中的 dock 队列场景:**基本等价**(宿主的 dock 队列行为是"激活 = 顶")

---

## 推荐方案:方案 A — `OnActivated` 近似 + 单一激活标志

### 改动文件**

| 文件 | 改动 |
|------|------|
| `Session/BalanceSession.cs` | 实现 `OnActivated()` / `OnDeactivated()`,维护 `_isActivated` 标志;新增 `_isHostActivated` 公开属性给 HookOverlayController |
| `Services/HookOverlayController.cs` | 在 `AttachViews` 时记录 view 所属 session 的 `_isActivated` 引用;派发前过滤 `_isActivated == false` 的 session |
| `Services/HookEventServer.cs` | 不变(继续对所有 BalanceSession 广播) |

### 数据流

```
Claude Code → HookEventServer → 所有 BalanceSession 的 OnHookReceived
                              ↓
                每个 BalanceSession 检查 _isActivated
                ├─ true  → 走 HookOverlayController 派发
                └─ false → 跳过,只把事件写入本地 hookEventWindow(活动流仍然更新)
```

**活动流**(`renderHooks`) 仍然所有 paper 可见(用户在展开面板时仍能回看),**仅 overlay(动画 + 文字)限制在最顶那个胶囊**。

### 关键代码改动

#### `Session/BalanceSession.cs`

```csharp
private bool _isActivated;  // 默认 false;被 OnActivated 置 true,OnDeactivated 置 false

public bool IsHostActive => _isActivated;  // 给 HookOverlayController 查

public void OnActivated()  // IPaperBodySession.OnActivated 实现
{
    _isActivated = true;
    // 如果有正在显示的 spinner overlay,且因为之前未激活被缓存,这里补派发
    HookOverlayController?.RefreshIfPending();
}

public void OnDeactivated()
{
    _isActivated = false;
    // 失活后立即清掉所有 overlay(避免"激活"切走时旧 overlay 还残留)
    HookOverlayController?.ClearAll();
}
```

#### `Services/HookOverlayController.cs`

新增一个 `IsHostActive` 委托 / 属性,由 `AttachViews` 注入:

```csharp
private Func<bool>? _isHostActive;
private bool IsHostActiveAllowed => _isHostActive?.Invoke() ?? true;

public void AttachViews(..., Func<bool>? isHostActive = null)
{
    _isHostActive = isHostActive;
    // ... 既有补发逻辑 ...
}

// 派发前置过滤
private void DispatchToView<T>(T? view, HookOverlayPlan plan) where T : FrameworkElement
{
    if (view == null) return;
    if (!IsHostActiveAllowed) return;  // 新增
    // ... 既有 dispatcher 逻辑 ...
}
```

#### `Session/BalanceSession.cs` — 接入 `IsHostActive`

```csharp
_overlayController.AttachViews(
    regularRing, dockedRing, regularDot, dockedDot,
    isHostActive: () => _isActivated);  // 新增
```

### 用户体验

| 场景 | 行为 |
|------|------|
| 单张余额监控 paper | 与现在一样:overlay 总是显示 |
| 多张余额监控 paper,激活其中一张 | 只有激活那张的胶囊显示 overlay |
| 切换激活 paper | 旧激活 paper 的 overlay 立即清掉;新激活 paper 的 overlay 立即显示(若有 pending plan) |
| AlwaysOnTop 把非激活 paper 顶到最前 | **失效**:被顶 paper 不显示 overlay(因为 `IsHostActive == false`)。建议告知用户:hook overlay 跟随"激活"而非"视觉最前" |

### 不变 / 保留

- 活动流(`renderHooks`)所有 paper 仍然接收事件
- 监视面板 / MiniView / 胶囊默认显示与现在一致
- hook overlay 文字 / 颜色 / 动画逻辑不变

---

## 替代方案对比

### 方案 B:设置项 `hookOverlayPaperId`(配置式)

```json
{ "id": hookOverlayPaperId, "type": string, "default": "" }
```

- 用户手动选择"hook overlay 显示在哪张 paper"
- 简单但失去"自动跟随"能力
- 不解决"位置变化"的问题(用户拖动调换,配置仍指向固定 paperId)

### 方案 C:跟宿主对接 PR,加 `TryGetTopmostCapsule` 接口

```csharp
// IPaperTodoHostApi 新增
bool TryGetTopmostCapsule(string providerId, out string paperId);
IDisposable SubscribeTopmostChanged(string providerId, Action<string> handler);
```

- 精确实现
- **违反 AGENTS.md "不得修改 PaperTodo.Plugin.Abstractions"** + "新增宿主能力必须先在宿主仓库实现"
- 需要先在 `PaperTodo` 仓库发 PR 等版本发布
- 长期方向

### 方案 D:HookEventServer 端做协调(违反 AGENTS.md)

- 插件内部维护"当前 topmost paperId"缓存
- 用 `Subscribe()` 订阅 `PaperChangedEvent` 自己推断"最近变更的可见 paper = topmost"
- **不准确**:`Visibility` 字段变化 ≠ 队列顺序变化
- 即使能准确,**违反 AGENTS.md "不得用共享内存/命名管道做插件间协调"**

---

## 风险与边界

1. **方案 A 与用户期望的语义不完全匹配**:"激活" ≠ "队列最顶部"严格相等。
   - dock 队列里通常等价,但 AlwaysOnTop / 拖动过程中可能短暂不等价
2. **激活切换有动画过渡**:用户拖动胶囊瞬间,失活的 paper 立即清 overlay,新激活的 paper 立即显示 — 看起来"动画跟着走"
3. **如果用户从不在 dock 队列(AlwaysOnTop 模式),始终是同一张 paper 激活,overlay 行为不变**
4. **hook 事件本身仍对所有 BalanceSession 广播**(事件流不会丢),只过滤 overlay 渲染

---

## 实施成本(方案 A 估算)

| 工作量 | 描述 |
|--------|------|
| 30 分钟 | `BalanceSession.cs`:实现 `OnActivated/OnDeactivated`,加 `_isActivated` + `IsHostActive` |
| 15 分钟 | `HookOverlayController.cs`:加 `IsHostActiveAllowed` 过滤 + 失活时清 overlay |
| 10 分钟 | `BalanceSession.cs`:`AttachViews` 时传 `isHostActive` lambda |
| 15 分钟 | 部署 + 手动验证 3 张余额监控 paper 场景 |

总计约 1 小时,**风险点**:确认宿主 `OnActivated` 的触发时机与"队列最顶部"语义对齐。

---

## 验证方案(方案 A)

1. **新建 2 张余额监控 paper**(一张 MiniMax,一张 DeepSeek),都填好 API Key
2. 让两张都折叠成胶囊,观察队列里两个余额监控胶囊
3. 触发 hook 事件(让 Claude Code 调一次 Bash),验证只有顶部那个胶囊显示动画
4. 点击切换"激活"到另一张 paper(模拟拖动 /点击),验证动画跟随切换
5. 验证活动流仍然两边都更新
6. 单 paper 场景回归(应该与现在一样)

## 相关代码索引

- `Services/HookOverlayController.cs:55-72`(`AttachViews`)
- `Services/HookOverlayController.cs:208-222`(`DispatchToViews`)
- `Session/BalanceSession.cs`(`BalanceSession` 当前没有 `OnActivated/OnDeactivated` 实现)
- `Z:\tool\PaperTodo\PaperTodo.Plugin.Abstractions\PaperBodyPluginContracts.cs:391-413`(`IPaperBodySession` 回调)
- `Z:\tool\PaperTodo\src\EdgeCapsuleQueueCoordinator.cs:55-94`(宿主内部队列数据)
- `Z:\tool\PaperTodo\PaperTodo.Plugin.Abstractions\PaperTodoHostContracts.cs:256-278`(`IPaperTodoHostApi` 暴露面)
- `Z:\tool\PaperTodo\AGENTS.md:197-203`(插件不能做的约束)