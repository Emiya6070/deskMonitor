# Cursor、Claude、Grok、OpenAI 用量

在设置 → 更多用量，分别开启个人订阅和 API/团队费用卡片。可以只显示用量而不添加行情；所有卡片可在“行情与行为 → 卡片顺序”排序。关闭窗口显示时不发起新的轮询，退出或应用配置会取消尚未完成的用量请求。

## 可用范围

| 服务 | 个人订阅 | API / 团队 |
| --- | --- | --- |
| Cursor | 本地标准快照；没有内置个人账户自动读取接口 | Admin API 团队当前账期总用量价值及按量支出；不是个人 API 剩余额度 |
| Claude | Claude Code 官方 statusLine 的 5 小时和周额度，通过附带脚本写入本地快照 | 组织本月 API 费用（UTC），不含 Pro/Max 订阅及 Priority Tier |
| Grok | 本地标准快照；没有内置 SuperGrok 自动读取接口 | xAI 团队本月 API 费用（UTC），不包含 Grok 网页订阅 |
| OpenAI | 此卡片不读取 ChatGPT 订阅；Codex 额度仍由原有 Codex 卡片读取 | 组织本月 API 费用（UTC）；提供官方余额与账单页入口，不将费用当作余额 |

配置与解析已通过合成响应测试；OpenAI 已完成真实账户的只读接口诊断，其余服务未使用真实管理密钥进行在线联调。Cursor/Grok 的个人订阅快照入口不是自动获取订阅余额的实现。已有可信本地采集器可以接入该格式，否则卡片显示未配置。

## API 凭据

在 Windows 用户环境变量中配置凭据，并重启挂件。设置里填的是**变量名称**，不是密钥内容：

- Cursor：`CURSOR_ADMIN_API_KEY`，需要有团队管理权限的 Admin Key。
- Claude：`ANTHROPIC_ADMIN_KEY`，需要组织 Admin Key，不是普通推理 API Key。
- Grok：`XAI_MANAGEMENT_KEY`，需要 Management Key，并在设置里填写 xAI Team ID。
- OpenAI：`OPENAI_ADMIN_KEY`，需要组织 Admin API Key，不是普通模型调用 Key。

各服务凭据仅发送给固定的官方 HTTPS 域名，禁用 HTTP 重定向。不会写入 DeskMonitor 设置文件或显示在错误消息中。环境变量不是加密保险库，请按你的本机密钥管理方式配置。费用每 5 分钟更新；请求失败时保留旧值并显示错误，不把错误当作零费用。Claude、Cursor 和 OpenAI 完整读取分页，xAI 结果被截断时显示错误。

## OpenAI 费用与余额

在 Windows 用户环境变量中设置 `OPENAI_ADMIN_KEY`，重启挂件后，在“更多用量”勾选“OpenAI · API 费用”。设置中只填写变量名；无需向聊天发送密钥。卡片调用官方 `GET https://api.openai.com/v1/organization/costs`，从本月 1 日 00:00 UTC 查询至本次读取时间，完整汇总所有分页的 `amount.value`（已经是 USD，无需除以 100）。范围为 Admin Key 对应的整个组织，包含该组织各项目，账单数据可能有延迟。

具体配置位置：Windows 搜索“编辑账户的环境变量”，新建用户变量，变量名填写 `OPENAI_ADMIN_KEY`，变量值填写密钥。保存后从托盘彻底退出并重新启动 DeskMonitor。“更多用量”页面的凭据输入框继续保留变量名。

费用接口所有分页均无明细时，卡片显示“暂无费用记录”；收到明确的零金额记录才显示 `0.00 USD`。这两种情况不同，不能从空费用列表断言没有模型调用。若官网已显示费用，先核对官网当前组织、项目筛选和日期是否与挂件的本月 UTC 范围一致。Admin Key 查询组织费用，并非只统计该管理 Key 自己发起的请求；Usage 请求/Token 数据与 Costs 费用明细也可能暂时不一致。

截至 2026-11-11，所核对的官方公开文档提供 Costs API，未找到剩余余额查询接口。因此卡片明确显示“本月累计费用”，不以充值金额减费用估算余额，也不使用未公开的 dashboard 接口。点击“查看余额与账单”会在浏览器打开 [OpenAI 官方账单页](https://platform.openai.com/settings/organization/billing/overview)，登录后选择对应组织查看。设置页中的入口无需开启费用卡片或提供任何 Key。

OpenAI 请求与解析、分页、异常和界面使用合成数据验证。2026-11-11 的真实账户只读诊断中，同一个 Admin Key 对 Costs 和模型 Usage 查询均返回成功，但 Costs 无明细、Usage 有请求记录；未发现权限拒绝或小额格式化导致的零费用。原因尚不能仅凭响应确定，费用延迟或官网筛选范围仍需核对。发布包和验证记录不含密钥或账号标识，也未将额外 Usage 查询加入挂件轮询。

依据：[OpenAI Costs API](https://developers.openai.com/api/reference/resources/admin/subresources/organization/subresources/usage/methods/costs)、[官方余额查看说明](https://developers.openai.com/api/docs/guides/chatgpt-api-credits)。

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
