# PaperTodo.ApiBalanceMonitor Agent 备忘

本文件只记录「不通读宿主和本仓库代码很难知道」的插件实现约束。代码是真相;普通 WPF / C# 常识、宿主项目通用规则不要写进来。

宿主仓库位于 `../PaperTodo`,其 `AGENTS.md` 描述宿主全局约束;本文件只覆盖本插件特有的部分,凡未提及之处均以宿主 `AGENTS.md` 为准。

---

## 一、技术栈声明

- **运行时**:.NET 10 (`net10.0-windows10.0.17763.0`),`RuntimeIdentifier=win-x64`,`SelfContained=false`(跟随宿主运行时)。
- **UI 框架**:WPF(`UseWPF=true`);不引入 WinForms / MAUI / Avalonia 等替代栈。
- **浏览器控件**:`Microsoft.Web.WebView2 1.0.4078.44`(仅用于展开后的监视面板);胶囊 / MiniView 不使用 WebView2。
- **JSON**:`System.Text.Json`;设置 / 状态解析失败一律回落到默认值,不抛异常给用户。
- **HTTP**:`System.Net.Http.HttpClient`,超时 15s;不引入第三方 HTTP 库。
- **插件协议**:
  - `apiVersion`:跟随宿主当前主版本(`PaperBodyPluginRegistry.SupportedPluginApiVersion = "1.8"`)。
  - `stateVersion`:2(`1` → `2` 一次性清空迁移)。
  - `kind`:native。
  - `runtimeRequirements`:`BackgroundUpdates`(已声明)。
  - `capabilities`:当前 `[]`。
- **宿主契约引用**:仅 `PaperTodo.Plugin.Abstractions`(只读),作为插件与宿主之间的唯一接口边界。
- **共享宿主程序集**(不要随 dll 一起打包,依赖解析时由宿主回落到宿主副本):`PaperTodo.Plugin.Abstractions`、`WinRT.Runtime`、`Microsoft.Windows.SDK.NET`、`Microsoft.Web.WebView2.Core`、`Microsoft.Web.WebView2.Wpf`、`Microsoft.Web.WebView2.WinForms`。
- **版本号**:显式维护在 `PaperTodo.Plugin.ApiBalanceMonitor.csproj` 的 `<Version>` 与 `plugin.json` 的 `version`,二者必须保持一致;不要恢复自动递增。

---

## 二、目录结构

```
PaperTodo.ApiBalanceMonitor/                # 本仓库根(插件源码仓库)
├── AGENTS.md                                # 本文件
├── README.md                                # 用户文档(中文)
├── plugin.json                              # 插件部署清单;插件 ID、设置项、requires 等
├── PaperTodo.Plugin.ApiBalanceMonitor.csproj
├── ApiBalanceMonitorPlugin.cs               # 唯一插件实现(单文件,~2900 行)
├── Sync-PluginsToOutput.ps1                 # 把 plugin.json / web/ / 编译产物复制到宿主输出目录
├── Test-DeepSeekBalance.ps1                 # DeepSeek 接口连通性测试(只测网络和 Key)
├── Test-MiniMaxCodingPlan.ps1               # MiniMax 接口连通性测试(只测网络和 Key)
├── web/
│   ├── monitor.html                         # DeepSeek 监视面板(展开后)
│   └── minimax.html                         # MiniMax 监视面板(展开后)
├── image/
│   └── README/                              # README 引用截图(178715*.png 等)
├── bin/                                     # 编译产物(Debug / Release,自动生成,不入库)
├── obj/                                     # 编译中间产物(自动生成,不入库)
└── .gitignore                               # 屏蔽 bin/ obj/ .runtime/ 等
```

部署目标目录(`Sync-PluginsToOutput.ps1` 默认):

```
../PaperTodo/输出/PaperTodo-v4.0.0-preview/plugins/api.balance.monitor/
├── plugin.json
├── PaperTodo.Plugin.ApiBalanceMonitor.dll
├── PaperTodo.Plugin.ApiBalanceMonitor.deps.json
└── web/
    ├── monitor.html
    └── minimax.html
```

