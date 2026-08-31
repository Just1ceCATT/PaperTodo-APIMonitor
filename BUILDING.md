# 编译与部署

本指南面向"从源码构建本插件并部署到 PaperTodo 宿主"的开发者。读者应已经会基础 `git` / `dotnet` 命令,不假设熟悉 PaperTodo 协议。

> **阅读路径**:
> - 用户(只想用)→ 请看 `README.md`(中文用户文档)
> - AI 代理 / 维护者理解代码约束 → 请看 `AGENTS.md`(含技术栈、目录结构、版本号约定等)
> - 开发者想"从源码到上线" → 即本文档

---

## 0. 心智模型

本插件是 PaperTodo 的"原生 native 插件",以 **WPF 类库**形式编译,产物是一个 .NET 程序集 (`PaperTodo.Plugin.ApiBalanceMonitor.dll`),由宿主 PaperTodo.exe 在运行时反射加载。它**不**独立运行,必须配合 PaperTodo 主程序才能看到 UI。

部署本质:把 编译产物 + 资源文件 + `plugin.json` 拷贝到 PaperTodo 的 `plugins/<plugin-id>/` 目录,然后重启 PaperTodo。

宿主的 `AGENTS.md` 描述整体约束;本文聚焦**步骤本身**。

---

## 1. 准备工作

### 1.1 安装 .NET SDK

本插件的 `TargetFramework` 是 `net10.0-windows10.0.17763.0`。需要 **.NET 10 SDK**(Preview 不可用,需正式版)。

```powershell
dotnet --list-sdks
```

期望看到形如 `10.0.3xx [...]` 的一行。本仓库已在 .NET 10 GA 后编译运行;更低版本会因 API 缺失失败。

> 若不想装 SDK、只想跑已编译的 dll:见第 6 节"用户侧部署",本文档后续以"开发者从源码构建"为主线。

### 1.2 克隆两个仓库

主 csproj 第 24 行有这条 ProjectReference:

```xml
<ProjectReference Include="..\PaperTodo\PaperTodo.Plugin.Abstractions\PaperTodo.Plugin.Abstractions.csproj" />
```

也就是说:**你必须把 PaperTodo 宿主仓库克隆到与本仓库并列的目录**,否则会编译失败。

```
Z:\tool\
├── PaperTodo.ApiBalanceMonitor\    <-- 本仓库
└── PaperTodo\                       <-- 宿主仓库(必须存在)
```

```powershell
cd Z:\tool

# 已经 clone 跳过这一步
git clone https://github.com/snownico0722/PaperTodo.git
git clone https://github.com/<your-fork>/PaperTodo.ApiBalanceMonitor.git
```

> 如果你不打算自己编译 `PaperTodo.Plugin.Abstractions`(比如用 NuGet 发布版),修改主 csproj 第 24 行为 `<Reference>` 指向预编译 dll;详见第 7 节"故障排查"。

---

## 2. 项目结构速览

完整目录树见 `AGENTS.md` 第二节。这里只摘关键文件:

| 路径 | 作用 |
|---|---|
| `PaperTodo.Plugin.ApiBalanceMonitor.csproj` | 主工程(类库, `UseWPF=true`, `SelfContained=false`) |
| `plugin.json` | 插件元数据 + 设置项 + 入口 dll 名,**会被复制到部署目录** |
| `web/monitor.html` / `web/minimax.html` | 监视面板的 HTML,**会随 web/ 目录整体部署** |
| `ApiBalanceMonitorPlugin.cs` | 插件入口(`IPaperBodyPlugin` 实现) |
| `Tests/*.csproj` | 纯逻辑测试(无 WPF 依赖,纯 `net10.0`) |
| `LoadTests/*.csproj` | 并发 / STA 压测(依赖 WPF, `net10.0-windows`) |
| `Sync-PluginsToOutput.ps1` | **部署脚本** —— 把编译产物 + 资源拷到宿主输出目录 |
| `.start_host.ps1` / `.restart_host.ps1` | 启动 PaperTodo.exe 的辅助脚本 |
| `Test-DeepSeekBalance.ps1` / `Test-MiniMaxCodingPlan.ps1` | 只测 API 连通性 / Key 有效性的脚本 |

---

## 3. 编译

> 后续所有步骤的"编译产物路径"都默认 `bin/<Config>/net10.0-windows10.0.17763.0/win-x64/`。

