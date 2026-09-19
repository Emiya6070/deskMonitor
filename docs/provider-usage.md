# Cursor、Claude、Grok 用量

在设置 → 更多用量，分别开启个人订阅和 API/团队费用卡片。可以只显示用量而不添加行情；所有卡片可在“行情与行为 → 卡片顺序”排序。关闭窗口显示时不发起新的轮询，退出或应用配置会取消尚未完成的用量请求。

## 可用范围

| 服务 | 个人订阅 | API / 团队 |
| --- | --- | --- |
| Cursor | 本地标准快照；没有内置个人账户自动读取接口 | Admin API 团队当前账期总用量价值及按量支出；不是个人 API 剩余额度 |
| Claude | Claude Code 官方 statusLine 的 5 小时和周额度，通过附带脚本写入本地快照 | 组织本月 API 费用（UTC），不含 Pro/Max 订阅及 Priority Tier |
| Grok | 本地标准快照；没有内置 SuperGrok 自动读取接口 | xAI 团队本月 API 费用（UTC），不包含 Grok 网页订阅 |

配置与解析已通过合成响应测试；没有使用真实用户密钥进行在线联调。Cursor/Grok 的个人订阅快照入口不是自动获取订阅余额的实现。已有可信本地采集器可以接入该格式，否则卡片显示未配置。

## API 凭据

在 Windows 用户环境变量中配置凭据，并重启挂件。设置里填的是**变量名称**，不是密钥内容：

- Cursor：`CURSOR_ADMIN_API_KEY`，需要有团队管理权限的 Admin Key。
- Claude：`ANTHROPIC_ADMIN_KEY`，需要组织 Admin Key，不是普通推理 API Key。
- Grok：`XAI_MANAGEMENT_KEY`，需要 Management Key，并在设置里填写 xAI Team ID。

各服务凭据仅发送给固定的官方 HTTPS 域名，禁用 HTTP 重定向。不会写入 DeskMonitor 设置文件或显示在错误消息中。环境变量不是加密保险库，请按你的本机密钥管理方式配置。费用每 5 分钟更新；请求失败时保留旧值并显示错误，不把错误当作零费用。Claude 和 Cursor 完整读取分页，xAI 结果被截断时显示错误。

## Claude Code 订阅桥接

需要 Claude Code 支持 `rate_limits` 的 statusLine 输出（官方文档目前标注 v2.1.251+）。在你已有的 Claude Code statusLine 配置中调用本仓库的 `scripts/export-claude-usage.ps1`，例如将下列配置合并到 Claude Code 设置中，并把路径替换为实际绝对路径：

```json
{
  "statusLine": {
    "type": "command",
    "command": "powershell.exe -NoProfile -File C:/Tools/deskMonitor/scripts/export-claude-usage.ps1"
  }
}
```

脚本通过标准输入接收 statusLine JSON，只保存额度字段，不保存提示词或会话内容。若已有自定义状态栏，先把脚本逻辑合并进原脚本；挂件不会自动修改 Claude Code 配置。运行至少一次有额度信息的 Claude Code 会话后，默认输出文件为 `%LOCALAPPDATA%/DeskMonitor/claude-subscription.json`。在挂件设置中填写展开后的绝对路径。

若 Windows PowerShell 的执行策略拒绝运行可信的本地脚本，可在上述命令的 `-File` 前加 `-ExecutionPolicy Bypass`，仅作用于这次脚本进程；无需修改机器或用户全局执行策略。本机合成输入检查使用了这一进程级选项。

此方式由 Claude Code 活动驱动；关闭 Claude Code 后不会持续拉取新额度。挂件每分钟读取快照，超过 15 分钟明确标记过期，到达重置时间显示“待更新”。

## 标准订阅快照

Cursor/Grok 的本地采集器可写入以下格式；`provider` 分别填写 `Cursor`、`Claude` 或 `Grok`。这是挂件的本地交换格式，不是声称各厂商提供该接口。

```json
{
  "provider": "Grok",
  "observedAt": "2026-09-18T04:00:00Z",
  "windows": [
    { "label": "订阅周期", "usedPercent": 35, "resetsAt": "2026-09-18T08:00:00Z" }
  ]
}
```

示例仅说明格式，不是实际账户额度。必须使用真实采集时间和已用百分比（0–100），不提供重置时间时填 `null`。支持 1–8 个窗口，拒绝跨服务快照、缺字段、未来时间和无效百分比；建议临时文件写完后原子替换，防止读取半个 JSON。

依据：[Cursor Admin API](https://cursor.com/docs/account/teams/admin-api)、[Claude Usage & Cost API](https://platform.claude.com/docs/en/manage-claude/usage-cost-api)、[Claude Code statusLine](https://code.claude.com/docs/en/statusline)、[xAI Management API](https://docs.x.ai/developers/management-api-guide)、[xAI Billing](https://docs.x.ai/developers/rest-api-reference/management/billing)。