宿主管理的运行时文件(**不要提交**):

- `plugins/data/api.balance.monitor.json` — 全局设置 + per-paper state,由宿主 `PaperBodyPluginDataStore` 写入。
- `plugins/api.balance.monitor/.runtime/webview2/` — WebView2 `userDataFolder`,每次会话可能变更。

代码组织约定:

- `ApiBalanceMonitorPlugin.cs` 是单一入口;按功能分段(`IPaperBodyPlugin` 元数据 / `BalanceSession` / 设置解析 / HTTP 拉取 / 胶囊视图 `BalanceCapsuleView` + `BalanceProgressRing` / MiniView `BalanceMiniView` / WebView2 面板 / payload 构建 / 格式化工具)。
- 内部 record 类型(`BalanceSettings` / `PaperState` / `BalanceSnapshot` / `UsageDay` / `CostDay` / `MiniViewSnapshot` / `MiniMaxQuota` / `DeepSeekMetrics`)集中放在会话类之前。
- 私有常量颜色 / 风险档位阈值 / URL 与会话类同文件,不分散到多个 `Constants.cs`。
- 不要新增 `Models.cs` / `Services/` / `Views/` 等目录结构;单文件实现已足以覆盖本插件规模,过早拆分反而打破"读一个文件就能理解全部"的特性。

---

## 三、编码规范

### 语言与可读性

- 对话、解释、建议、**代码注释**、**Commit Message** 一律使用**简体中文**。
- 保留专业术语原样(`API` / `SDK` / `JSON` / `HTTP` / `WPF` / `WebView2` 等),不要音译或自造词。
- 注释解释"为什么"而不是"做了什么";代码本身已说明"做了什么"。每段非平凡逻辑(协议细节、性能优化、跨平台兜底、宿主管线约束)都要有 1–3 行注释。

### C# / WPF 性能与渲染

- 所有 `SolidColorBrush` 必须 `Freeze()`(包括 `ToBrush` 返回值 / `_grayBrush` / `RiskColor` 中间值);`Pen` / `StreamGeometry` 也必须 Freeze;让 WPF 渲染系统走快路径并允许跨线程共享。
- `PaperBodyTheme.FontFamily` 是 Source 字符串,缓存为 `FontFamily` 后按字符串相等判断复用;每次 `ApplyTheme` 都重建 `FontFamily` 会触发 WPF 字体回退链(首次可达 100ms 级)。
- 圆环控件 `BalanceProgressRing` 的 `Pen` 与弧形 `StreamGeometry` 按值缓存,只在外参变化时重建,否则首屏会出现秒级卡顿。
- 自绘胶囊 / MiniView 内部控件统一设置 `IsHitTestVisible=false`、`Focusable=false`,避免与宿主输入管线冲突;宿主会再次强制重置,本地保险即可。
- 文本宽度测量走 `TextBlock.Measure(...)` + `DesiredSize.Width`,与渲染管线同源;不要按"每字符 N DIP"线性估算作为正式逻辑。

### 数值与错误处理

- 数值钳制统一走 `Finite(v)` / `FiniteOrNull(v?)`,`NaN` / `±Infinity` → 0 或 null;不要在各路径分散判断。
- `HttpClient.Timeout = 15s` 在 .NET 中以 `TaskCanceledException` 呈现,`PollAsync` 必须单独捕获并提示"请求超时,请检查网络连接",不要把异常类型名(`TaskCanceledException` / `HttpRequestException`)抛给用户。
- `FetchJsonAsync` 把所有网络 / 解析异常归为 `null`;只有"JSON 合法但缺关键字段"才走 `BalanceSnapshot.Error("缺少 ...")` 之类的友好提示。
- `Dispose` 内的清理(`_webView.Dispose()` 等)用 `try { } catch { }` 吞掉所有异常,释放阶段不应抛异常干扰宿主卸载。

### 状态推送与签名去重