### 3.1 Debug 构建(日常开发)

```powershell
cd Z:\tool\PaperTodo.ApiBalanceMonitor
dotnet build -c Debug
```

产物示例:

```
bin\Debug\net10.0-windows10.0.17763.0\win-x64\
├── PaperTodo.Plugin.ApiBalanceMonitor.dll       <-- 入口 dll
├── PaperTodo.Plugin.ApiBalanceMonitor.deps.json
├── PaperTodo.Plugin.Abstractions.dll            <-- 来自宿主仓库编译
├── Microsoft.Web.WebView2.{Core,Wpf,WinForms}.dll
└── ... (其他 WebView2 native runtime dll)
```

### 3.2 Release 构建(发布给用户)

```powershell
dotnet build -c Release
```

> ⚠️ **注意**:csproj 第 13-16 行 Release 配置禁用了 PDB(`DebugType=none`)。Release 模式下崩溃没有栈追踪——出问题时临时改成 `DebugType=embedded`(把 pdb 嵌入 dll),出 bug 跟栈后改回 `none` 再发布。

### 3.3 自带运行时发布(可选,免用户装 .NET 10)

发布成 self-contained 单文件可执行包。**用户机器不需要装 .NET Desktop Runtime**,但 dll 不能用了,必须改 exe 入口——本插件目前不是这种形态,**留给将来 PaperTodo 协议支持 exe plugin 入口时使用**。

```powershell
# 仅作参考,目前不会生效
dotnet publish -c Release -r win-x64 --self-contained true ^
  -o publish\sc ^
  /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true
```

### 3.4 跑测试

```powershell
# 纯逻辑(快速)
dotnet test Tests\PaperTodo.Plugin.ApiBalanceMonitor.Tests.csproj

# WPF / STA(慢,真实环境回归)
dotnet test LoadTests\PaperTodo.Plugin.ApiBalanceMonitor.LoadTests.csproj
```

Tests 工程**链接源文件**(`<Compile Include="..\Models\BalanceSample.cs" Link="BalanceSample.cs" />`),不引入主工程依赖,避免 WPF / Windows-only 污染纯逻辑测试。维护新单元测试时:**只测无 WPF 引用的纯逻辑类**,否则会破坏这层隔离。

---

## 4. 部署

部署 = 把编译产物 + `plugin.json` + `web/` 拷到宿主 `plugins/<id>/` 目录。

**推荐:用仓库自带的部署脚本** `Sync-PluginsToOutput.ps1`。

### 4.1 标准部署(默认路径)

```powershell
# 必须先 build(脚本会从 bin/Release/.../win-x64 找产物)
dotnet build -c Release

# 默认目标:../PaperTodo/输出/PaperTodo-v4.0.0-preview/plugins/api.balance.monitor/
powershell -File Sync-PluginsToOutput.ps1
```

脚本会:
- 拷 `plugin.json` 到目标目录
- 整目录拷 `web/`
- 从 `bin/Release/.../win-x64/` 只挑 `PaperTodo.Plugin.ApiBalanceMonitor.dll` 和 `.deps.json` 拷过去(宿主 dll / WebView2 dll 由宿主解析回落,**不**随插件拷贝)

### 4.2 自定义目标路径

PaperTodo 输出目录因人而异(用户脚本里写死的是 `输出\PaperTodo-v4.0.0-preview`)。可用 `-OutputDir` 覆盖:

```powershell
powershell -File Sync-PluginsToOutput.ps1 -OutputDir "D:\path\to\PaperTodo-v4.0.0-preview"
```

### 4.3 手动部署(不推荐)

```powershell
$src = "Z:\tool\PaperTodo.ApiBalanceMonitor"
$dst = "Z:\tool\PaperTodo\输出\PaperTodo-v4.0.0-preview\plugins\api.balance.monitor"
New-Item -ItemType Directory -Force -Path $dst

# plugin.json
Copy-Item -Force "$src\plugin.json" "$dst\plugin.json"

# web/ 整目录
Copy-Item -Recurse -Force "$src\web" "$dst\web"

# 编译产物(只挑入口 dll + deps.json)
Copy-Item -Force "$src\bin\Release\net10.0-windows10.0.17763.0\win-x64\PaperTodo.Plugin.ApiBalanceMonitor.dll" "$dst\"
Copy-Item -Force "$src\bin\Release\net10.0-windows10.0.17763.0\win-x64\PaperTodo.Plugin.ApiBalanceMonitor.deps.json" "$dst\"
```

