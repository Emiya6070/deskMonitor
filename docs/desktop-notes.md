# 桌面便签与 ChatGPT

## 桌面使用

从挂件右键菜单或托盘右键菜单打开 **桌面便签**。

- **新建本地便签**：无需服务。标题、桌面重点、完整内容分别填写；原文链接可选。
- 每张便签都是独立窗口，拖动标题移动，拖动边缘调整大小，可以分别置顶。正文区域展示 `summary`，重点过长时可滚动。
- **打开原文** 使用系统浏览器打开 `sourceUrl`，可放 ChatGPT 对话链接、GitHub 原文或报告链接。没有链接时按钮禁用；**查看全文**显示保存的 `content`。服务便签更新后，已打开的全文窗口也会同步。
- **收起**只隐藏窗口，不删除数据；管理窗口双击条目或点击“显示”恢复。窗口位置、大小、置顶与收起状态在正常退出后恢复。窗口会保持在主屏工作区内，避免显示器变化后找不到。
- 填写服务基础地址，例如 `https://notes.example.com/deskmonitor-notes`（将示例域名和路径换成自己的部署地址，不加 `/notes`），以及访问令牌，点击“保存连接”。也支持反向代理的路径前缀，代理需移除前缀后转发。
- 每 30 秒 GET `/notes`，可立即刷新。远程要求 HTTPS，本机 `localhost` / `127.0.0.1` 调试允许 HTTP。不跟随重定向，避免令牌被转发至另一个地址。
- **新建服务便签**与服务列表共用存储，ChatGPT 可继续读取和更新。桌面编辑会检查 revision；发生冲突时保留编辑窗口和文本，请刷新列表、查看新版本后重新编辑。
- 服务返回新增便签会自动打开；删除的便签自动关闭。断网时保留本次运行中上次成功内容并标记过期；重启后需成功连接才能重新取得服务便签。本地便签不受网络影响。
- 最多 100 张本地便签、100 张服务便签。远程和本地同 ID 不冲突。更换服务不会迁移便签。

本地数据为 `%LOCALAPPDATA%/DeskMonitor/notes.json`；连接及窗口设置为同目录 `notes-settings.json`，与原行情设置分开。访问令牌以明文保存在当前 Windows 用户的设置文件里，请勿共享该文件。数据损坏时显示错误并保留原文件，不静默覆盖。建议随日常个人数据备份。

## 可部署服务

服务使用项目现有的 .NET 8 工具链，MCP 入口使用官方 ModelContextProtocol.AspNetCore SDK。提供带 Bearer 令牌认证的单用户桌面 API 和带 OAuth 认证的 MCP 工具，使用原子替换的 JSON 文件保存数据。部署说明见 [服务部署](../DeskMonitor.NotesService/DEPLOYMENT.md)。不要让多个服务进程同时写同一数据文件；没有多用户隔离或跨进程数据库锁。

| 方法与路径 | 用途 |
| --- | --- |
| `GET /notes` | 返回便签 JSON 数组，每项包含下列全部字段 |
| `GET /notes/{id}` | 读取一张便签，供聊天继续 |
| `PUT /notes/{id}` | 新建或更新；请求体为 title、summary、content、sourceUrl、expectedRevision |
| `DELETE /notes/{id}?revision=2` | 按当前版本删除；桌面管理使用，未在 ChatGPT Actions 中暴露 |

```json
{
  "id": "deskmonitor-analysis",
  "title": "DeskMonitor · 便签分析",
  "summary": "结论：重点和全文分开。\n下一步：连接 ChatGPT 并试写一张便签。",
  "content": "这里保存完整分析、证据与下一步……",
  "sourceUrl": null,
  "revision": 1,
  "updatedAt": "2026-11-15T00:00:00Z"
}
```

标题上限 120 字，重点上限 2000 字，全文上限 100000 字（按 .NET UTF-16 字符计数）。三个字段必填且非空。ID 为 1–80 位英文字母、数字、连字符、下划线。原文链接仅接受 HTTP/HTTPS；不运行 HTML、Markdown 脚本或本地文件协议。

新建使用 `expectedRevision: 0`；更新必须先读取再携带当前 revision。成功后 revision 加一。同 ID 重复创建、过期更新、过期删除返回 409。校验失败返回 400，无有效令牌返回 401，未找到返回 404。请求体限制 768 KiB。请求超时后应先读取该 ID 确认结果，不另建 ID。

## ChatGPT 网页接入

先部署自己的 HTTPS 便签服务，记下配置的 `NOTES_PUBLIC_URL`。桌面连接直接使用这个基础地址。ChatGPT 使用自定义 MCP 时，在基础地址后加上 `/mcp`，按 [MCP 接入说明](chatgpt-notes-mcp.md) 配置 OAuth。

如果使用支持 Actions 的自定义 GPT，可通过以下步骤连接同一个服务。

1. 编辑自定义 GPT，在配置页打开 Actions。
2. 复制 [Actions 配置 JSON](chatgpt-notes-openapi.json)，将 `servers[0].url` 改为自己的 HTTPS 基础地址后粘贴。若已将修改好的 JSON 发布在自己的服务器，也可从该文件的公开 HTTPS 地址导入。
3. 在 Authentication 中选择 API Key / Bearer，填入该服务的 `NOTES_API_TOKEN` 值，省略 `Bearer ` 前缀。该认证设置允许 GPT 读写便签，请仅授予预期使用者。
4. 将 [默认便签规则](chatgpt-notes-instructions.md) 复制到 GPT 的 Instructions。
5. 运行 `listNotes`。新部署的空服务应返回空列表。试存一张便签，在桌面刷新后核对标题、重点、全文和原文链接，再让 GPT 更新同一张便签，确认版本增加、条目数不变。

默认规则会在仓库分析形成结论后写入便签，用户可以要求“不保存”。平台可能要求确认写入。若希望从桌面跳回聊天，可在聊天中提供当前对话的真实链接。更新时会沿用已有链接，首次保存且未获得链接时 `sourceUrl` 为 null。

账号及工作区权限以 ChatGPT 显示的配置入口为准。官方文档见 [Actions 入门](https://developers.openai.com/api/docs/actions/getting-started) 和 [Actions 认证](https://developers.openai.com/api/docs/actions/authentication)。

## 验证

```powershell
dotnet run --project DeskMonitor.Tests -c Release
dotnet build DeskMonitor.NotesService -c Release
dotnet run --project DeskMonitor.Tests -c Release -- --notes-service DeskMonitor.NotesService/bin/Release/net8.0/DeskMonitor.NotesService.dll
dotnet run --project scripts/VisualChecks -c Release -- artifacts/notes-visuals --notes
```

服务集成检查使用随机临时令牌、临时文件和本机随机端口，不触碰用户便签或现有服务器。测试包括认证、读写、版本冲突、非法内容、重启持久化与删除；不等同于真实 ChatGPT 调用或公网部署验证。