- `UpdateSnapshot` 内所有写路径(`SetCapsulePresentation` / 1.7 自绘 view / `PushView`)必须先比较 `signature`(文本 + 风险比 + 颜色 + statusText),相同则整段跳过,避免定时器 tick 触发宿主布局抖动。
- 哨兵定时器(30 秒 `_peakCheckTimer`)只用于判断是否进入 / 离开时段,本身不拉网络;不要在哨兵里堆业务逻辑。
- 业务状态推送路径与 UI 渲染路径分离:MiniView / Web 面板只读 `_snapshot` / `_usageDays` / `_costDays` / `_minimaxModelRemains` 等已缓存字段,不要在渲染时拉网络。

### 并发

- 主轮询 `DispatcherTimer` + `HttpClient`;用 `Interlocked.Exchange(ref _polling, 1)` 串行化,避免请求堆积与相互取消。
- DeepSeek 余额 / 用量 / 消费三个接口并行(`Task.WhenAll`),失败分别落到 `null`,不要让单个失败中断整个 `PollAsync`。
- WebView2 消息解析 / 设置解析 / 状态序列化等可失败操作统一 `try { } catch { }`,不要把内部异常冒泡到宿主;该抛的只有"插件契约本身被破坏"这类不可恢复错误。

### 命名与作用域

- 内部 record / 枚举使用 `internal sealed record` / `internal enum`,不暴露给宿主。
- 公开 API(`IPaperBodyPlugin.Id` / `DisplayName` / 等)严格匹配 `plugin.json` 声明(`Id == "api.balance.monitor"`、版本号、API 版本号)。
- 字段 / 属性命名遵循宿主约定:`_camelCase` 私有字段、`PascalCase` 公共属性;常量用 `PascalCase`。
- 风险档位 / 颜色 / URL 等"魔法值"提为 `private const`,不要在表达式里直接写字面量。

---

## 四、构建命令

普通开发构建:

```powershell
dotnet build PaperTodo.Plugin.ApiBalanceMonitor.csproj -c Release
```

产物路径:`bin/Release/net10.0-windows10.0.17763.0/win-x64/`,仅需要以下两项进入部署目录:

- `PaperTodo.Plugin.ApiBalanceMonitor.dll`
- `PaperTodo.Plugin.ApiBalanceMonitor.deps.json`

不要携带 PDB / XML 文档 / 宿主已提供的共享程序集 / 中间原生库。

部署到宿主输出目录(自动找 `../PaperTodo/输出/PaperTodo-v4.0.0-preview/`,可用 `-OutputDir` 覆盖):

```powershell
powershell -File Sync-PluginsToOutput.ps1
# 或:
powershell -File Sync-PluginsToOutput.ps1 -OutputDir "D:\path\to\PaperTodo-v4.0.0-preview"
```

该脚本会一并复制 `plugin.json`、`web/` 与编译产物;复制完成后**必须重启 PaperTodo**,native 插件不支持热重载。

接口连通性测试(只测网络和 Key,不会改 PaperTodo 任何状态):

```powershell
# 从插件数据文件读取 Key;不存在则回退到环境变量
powershell -File Test-DeepSeekBalance.ps1
powershell -File Test-MiniMaxCodingPlan.ps1

# 通过环境变量传入,避免命令历史泄露
$env:DEEPSEEK_API_KEY = "sk-..."
powershell -File Test-DeepSeekBalance.ps1
```

调试顺序建议:**先用脚本确认网络 / Key → 再用插件跑**;不要在插件里写网络调试代码(临时 `Console.WriteLine` 之类),需要诊断时直接断点 `FetchJsonAsync` / `PollAsync`。

---

## 五、Never 规则

以下规则是**强约束**,违反任意一条都需要先与用户确认。

### 不修改宿主代码

