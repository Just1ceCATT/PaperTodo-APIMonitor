#!/usr/bin/env node
// Claude Code hook 转发脚本 → PaperTodo 余额监测插件 HTTP 端点
//
// 用途：在 ~/.claude/settings.json 配置 hook，把 Claude Code 生命周期事件
// 转推到本插件的 HttpListener（默认 http://127.0.0.1:17890/hook）。
//
// 时延：loopback HTTP + 本地 JSON 解析，典型 5-15ms。
//
// 安装：
// 1. 保存为 ~/.claude/hooks/notify-paper-todo.js
// 2. chmod +x ~/.claude/hooks/notify-paper-todo.js
// 3. 在 ~/.claude/settings.json 添加 hook 配置（见 README）

const http = require("http");

// 端口可通过环境变量覆盖；默认 17890 与插件默认一致。
const PORT = parseInt(process.env.PAPERTODO_HOOK_PORT || "17890", 10);
const HOST = "127.0.0.1";
const PATH = "/hook/";
const TIMEOUT_MS = 200;

// 从 stdin 读 Claude Code 的 hook JSON（约定格式）
let input = "";
process.stdin.setEncoding("utf8");
process.stdin.on("data", (chunk) => { input += chunk; });
process.stdin.on("end", () => {
  let hookEventName = "";
  let toolName = null;
  let summary = "";
  try {
    const obj = JSON.parse(input);
    hookEventName = obj.hook_event_name || "";
    toolName = obj.tool_name || null;

    // 合成简短 summary，避免插件再做翻译。
    switch (hookEventName) {
      case "PostToolUse":
        summary = `Claude: ${toolName || "Tool"} 调用完成`;
        break;
      case "PreToolUse":
        summary = `Claude: 即将调用 ${toolName || "Tool"}`;
        break;
      case "UserPromptSubmit":
        summary = "Claude: 收到用户提示";
        break;
      case "Stop":
        summary = "Claude: 已停止响应";
        break;
      case "StopFailure":
        summary = "Claude: 响应异常中止";
        break;
      case "Notification":
        summary = "Claude: 需要注意";
        break;
      case "SessionStart":
        summary = "Claude: 会话启动";
        break;
      case "SessionEnd":
        summary = "Claude: 会话结束";
        break;
      default:
        summary = `Claude: ${hookEventName}`;
    }
  } catch (e) {
    // JSON 解析失败也尝试转发，让插件决定如何处理。
    hookEventName = "Unknown";
    summary = `Hook 解析失败: ${e.message}`;
  }

  const body = JSON.stringify({
    hook_event_name: hookEventName,
    tool_name: toolName,
    summary: summary
  });

  const req = http.request({
    host: HOST,
    port: PORT,
    path: PATH,
    method: "POST",
    timeout: TIMEOUT_MS,
    headers: {
      "Content-Type": "application/json",
      "Content-Length": Buffer.byteLength(body)
    }
  }, (res) => {
    // 收到 200 后立即退出；不读响应体（很小且不需要）。
    res.resume();
    process.exit(0);
  });

  req.on("timeout", () => { req.destroy(); process.exit(0); });
  req.on("error", () => {
    // 端口未监听 / 连接拒绝 / 超时 — 不影响 Claude Code 主流程。
    process.exit(0);
  });
  req.write(body);
  req.end();
});
