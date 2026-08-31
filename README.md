# PaperTodo API 余额监测插件

[PaperTodo](https://github.com/snownico0722/PaperTodo) 的第三方插件,把 API 服务商的**余额 / 用量额度 / Claude Code hook 活动**拉到桌面。插件按 PaperTodo 的"胶囊 / MiniView / 监视面板"三层协议分层渲染:

- **胶囊**(1.7 自绘)— paper 折叠成胶囊时,留在屏幕上一直可见
- **MiniView**(1.8 自绘)— paper 悬停预览时,在屏幕边缘露出的小卡
- **监视面板**(WebView2)— 点胶囊展开后显示的完整 HTML 面板

> 当前版本 **1.2.0**(API 协议 **2.0**)。已支持 **DeepSeek / MiniMax / Kimi For Coding / ZhiPu GLM** 四个供应商;OpenCode Go / MiMo / CodeX 已预留 Key 入口,监视面板暂未适配。

---

## 它能做什么

### 胶囊视图(常驻可见)

胶囊协议 1.7 完全自绘 WPF,圆环 + 数字 + 状态点共占约 92×46 DIP,paper 折叠成胶囊后仍按"刷新间隔"轮询。

- **状态指示**:
  - DeepSeek:风险圆环 + 余额数字(`¥15.96` 或 `21% · ¥15.96`,百分比可在设置里关)
  - MiniMax / Kimi / ZhiPu:剩余百分比 + 当前周期重置倒计时(`7% · 3时45分`)
- **风险色**:余额越低颜色越红,跟着阈值走;阈值未设置时显示灰色。设置里可一键关闭圆环(`disableRing`),仅保留文字。
- **hook overlay**:Claude Code 触发 `PreToolUse` / `PostToolUse` 时,胶囊临时覆盖一个旋转沙漏或工具名;`Stop` / `PermissionRequest` / `PostToolUseFailure` 时覆盖一个对勾 / 问号 / 红叉,默认 8 秒后恢复余额。

![1787153056075](image/README/1787153056075.png)

### MiniView(悬停预览)

paper 悬停在屏幕边缘时,在 pill 形状里露出比胶囊更完整的快照。完全自绘 WPF,按供应商区分默认尺寸:

| 供应商 | 默认尺寸 |
|--------|---------|
| DeepSeek | 322 × 257(今日消费 / 近 7 日 / 今日消耗 3 行 + footer) |
| MiniMax / Kimi / ZhiPu / OpenCode / MiMo / CodeX | 280 × 180(每五小时 / 每周 双进度条) |

字体可被 `MiniView 字体` 设置覆盖(留空跟随主题)。

![1787153149187](image/README/1787153149187.png)

### 监视面板(点胶囊展开)

WebView2 渲染 `web/monitor.html` 或 `web/minimax.html`,与 paper body 容器等比缩放。笔记纸默认尺寸(`NoteDefaultWidth = 320 - 16 chrome = 304 DIP 宽`)下,zoom = 1.0×,三段全部默认展开;Todo 纸 / 大尺寸 paper 时按 `innerWidth / 304` 等比放大,钳制到 `[0.85, 1.5]×`。

**DeepSeek**(面板标题 "API 余额监测"):

- 顶部:**余额数字**(30px serif)+ 在线状态点 + 末次更新时间 + ⚙ 供应商切换
- **三张指标卡**(等宽三列,各 ~95px):
  - 今日消费金额(含模型分布 + 较昨日 ↑/↓/→ 箭头)
  - 近 7 日(日均)
  - 今日消耗(Token 数量 / 缓存命中 / 命中率)
- **用量趋势柱状图**:高度 105px,可下拉切换 今天 / 昨天 / 近 7 天 / 近 30 天 / 本月 / 上月 / 自定义时段;>15 天自动压缩柱宽、抽稀横轴标签
- **Claude Code 活动流**(数据到达时显示):最近 5 条 hook 事件摘要 + 时间戳

**MiniMax / Kimi / ZhiPu**(面板标题 "Coding Plan"):

- 顶部:Coding Plan 标题(22px serif)+ 状态点 + ⚙ 切换
- **两根进度条**:每五小时余额(interval)+ 每周余额(weekly),各自显示百分比 + 倒计时;风险 < 30% 时进度条变橙
- **Claude Code 活动流**(同 DeepSeek)

每个 section header 都可以**点击收起 / 展开**,折叠状态写 `localStorage`(`paperTodo.balanceMonitor.collapse`),跨 paper / 跨 provider / 重启 PaperTodo 都保留。`hooks` 段不受折叠影响,由数据驱动(`hooks = []` 时整段 `hidden`)。

**多供应商切换**:面板右上角的 ⚙ 按钮,弹出 DeepSeek / MiniMax / Kimi / ZhiPu / OpenCode 列表,当前供应商打勾,点一下切换并立即重拉数据。**每个 paper 独立选供应商**(per-paper state)。

**跟随主题**:浅色 / 深色自动切换,body 透明背景让宿主便签色透出。

![1787153181991](image/README/1787153181991.png)

### Claude Code hook 集成

插件在本地 `127.0.0.1:17890`(端口冲突时自动 +1 ~ +4 重试)启动一个 HTTP 服务器,接收 Claude Code 的生命周期事件转推。可在 plugin.json 设置里独立开关以下 7 个事件:

| 事件 | 行为 |
|------|------|
| `UserPromptSubmit` | hook overlay 转沙漏(收到用户提示) |
| `PreToolUse` | hook overlay 转沙漏 + 显示工具名(如 `Edit` / `Bash` / `WebFetch`) |
| `PostToolUse` | hook overlay 转沙漏 + 显示工具名 |
| `PostToolUseFailure` | 覆盖红叉 + "✗ 执行异常",8 秒倒计时 |
| `PermissionRequest` | 覆盖黄色问号 + "等待用户回应",8 秒倒计时 |
| `Stop` | 覆盖绿色对勾 + "✓ 任务完成",8 秒倒计时 |
| `StopFailure` / `SessionNot` / `Notification` | 不渲染 overlay,只进入活动流 |

胶囊临时 overlay 用 `HookOverlayController` 状态机管理,避免动画并存与后台线程并发崩溃。

安装方法见 `README` 下半节"Claude Code hook 安装"。

---

## 快速上手

1. **首次使用**:新建一张 PaperTodo 纸条,正文类型选「API 余额监测」。
2. **填 API Key**:右键纸条 → 插件设置,在「DeepSeek API Key」一栏粘贴 `sk-…`(DeepSeek 供应商)或在「MiniMax API Key」一栏粘贴 MiniMax Key。**每个供应商的 Key 独立**,不互相复用。留空则跳过对应供应商。
3. **等首次轮询**:胶囊会出现数字和圆环。展开查看完整面板。
4. **切换供应商**:点开监视面板右上角的 ⚙,从下拉里选目标供应商。**切换是按"每张纸"独立记录的**,不同纸条可以选不同的供应商。
6. **(可选)Claude Code hook**:在 `~/.claude/settings.json` 启用 hook,本地 127.0.0.1:17890 即可收到事件。详见下文。

> 💡 DeepSeek 想看今日 Token / 近 7 日消费图表,还需要填「用量 Token」,这是 [platform.deepseek.com](https://platform.deepseek.com) 的用量查询 Token,**不是** API Key。
你需要在[platform.deepseek.com](https://platform.deepseek.com) 中登录你的账户,在页面中点击F12打开开发者工具,在控制台中输入 `JSON.parse(localStorage.userToken).value`,将输出的字符串粘贴到设置中.

  ![1787152926075](image/README/1787152926075.png)

---

## 设置项

由 PaperTodo 宿主绘制设置页。所有 Key **本地明文存储**(`plugins/data/api.balance.monitor.json`),请自行评估风险。

### 通用

| 设置项 | 类型 | 默认 | 说明 |
| --- | --- | --- | --- |
| 刷新间隔 | number | 60 秒 | 15–3600,胶囊折叠后仍按此间隔轮询 |
| 关闭圆环 | boolean | 关 | 勾选后胶囊不再显示圆环指示器,仅保留文字;MiniView 5h/周进度条不受影响 |
| 关闭圆点呼吸动效 | boolean | 关 | DeepSeek 胶囊在高峰期不再触发圆点呼吸动效;圆点仍按风险档位静态显示橙色 |
| MiniView 字体 | string | 空 | 覆盖 MiniView 字体,留空跟随主题;Windows 填字体名(如 `Microsoft YaHei UI`),macOS / Linux 填字形名(如 `PingFang SC` / `Noto Sans CJK SC`) |

### 供应商 Key

| 设置项 | 说明 |
| --- | --- |
| DeepSeek API Key | Bearer 鉴权,只用于 DeepSeek 供应商 |
| DeepSeek 用量查询 Token(可选) | platform.deepseek.com 的用量查询 Token,非 API Key;填写后 DeepSeek 面板显示 Token / 消费柱状图 |
| MiniMax API Key | Bearer 鉴权,只用于 MiniMax 供应商 |
| ZhiPu GLM API Key | 国际版从 z.ai 控制台获取;国内版从 open.bigmodel.cn 获取 |
| ZhiPu 区域 | select,国际版 / 国内版 |
| ZhiPu 套餐类型 | select,影响配额面板字段 |
| Kimi For Coding API Key | platform.kimi.com 控制台获取 |
| OpenCode Go API Key | 开发中,面板暂未适配 |
| MiMo API Key | 开发中,面板暂未适配 |
| CodeX API Key | 开发中,面板暂未适配 |

### Claude Code hook

| 设置项 | 默认 | 说明 |
| --- | --- | --- |
| hook 端口 | 17890 | 端口冲突时自动 +1 ~ +4 重试 |
| overlay 持续秒数 | 8 | Color overlay(`Stop` / `PermissionRequest` / `PostToolUseFailure`)的展示秒数,倒计时到时自动恢复余额 |
| 收到用户提示时通知 | 开 | `UserPromptSubmit` 是否进活动流 |
| 工具调用前通知 | 开 | `PreToolUse` 是否进活动流 + overlay |
| 工具调用后通知 | 开 | `PostToolUse` 是否进活动流 + overlay |
| 工具权限请求通知 | 开 | `PermissionRequest` 是否进活动流 + overlay |
| 工具调用失败通知 | 开 | `PostToolUseFailure` 是否进活动流 + overlay |

---

## Claude Code hook 安装

把以下配置加到 `~/.claude/settings.json`(Windows 上是 `C:\Users\<你>\.claude\settings.json`):

```json
{
  "hooks": {
    "UserPromptSubmit":   [{ "type": "http", "url": "http://127.0.0.1:17890/hook/", "timeout": 200 }],
    "PreToolUse":         [{ "type": "http", "url": "http://127.0.0.1:17890/hook/", "timeout": 200 }],
    "PostToolUse":        [{ "type": "http", "url": "http://127.0.0.1:17890/hook/", "timeout": 200 }],
    "PostToolUseFailure": [{ "type": "http", "url": "http://127.0.0.1:17890/hook/", "timeout": 200 }],
    "PermissionRequest":  [{ "type": "http", "url": "http://127.0.0.1:17890/hook/", "timeout": 200 }]
  }
}
```

Claude Code 会把每个事件以 JSON POST 到 `127.0.0.1:17890/hook/`,插件收到后:
1. 写入本地滑动窗口(最近 5 条)
2. 推送给 WebView 监视面板的活动流
3. 触发胶囊 overlay(`Stop` / `PermissionRequest` / `PostToolUseFailure` 显示静态图标,`PreToolUse` / `PostToolUse` 显示旋转沙漏 + 工具名)

事件格式参考 `scripts/notify-paper-todo.js`。

---

## 接口连通性测试

仓库附带两个 PowerShell 脚本,用来**只测网络和 Key 是否有效**(不会改 PaperTodo 的任何状态),便于排错:

```powershell
# 从插件数据文件读取 Key;不存在则回退到环境变量
powershell -File Test-DeepSeekBalance.ps1
powershell -File Test-MiniMaxCodingPlan.ps1

# 或者通过环境变量传入,避免命令历史泄露
$env:DEEPSEEK_API_KEY = "sk-..."
powershell -File Test-DeepSeekBalance.ps1
```

---

## 已对接的 API

| 供应商 | 用途 | 地址 |
| --- | --- | --- |
| DeepSeek | 余额 | `https://api.deepseek.com/user/balance` |
| DeepSeek | 用量(可选) | `https://platform.deepseek.com/api/v0/usage/amount` |
| DeepSeek | 消费(可选) | `https://platform.deepseek.com/api/v0/usage/cost` |
| MiniMax | Coding Plan 余额 | `https://www.minimaxi.com/v1/api/openplatform/coding_plan/remains` |
| ZhiPu GLM | Coding Plan 余额 | 国内 / 国际版区分 |
| Kimi For Coding | Coding Plan 余额 | `platform.kimi.com` |

---

## 常见问题

**胶囊一直显示"尚未拉取"?**
- 检查 Key 是否填对(DeepSeek 是 `sk-…` 开头,MiniMax 单独签发)。
- 检查网络是否能访问对应接口(可用上方测试脚本验证)。
- 折叠胶囊是否超过 60 秒?可以右键 → 设置 → 调小"刷新间隔"。

**DeepSeek 面板没有 Token / 消费图表?**
- 「用量 Token」没填,或 Token 已过期。这个 Token 在 [platform.deepseek.com](https://platform.deepseek.com) 的用量页签发,**不是** API Key**。

**切换供应商后没反应?**
- ⚙ 切换是按"每张纸"独立记录的。如果面板是 HTML 重载后还没拉数据,等一个轮询周期即可。
- 切换到 OpenCode / MiMo / CodeX 会回到「尚未适配该供应商」状态,这是正常的(面板开发中)。

**Key 是明文存储,安全吗?**
- 本插件不加密,文件落在 PaperTodo 的 `plugins/data/api.balance.monitor.json` 下,本机用户权限即可读取。**请勿在共享电脑使用,或自行评估是否接受此风险**。

**Claude Code hook 没收到事件?**
- 检查插件端口(默认 17890)是否被防火墙拦截(loopback 应不受影响)。
- 检查 hook 设置里事件订阅开关是否全开。
- 在 PowerShell 里 `curl http://127.0.0.1:17890/hook/` 测试 HTTP 服务器是否在线;应该返回 `{"ok": true, "port": 17890}`。

---

## 版本历史

- **1.2.0**(当前)— Kimi / ZhiPu 接入、Capsule hook overlay、`HookOverlayController` 状态机、MiniView 1.8 协议、笔记纸默认尺寸适配(zoom 1.0× + 折叠默认展开)、WebView2 等比缩放
- **1.1.0** — DeepSeek / MiniMax 双供应商、HTML 监视面板、缓存命中可视化
- **1.0.0** — DeepSeek 单一余额监测

---

## 许可

仅供学习与个人使用。各供应商 API 的使用需遵守对应服务商的服务条款。