- **本项目的所有功能实现都必须通过插件完成,严禁以任何形式修改、补丁、注入、反射修改宿主代码。**
- 仓库内 `../PaperTodo/` 是宿主,本仓库只读、不可写,不要 fork 修改。
- 不得通过反射、`AssemblyLoadContext` 干预、动态代理、共享内存、命名管道、文件锁竞争等方式间接改宿主状态。
- 不得向宿主 `plugins/` 目录写宿主自身 dll、原生库或 PDB / XML 文档;只放本插件的 `plugin.json`、`*.dll`、`*.deps.json`、`web/`。
- 不得修改宿主 `PaperTodo.Plugin.Abstractions` 内的契约类型;插件引用它作为只读接口。
- 新增宿主能力必须先在宿主仓库实现并发布新版本,再在本插件使用;不要在本仓库尝试"模拟"宿主能力。
- 如果宿主当前确实没有插件侧的合法接口,在向用户确认前不要硬上替代方案。

### 不越过协议层

- 1.8 MiniView 内部**禁止**使用 `Window`、`HwndHost`、`WindowsFormsHost`、`WebView2`、已 parented 控件;违反者宿主会拒载。
- 1.7 自绘胶囊必须返回 **fresh unparented** `FrameworkElement`(宿主校验 `Parent == null`);复用缓存实例时通过 `Update(...)` 原地刷新,不要重新 `SetCapsulePresentation` 触发重建。
- 监视面板 `web/*.html` 内**禁止**发任何网络请求(`fetch` / `XHR`);所有数据由 C# 端拉取并经 `PostWebMessageAsJson` + `ExecuteScriptAsync` 双通道推送。
- 不要在 MiniView 内做定时轮询;业务状态由胶囊的 `DispatcherTimer` 统一驱动,MiniView 只读已缓存字段。
- 不要假定宿主会热重载本插件:修改 dll 后**必须**重启 PaperTodo。

### 不引入冗余 / 不破坏兼容性

- 不要携带 PDB / XML 文档 / 宿主已提供的共享程序集到部署目录。
- 不要在 `web/*.html` 里引外部 CDN / 字体文件 / 图片(断网 / 离线环境下不可用)。
- 不要向 `plugins/api.balance.monitor/.runtime/webview2/` 提交用户数据缓存。
- 不要绕过 `IPaperBodyContext.SaveStateJson` 直接读写 `plugins/data/api.balance.monitor.json`。
- 不要回退到旧版「绿黄红」三分档或按余额绝对值变色的胶囊风险色;v3.1 档位是当前契约。
- 不要在 MiniView 进度条 fill 上恢复按 `RiskColor(ratio)` 染色;中性灰 `#808080` 已冻结为当前契约。
- 不要把异常类型名(`TaskCanceledException` / `HttpRequestException` / `JsonException`)直接展示给用户;用友好中文提示。

### 不破坏用户体验

- 不要在 UI 文案里硬编码白色 / 黑色,统一跟随宿主主题 `PaperBodyTheme.TextColor` / `PaperBodyTheme.PaperColor`。
- 不要把供应商 Key(`deepseekApiKey` / `minimaxApiKey` / `opencodeApiKey`)写入 per-paper state;Key 始终走全局设置。
- 不要为 OpenCode Go 引入半成品 UI;面板返回"尚未适配该供应商"是当前契约。
- 不要把多个供应商的监视面板 HTML 塞进同一个文件用 `if` 分发;`web/monitor.html` 与 `web/minimax.html` 各自维护,新增供应商时按 `HtmlFileNameFor(provider)` 同步扩展。
- 不要让签名 / 风险色 / status 文本中**任何一个**变化就触发 `SetCapsulePresentation` 重写;signature 去重是宿主布局稳定的前提。

---

## 六、约定与协作

### 工作方式

- 不要用临时最简原型、止血式局部假模型或明显偏离产品形态的替代实现来交付改动。除非改动巨大到需要重新定路线,必须先向用户确认,再按真实产品结构修改。
- 需要提交时,如果未提交改动能按功能边界无损拆分,并且每个提交都保持可构建、可理解、可独立回滚,应拆成多个独立提交方便管理;不要把无关文档、备份文件或用户的其他改动混入功能提交。
- 实现前先读一遍宿主 `PaperTodo.Plugin.Abstractions/` 下的接口与 `PaperBodyPluginRegistry*.cs` 内的加载 / 校验规则;插件 `plugin.json` 与 dll 内 `IPaperBodyPlugin` 的所有声明必须一致,否则宿主拒载。

