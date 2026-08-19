# PaperTodo API 余额监测插件

[PaperTodo](https://github.com/snownico0722/PaperTodo) 的**第三方插件**：定时拉取服务商余额接口，在胶囊中显示「风险圆环 + 货币 + 余额 + 可选百分比」，点击展开 HTML 监视面板，**支持 DeepSeek / MiniMax / OpenCode Go 多个供应商**。

本仓库是**独立插件仓库**，只包含插件内容，不含宿主（PaperTodo）代码。

---

## 功能

### 通用

- **多供应商**：DeepSeek / MiniMax / OpenCode Go 三个供应商可切换，**每个胶囊（paper）独立选择**。
- **⚙ 切换**：展开监视面板右上角的 ⚙ 按钮，弹出供应商列表，当前供应商打勾。
- **风险圆环**：胶囊内 ProgressRing 颜色随余额变化（v3.1 算法 `risk = 阈值 / 余额`）
  - `risk ≥ 1.0` → 红色（满圆）
  - `risk ≥ 0.8` → 橙色
  - `risk ≥ 0.5` → 黄色
  - 其余 → 绿色
  - 未配置阈值 → 灰色
- **胶囊文本**：
  - DeepSeek：`¥15.96 · 21%`（货币 + 余额 + 可选百分比）
  - MiniMax：`7% · 3时45分`（当前周期剩余百分比 + 时长）
- **协议 1.7 原生插件**：插件实现 `IPaperCapsuleViewProvider`，胶囊**完全由插件自己渲染**（自定义 Grid + ProgressRing + TextBlock 1:1 复刻宿主 1.6 视觉）；胶囊宽度随内容自适应，长文本不被省略号截断。`SetCapsulePresentation` 仍用于 ToolTip / PlainText / 自动宽度等协议层通道。
- **折叠轮询**：声明 `backgroundUpdates`，插件折叠为胶囊后仍按设定间隔轮询。

### DeepSeek 监视面板

- **顶部**：可用余额圆环 + 末次更新时间 + 状态点
- **三指标卡**：今日消费（金额 + 模型分布 + 较昨日变化）、近 7 日（日均）、今日消耗（Token + 缓存命中数 + 缓存命中率）
- **用量趋势柱状图**：下拉切换时段（今天 / 昨天 / 近 7 天 / 近 30 天 / 本月 / 上月 / 自定义），自定义时段支持按 2 小时分桶或日级
- **主题**：随宿主主题切换（深 / 浅）

### MiniMax 监视面板

- **聚焦 general 模型**（MiniMax Coding Plan 主模型）
- **两个进度条**：
  - **每五小时余额（interval）**：进度条 + 百分比 + foot "距离下次重置还剩有 1小时30分钟"
  - **每周余额（weekly）**：同上；超过 24 小时自动带"天"，如 `3天5小时47分钟`
- **跟随宿主主题**（深 / 浅，衬线杂志风）

### OpenCode Go

- 已预留设置项与代码分支，HTML 面板暂未实现；切换后会显示"尚未适配该供应商"。

---

## 设置项（由宿主插件设置页绘制）

| 项 | 类型 | Quick | 说明 |
| --- | --- | --- | --- |
| `deepseekApiKey` | string |  | DeepSeek API Key（Bearer 鉴权） |
| `minimaxApiKey` | string |  | MiniMax API Key（Bearer 鉴权） |
| `opencodeApiKey` | string |  | OpenCode Go API Key（预留） |
| `usageToken` | string |  | platform.deepseek.com 用量查询 Token（非 API Key）。填写后监视面板显示近 7 天 Token 用量柱状图。 |
| `pollSeconds` | number | ✓ | 刷新间隔（15–3600 秒） |
| `currencySymbol` | select |  | ¥ 人民币 / $ 美元 |
| `balanceThreshold` | number | ✓ | DeepSeek 余额提醒阈值 |
| `showPercentage` | boolean | ✓ | 胶囊是否显示百分比 |

> **Provider 选择不在这里**：v1.2.0 改为 per-paper state，详见下方"多供应商模型"。

> ⚠️ **API Key 本地明文存储**（由宿主写入 `plugins/data/api.balance.monitor.json`）。建议使用各供应商的**只读子 key** 减小泄露影响。

---

## 多供应商模型（per-paper state）

宿主协议下，同一 plugin id 的所有 paper 共享同一份全局 settings，没有"每个胶囊独立设置"的入口。本插件用 **per-paper `StateJson`** 实现"每个胶囊独立选供应商"：

- `plugin.json` 移除全局 `provider` 字段；`stateVersion` 升到 2
- `IPaperBodyPlugin.MigrateState(stateJson, fromVersion)` 把旧 v1 数据迁移为新格式
- C# 端在 `ParseMiniMaxBalanceResponse` 等解析函数中完全使用 `_state.Provider`（per-paper），`OnSettingsChanged` 不再改 provider
- HTML 面板顶部 ⚙ 按钮发送 `{type:"switchProvider", provider:"..."}` 给 C#；C# 校验值 → 写 `SaveStateJson` → 重新加载对应 HTML → 立即重拉
- 各 paper 的 state 存在 `plugins/data/api.balance.monitor.json` 的 `papers[paperId]` 节点，与全局 settings 隔离

**关键代码位置**：
- `ApiBalanceMonitorPlugin.cs` 的 `PaperState` / `ReadState` / `SerializeState` / `SetPaperProvider` / `MigrateState`
- `web/monitor.html` / `web/minimax.html` 的 `.settings-gear` + `.gear-btn` + `.gear-menu`

---

## 依赖宿主协议

插件是宿主协议 1.7 的 native 插件，通过宿主仓库中的 `PaperTodo.Plugin.Abstractions` 项目引用契约（`IPaperBodyPlugin` / `IPaperBodySession` / `PaperCapsulePresentation` 等）。

本仓库假定宿主仓库位于相邻目录：

```
Z:\tool\
├─ PaperTodo\                      # 宿主仓库（非本仓库内容）
│  └─ PaperTodo.Plugin.Abstractions\
└─ PaperTodo.ApiBalanceMonitor\    # ← 本仓库
```

---

## 构建

```powershell
dotnet build -c Release
```

产物：`bin\Release\net10.0-windows10.0.17763.0\win-x64\PaperTodo.Plugin.ApiBalanceMonitor.dll`（含 WebView2 deps.json）

---

## 部署到宿主输出目录

```powershell
powershell -File Sync-PluginsToOutput.ps1
```

脚本会把：
- `plugin.json`
- `web/*.html`（DeepSeek + MiniMax 面板）
- 编译产物（`.dll` + `.deps.json`）

复制到 `..\PaperTodo\输出\PaperTodo-v4.0.0-preview\plugins\api.balance.monitor\`。

> **部署前请先退出 PaperTodo**（宿主运行时 DLL 被锁）。脚本无 -Force；若 DLL 被锁，结束 PaperTodo 后重跑。
> 也可用 `-OutputDir` 覆盖目标路径。

---

## 测试接口连通性

仓库提供两个 PowerShell 脚本，分别实测 DeepSeek / MiniMax 余额接口，作为网络可用性验证（非单元测试）。

**用法**（任选其一）：

```powershell
# 方式 1：从插件设置文件自动读取
powershell -File Test-DeepSeekBalance.ps1
powershell -File Test-MiniMaxCodingPlan.ps1

# 方式 2：通过环境变量（不写入脚本与命令行）
$env:DEEPSEEK_API_KEY = "sk-..."
powershell -File Test-DeepSeekBalance.ps1
```

脚本会从 `..\PaperTodo\输出\PaperTodo-v4.0.0-preview\plugins\data\api.balance.monitor.json` 的 `settings.deepseekApiKey` / `settings.minimaxApiKey` 读取 Key；若不存在则回退到环境变量。**Key 不会出现在命令行参数或脚本文件中**，避免命令历史泄露。

---

## 仓库结构

```
PaperTodo.ApiBalanceMonitor/
├── ApiBalanceMonitorPlugin.cs        # 插件全部 C# 逻辑（约 1616 行）
├── PaperTodo.Plugin.ApiBalanceMonitor.csproj
├── plugin.json                       # 插件清单（apiVersion 1.7 / stateVersion 2）
├── Sync-PluginsToOutput.ps1          # 部署脚本
├── Test-DeepSeekBalance.ps1          # DeepSeek 接口连通性测试
├── Test-MiniMaxCodingPlan.ps1        # MiniMax Coding Plan 接口连通性测试
├── web/
│   ├── monitor.html                  # DeepSeek 面板（深色衬线 + 柱状图）
│   └── minimax.html                  # MiniMax 面板（杂志风，双进度条）
├── .gitignore
└── README.md
```

`bin/`、`obj/`、`.runtime/`（WebView2 user data）由 `.gitignore` 排除。

---

## API 接口说明

| 供应商 | URL | 鉴权 |
| --- | --- | --- |
| DeepSeek 余额 | `https://api.deepseek.com/user/balance` | Bearer API Key |
| DeepSeek 用量 | `https://platform.deepseek.com/api/v0/usage/amount?month=MM&year=YYYY` | x-app-version + Bearer 用量 Token |
| DeepSeek 消费 | `https://platform.deepseek.com/api/v0/usage/cost?month=MM&year=YYYY` | 同上 |
| MiniMax Coding Plan | `https://www.minimaxi.com/v1/api/openplatform/coding_plan/remains` | Bearer API Key |

---

## 版本历史

- **1.2.0** （当前）— per-paper 供应商、⚙ 切换 UI、MiniMax 双进度条面板
- **1.1.0** — DeepSeek / MiniMax 双供应商、HTML 监视面板、缓存命中可视化
- **1.0.0** — DeepSeek 单一余额监测

---

## 许可

仅供学习与个人使用。各供应商 API 的使用需遵守对应服务商的服务条款。