部署完最终目录:

```
<PaperTodo>\plugins\api.balance.monitor\
├── plugin.json
├── PaperTodo.Plugin.ApiBalanceMonitor.dll
├── PaperTodo.Plugin.ApiBalanceMonitor.deps.json
└── web\
    ├── monitor.html
    └── minimax.html
```

> 宿主会按需回落其他 dll(WebView2 / PaperTodo.Plugin.Abstractions)到宿主自己的副本,**不要**把这些也拷进插件目录。

### 4.4 开发期联接(可选,改一行就刷新)

不想每次 build 都手动拷?在 PaperTodo 插件目录建一个**目录联接**(junction)指向 `bin\Debug\...`:

```powershell
$dst = "Z:\tool\PaperTodo\输出\PaperTodo-v4.0.0-preview\plugins\api.balance.monitor"
$src = "Z:\tool\PaperTodo.ApiBalanceMonitor\bin\Debug\net10.0-windows10.0.17763.0\win-x64"

# Junction 是双向软链,删 dst 时源目录不会被删
New-Item -ItemType Junction -Path $dst -Target $src
```

之后 `dotnet build -c Debug` 完,只需要把 `plugin.json` + `web/` 同步过去(联接不会同步这些,它们不在 bin 下)即可:

```powershell
powershell -File Sync-PluginsToOutput.ps1
```

> Junction 切到 Release 时要先 `Remove-Item $dst`(只删联接,不删源)再建。

---

## 5. 启动宿主 + 验证

### 5.1 启动宿主

```powershell
# 仓库自带的启动脚本(写死了路径,你可能需要改)
.\.start_host.ps1

# 或直接:
Start-Process "Z:\tool\PaperTodo\输出\PaperTodo-v4.0.0-preview\PaperTodo.exe" `
  -WorkingDirectory "Z:\tool\PaperTodo\输出\PaperTodo-v4.0.0-preview"
```

启动后:
1. 新建一张 PaperTodo 纸条
2. 正文类型选「API 余额监测」
3. 右键纸条 → 插件设置 → 填对应供应商的 API Key

### 5.2 验证 HTTP 服务器(Claude Code hook)

本插件启动时会在 `127.0.0.1:17890` 启动一个 HTTP 服务器(端口可在设置里改)。

```powershell
# 健康检查
curl http://127.0.0.1:17890/hook/
# 期望: { "ok": true, "port": 17890 }

# 模拟一次 hook 推送(Stop 事件)
curl -X POST -H 'Content-Type: application/json' `
  -d '{"hook_event_name":"Stop","summary":"测试"}' `
  http://127.0.0.1:17890/hook/
# 期望: 胶囊 3 秒内出现绿色对勾 + "✓ 任务完成"
```

### 5.3 验证供应商 API

不要在 PaperTodo 里反复试 Key 是否有效,直接用仓库自带的接口测试脚本:

```powershell
# 从插件数据文件读 Key;不存在则回退到环境变量
powershell -File Test-DeepSeekBalance.ps1
powershell -File Test-MiniMaxCodingPlan.ps1