### 产品边界

- 本插件是为 PaperTodo 提供额外能力的**第三方余额监测面板**,不是账户管理平台、计费系统或限流工具。默认不做账号体系、Token 计算、明细导出、配额充值、跨供应商余额汇总、自动告警通道。
- 支持的供应商:**DeepSeek**(`/user/balance`、`/api/v0/usage/...`)与 **MiniMax**(`/v1/api/openplatform/coding_plan/remains`)。**OpenCode Go** 仅在 `plugin.json` 中预留 Key 入口,监视面板返回「尚未适配该供应商」。
- UI 分三层:**胶囊**(常驻可见,1.7 自绘)/ **MiniView**(1.8 边缘预览,MiniMax 双进度条 + DeepSeek 三列卡片)/ **展开后的监视面板**(WebView2)。
- 每张 paper 独立选择供应商(per-paper state);不同纸条可以选不同供应商,但 Key 始终走全局设置。

### 插件协议

- `apiVersion` 跟随宿主当前主版本;宿主拒载不在 `[MinimumPluginApiVersion, SupportedPluginApiVersion]` 区间内的插件。
- `stateVersion` 每次破坏 per-paper state JSON 结构时必须 +1;`IPaperBodyPlugin.MigrateState(stateJson, fromVersion)` 必须为旧版本返回合法 JSON。当前 `2`,旧 `1` 无字段,迁移时一律返回 `"{}"` 触发默认 deepseek 重选。
- `kind: native`,入口必须是单个 `.dll`,且该 dll 内**仅含一个**公开无参 `IPaperBodyPlugin` 实现;`Activator.CreateInstance` 失败、找不到或多个都会被宿主抛 `InvalidDataException`。
- 插件 ID `api.balance.monitor` 必须与目录名同名;插件 ID 是宿主注册表的 key,不可改名,改名会丢弃所有用户设置与 per-paper state。

### 设置与持久化

- 所有设置由宿主根据 `plugin.json` 绘制,**不要在本插件内自绘设置页**。`Settings` 数组必须符合 `PaperBodyPluginRegistry.Settings.cs` 的 `ValidateSettings`:`id` 匹配 `^[A-Za-z0-9._-]{1,80}$`,`type` 仅 `boolean` / `string` / `number` / `select`,`quick` 项最多 3 个,`step > 0`、`min <= max`。
- 设置项 Key 字段(`deepseekApiKey` / `minimaxApiKey` / `opencodeApiKey` / `usageToken`)是用户**明文**输入,本仓库不加密,直接写宿主管理的 `plugins/data/api.balance.monitor.json`。README 与 UI 文案必须保留明文存储的告警。
- per-paper state(`provider`)走 `IPaperBodyContext.SaveStateJson` → 宿主 `PaperBodyPluginDataStore`。
- 全局设置变更由宿主回调 `IPaperBodySession.OnSettingsChanged(string)`,per-paper state 变更由本插件主动调用 `SaveStateJson`;二者不要互相覆盖。
- 旧版单一 `apiKey` 字段仅作 DeepSeek 兼容迁移源,新设置请分别填到 `deepseekApiKey` / `minimaxApiKey` / `opencodeApiKey`。迁移读取时**先读新字段,缺失时回落到 `apiKey`**,不要反向。

### 1.7 胶囊自绘

- 复用实例时通过 `Update(text, ringColor, ringArc)` 原地刷新,不要重新 `SetCapsulePresentation` 触发宿主重建。
- `PaperCapsulePresentation.PreferredWidth` 必须与 `MeasureTextWidth(主题字体 TextBlock.Measure)` 同源,避免 1.6 模板与 1.7 自绘出现亚像素舍入差异导致省略。固定列宽(6 pad + 18 ring + 5 gap + 4 right pad = 33) + 文本宽度 + 0.1 余量。
- `Components` 至少保留 1 项 `Length > 0` 的 `Text` 占位让 `Normalize` 不返回 `null`;但只要返回了 `customView`,宿主就不会用 1.6 模板渲染它们。
- 风险色按 `ComputeRiskRatioForCurrent` 走 v3.1 档位(Safe < 0.5 / Warming < 0.8 / Danger < 1.0 / Overrun ≥ 1.0)。
- 圆环控件 `BalanceProgressRing` 完全 1:1 复刻宿主 `PaperWindow.PluginCapsule.cs` 的 `CapsuleProgressRing`(Pen 粗 2、半径 `max(1, size/2 - 1.5)`、起点 -90° 顺时针、`value ≥ 0.999` 画整圆)。

