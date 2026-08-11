# PaperTodo API 余额监测插件

PaperTodo（WPF 便签应用）的**原生插件**：定时拉取 DeepSeek `/user/balance` 接口，在胶囊中显示「风险圆环 + 货币 + 余额 + 可选百分比」。

本仓库是**独立插件仓库**，只包含插件内容，不含宿主（PaperTodo）代码。

## 功能

- 固定请求 `https://api.deepseek.com/user/balance`，Bearer 鉴权（API Key 本地明文存储，建议使用只读子 key）。
- 风险圆环：`risk = 阈值 / 余额`
  - `risk ≥ 1.0` → 红色（满圆）
  - `risk ≥ 0.8` → 橙色
  - `risk ≥ 0.5` → 黄色
  - 其余 → 绿色
  - 未配置阈值 → 灰色
- 胶囊文本：`¥15.96 56%`（余额 + 百分比，可关闭）。
- **Protocol 1.7 自定义胶囊视图**：插件实现 `IPaperCapsuleViewProvider`，由插件自绘圆环与文本，胶囊宽度随内容自适应，长文本不再被宿主省略号截断；1.6 的 `PaperCapsulePresentation` 仍作为兜底。

## 设置项（由宿主插件设置页绘制）

| 项 | 类型 | 说明 |
| --- | --- | --- |
| `apiKey` | string | Bearer API Key |
| `pollSeconds` | number | 刷新间隔（15–3600 秒） |
| `currencySymbol` | select | ¥ 人民币 / $ 美元 |
| `balanceThreshold` | number | 余额提醒阈值 |
| `showPercentage` | boolean | 是否显示百分比 |

## 依赖宿主协议

插件依赖宿主仓库中的 `PaperTodo.Plugin.Abstractions`（协议契约）。本仓库假定宿主仓库位于相邻目录：

```
Z:\tool\
├─ PaperTodo\                      # 宿主仓库（非本仓库内容）
│  └─ PaperTodo.Plugin.Abstractions\
└─ PaperTodo.ApiBalanceMonitor\    # ← 本仓库
```

## 构建

```powershell
dotnet build -c Release
```

产物：`bin\Release\net10.0-windows\PaperTodo.Plugin.ApiBalanceMonitor.dll`

## 部署到宿主输出目录

```powershell
powershell -File Sync-PluginsToOutput.ps1
```

脚本会把仓库根的 `plugin.json` 与编译产物（dll / deps.json）复制到
`..\PaperTodo\输出\PaperTodo-v4.0.0-preview\plugins\api.balance.monitor\`。
也可用 `-OutputDir` 覆盖目标路径。部署前请先退出 PaperTodo，部署后重启生效。

## 测试接口连通性

`Test-DeepSeekBalance.ps1` 用配置的 API Key 实测 DeepSeek 余额接口（网络可用性验证，非单元测试）。