# 或者通过环境变量传入,避免命令历史泄露
$env:DEEPSEEK_API_KEY = "sk-..."
powershell -File Test-DeepSeekBalance.ps1
```

脚本**只测网络和 Key**,不会触发 PaperTodo 任何状态变化。

### 5.4 接入 Claude Code hook

编辑 `~/.claude/settings.json`(Windows: `C:\Users\<你>\.claude\settings.json`),加 hooks 配置。具体格式和事件清单见 README.md 第 143-167 行。

---

## 6. 用户侧部署(只装不编)

下游用户拿到本插件的 dll 后,**需要 PaperTodo 主程序和 .NET 10 Desktop Runtime**:

1. 安装 [PaperTodo](https://github.com/snownico0722/PaperTodo)(按其官方步骤)
2. 安装 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)(选 Desktop Runtime,不是 SDK 也不是 ASP.NET)
3. 拿到一份 `api.balance.monitor.zip`,结构如下:
   ```
   api.balance.monitor\
   ├── plugin.json
   ├── PaperTodo.Plugin.ApiBalanceMonitor.dll
   ├── PaperTodo.Plugin.ApiBalanceMonitor.deps.json
   └── web\
       ├── monitor.html
       └── minimax.html
   ```
4. 解压整个目录到 PaperTodo 的 `plugins/` 下,重启 PaperTodo 即可。

> **WebView2 Runtime**:Windows 10 1809+ / Windows 11 一般已自带;若用户报告"监视面板白屏",装 [Evergreen WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)。

---

## 7. 故障排查

| 现象 | 可能原因 | 解决 |
|---|---|---|
| `error CS0234: type 'PaperTodo.Plugin.Abstractions' does not exist` | 缺宿主仓库 | 克隆到同级 `Z:\tool\PaperTodo\`,或改 csproj 引用为 NuGet / 预编译 dll |
| `The current SDK does not support targeting .NET 10` | 装的 SDK 太旧 | 装 .NET 10 SDK |
| `Build succeeded;PaperTodo 启动后看不到插件` | plugin.json 没拷 / 路径错 | 用 `Sync-PluginsToOutput.ps1` 重新部署 |
| `监视面板白屏` | 缺 `web/` 目录 / 缺 WebView2 Runtime | 重新部署整个 `web/` 子目录;装 Evergreen WebView2 Runtime |
| `Module not found: Microsoft.Web.WebView2.Wpf.dll` | 插件目录漏掉宿主回落 dll | 不要自己拷 WebView2 dll,宿主会回落自己的副本 |
| `port 17890 already in use` | 端口被占 | 设置 `hooksPort` 到 1024-65535 任意空闲端口;或停占端口的程序 |
| `Release 崩溃无栈` | Release 禁了 PDB | 临时改 `DebugType=embedded`,或编 Debug 复现 |
| `Key 改了不生效` | 设置改动后未重启 PaperTodo | 设项改动**需要重启** PaperTodo 才生效(部分项动态生效) |
| `Hook 事件没收到` | Claude Code 没发 / 端口被拦 | `curl http://127.0.0.1:17890/hook/`;检查 `~/.claude/settings.json` 是否启用对应事件 |
| `依赖 PaperTodo.Plugin.Abstractions 的版本不一致` | 宿主和插件构建在不同 commit | 重新 `dotnet restore` 后构建;或固定 Abstractions dll 版本 |

---

## 8. 日常开发循环(改一行就生效)

1. 改代码
2. `dotnet build -c Debug`(也可 `dotnet watch build` 自动重编)
3. `Sync-PluginsToOutput.ps1`(或开发期 junction 让 dll 自动同步)
4. `.restart_host.ps1` 重启 PaperTodo

把上面 4 步用 `psake` / `npm-run-all` 之类的串起来,可以做成一键脚本;但最初版直接 PowerShell 命令行循环已经够用。

---

## 9. 相关文档与脚本索引

| 文件 | 用途 |
|---|---|
| `README.md` | 用户向中文文档:功能介绍、使用、设置项、Claude Code hook 接入 |
| `AGENTS.md` | 维护者向技术约束:技术栈、目录、版本号、宿主程序集回落清单 |
| `plugin.json` | 插件元数据 + 22 条设置项描述 |
| `Sync-PluginsToOutput.ps1` | 一键部署(默认目标 ../PaperTodo/输出/...) |
| `.start_host.ps1` / `.restart_host.ps1` | 启动宿主(写死路径,可能要改) |
| `Test-DeepSeekBalance.ps1` / `Test-MiniMaxCodingPlan.ps1` | 接口连通性测试,只测 Key + 网络,不动 PaperTodo |

---

## 10. 版本号约定

`AGENTS.md` 第一段已经写明:**`PaperTodo.Plugin.ApiBalanceMonitor.csproj` 的 `<Version>` 和 `plugin.json` 的 `version` 必须保持一致,任何 release 之前手动同步**。

本文档发布时快照到的版本:
- `csproj` `<Version>` —— 1.1.0
- `plugin.json` `version` —— 1.3.0
- `plugin.json` `apiVersion` —— 2.1
- README 显示的版本 —— 1.2.0

> ⚠️ 这三个数字当前**不一致**,已在 `AGENTS.md` 未列入的"todo 列表"中。改版前请优先同步,再编译部署。