### 1.8 MiniView

- 尺寸范围 120×90 ~ 480×420 DIP,本插件当前声明 `440×180`。空数据时不要显示更小尺寸;尺寸变化时宿主会重建 MiniView。
- `OnMiniViewVisibilityChanged(bool)` 仅做"暂停 / 恢复"提示;不要重置或重建 MiniView 树。
- 字体覆盖:插件设置 `miniViewFontFamily` 非空时替换主题字体,留空跟随主题;Windows 用字体名(如 `Microsoft YaHei UI`),macOS / Linux 用字形名(如 `PingFang SC` / `Noto Sans CJK SC`)。

### WebView2 监视面板

- C# 端 `BuildViewPayload` 输出必须保持键名稳定(`provider` / `status` / `statusKind` / `balance` / `costToday` / `cost7d` / `todayTokens` / `todayHit` / `cacheRate` / `modelRemains` / `usage` / ...);改键名必须同时改 `web/*.html` 的 `window.__renderBalance`。
- 虚拟主机名 `papertodo.balance.monitor.local` 与 `userDataFolder`(默认 `<pluginDir>/.runtime/webview2`)是本插件私有命名空间,不要复用宿主其他插件使用的虚拟主机名。
- WebView2 渲染进程 `RenderProcessExited` 时调用 `CoreWebView2.Reload()` 恢复;不要在 `ProcessFailed` 里直接 Dispose `_webView`,留给 `Dispose()`。
- `_webViewReady` / `_documentReady` 双闸门:任一未就绪就把最新 payload 缓存到 `_pendingPayload`,`NavigationCompleted` 后补发。
- HTML body 必须**透明背景**,宿主会按主题把便签色垫到 WebView2 之下;不要在 body 上写 `background: white` 或带 alpha 的非透明背景。
- 字体方案优先跟随宿主传入的 `theme.text` / `theme.muted`;`web/monitor.html` 已声明 `--serif` 与 `--sans` 双字族,改字体请保持 Windows / macOS / Linux 三端兜底。

### 状态轮询与并发

- 主轮询 `DispatcherTimer` + `HttpClient`(超时 15s);用 `Interlocked.Exchange(ref _polling, 1)` 串行化。
- DeepSeek 余额 / 用量 / 消费三个接口并行拉取,失败分别落到 `null` 而不是中断整个流程。
- 高峰时段哨兵 `DispatcherTimer`(30 秒)只用于 UTC+8 9-12 / 14-18 切换判断,当前未驱动 UI 渲染,保留 `_lastIsPeakHour` 字段以备后续业务扩展;不要让哨兵触发额外网络请求。

### 与宿主协作

- 宿主 `PaperTodo.Plugin.Abstractions/` 是唯一契约来源;新增能力先看接口是否已暴露,没有再走"等宿主版本"。
- 宿主 `AGENTS.md` 描述胶囊 / 贴边胶囊 / 主题 / 设置页等全局约束;本插件只在这些约束下做"插件侧实现"。
- 接口连通性测试脚本是排错第一站,不要在插件里写网络调试代码。
- 任何对宿主行为有疑问时优先用宿主侧的样例插件(`plugins/official.clock.web`、`plugins/sample.focus-timer.native` 等)对照。

### 更新本文

只有插件协议约束、宿主契约变更、设置项结构调整、构建 / 部署流程变化、跨平台字体约定等需要长期记忆的内容才更新本文。普通 UI 微调、文案、颜色、间距、动画参数、单个 bug 修复不需要同步。
