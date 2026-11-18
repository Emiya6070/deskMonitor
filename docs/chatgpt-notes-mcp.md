# ChatGPT 网页通过 MCP 使用桌面便签

先按 [服务部署说明](../DeskMonitor.NotesService/DEPLOYMENT.md) 启动自己的服务，并从 ChatGPT 可访问的网络提供 HTTPS 入口。下文以 `https://notes.example.com/deskmonitor-notes` 为基础地址，请替换成你配置的 `NOTES_PUBLIC_URL`。同一基础地址可供桌面连接使用。

## 连接参数

| 参数 | 值 |
| --- | --- |
| 名称 | 桌面便签 |
| MCP 地址 | `https://notes.example.com/deskmonitor-notes/mcp` |
| 传输 | Streamable HTTP |
| 认证 | OAuth，预登记客户端 |
| Client ID | `deskmonitor-chatgpt` |
| Client Secret | 服务配置中的 `NOTES_OAUTH_CLIENT_SECRET` |
| Token 端点身份验证方法 | `client_secret_post` |
| 授权回调 | `https://chatgpt.com/connector_platform_oauth_redirect` |

## 网页操作

1. 从账号或工作区提供的自定义 MCP 服务入口添加服务，输入名称和完整 MCP 地址。
2. 选择 OAuth，在高级设置中选择自定义 OAuth 客户端，填写表中的 Client ID、Client Secret 和认证方法。使用自动发现的端点和 `notes`、`offline_access` 范围。
3. 按页面提示连接。浏览器跳到你自己的服务域名后，在“连接桌面便签”页面输入 `NOTES_API_TOKEN` 并授权。服务用这个令牌确认便签所有者，ChatGPT 客户端使用上表中的独立 Client Secret。
4. 返回 ChatGPT 后，确认能看到 `list_notes`、`get_note`、`save_note`。在需要使用便签的聊天中启用这些工具。

服务只接受表中预登记的回调 URI。若界面给出其他 URI，请核对客户端设置和发现文档。

## 实际使用

可以先发送“列出我的桌面便签”。刚部署的服务会返回空列表，保存第一张便签后就能在桌面刷新查看。

分析时可说：“读取这个 Git 仓库并分析，完成后把结论写入桌面便签；重点写结论、下一步和阻塞，全文保存完整分析。”继续时可说：“读取并继续某某便签，完成后更新原便签。”

服务在 MCP 握手说明和工具描述中携带默认规则：仓库已成功读取且形成结论后保存；同项目同主题更新原 ID；用户说“不保存”时不写；写入仍遵循 ChatGPT 的确认要求。规则只在选用了该工具的聊天中生效，不会让所有普通聊天自动连接服务。全文见 [默认规则](chatgpt-notes-instructions.md)。

`summary` 是桌面重点，`content` 是完整分析，`sourceUrl` 是原文或对话链接。保存规则会自动填写上下文中已知的真实当前对话 URL，并在更新但未取得新链接时保留原链接。ChatGPT MCP 的 `openai/session` 是匿名会话标识，不是可跳转的对话 ID，不能拼接成网址；纯 MCP 无法保证自动取得地址栏 URL。需要跳回聊天而链接仍为空时，把地址栏的真实链接提供一次，后续可继续沿用；没有链接时仍能查看保存的全文。参考：[官方客户端元数据说明](https://developers.openai.com/plugins/reference#_meta-fields-the-client-provides)。

`list_notes` 只返回标题、重点、ID、版本和更新时间，不传回所有便签的全文；`get_note` 读取一张的完整内容；`save_note` 检查版本，拒绝过期覆盖。没有暴露 MCP 删除工具。

## 实现与验证

服务采用官方 `ModelContextProtocol.AspNetCore` 2.3.0（.NET 8），使用无会话 Streamable HTTP；同时验证当前默认协议和 `2025-11-25` initialize 客户端。桌面项目仍不依赖 MCP SDK。

OAuth 为单用户、单个预登记 ChatGPT 客户端的授权码流程，要求 S256 PKCE、精确回调和资源地址匹配。授权页校验 CSRF，授权码五分钟单次有效；访问令牌一小时，刷新令牌三十天并每次轮换。服务只保存令牌的哈希，存储及数据保护密钥位于服务的私有 StateDirectory，重启后可继续刷新。MCP OAuth 令牌不能访问桌面 REST 接口，也不能借该接口删除数据。

测试命令：

```powershell
dotnet build DeskMonitor.NotesService -c Release
dotnet run --project DeskMonitor.Tests -c Release -- --notes-service DeskMonitor.NotesService/bin/Release/net8.0/DeskMonitor.NotesService.dll
```

检查包括原 REST 接口、OAuth 发现、授权页、CSRF、客户端认证、PKCE、错误回调与资源、授权码/刷新令牌重放、官方 MCP 客户端发现和读写、旧版本冲突及重启后续期。公网验证使用同一测试程序的 `--notes-mcp-live`，通过环境变量传入凭据，不把令牌放进命令参数或日志；仅创建和删除随机 ID 的测试便签。

官方参考：[ChatGPT 自定义 MCP 连接](https://help.openai.com/en/articles/12584461-connecting-custom-mcp-servers-in-chatgpt)、[OAuth 认证与回调要求](https://developers.openai.com/plugins/build/auth)、[官方 C# SDK](https://github.com/modelcontextprotocol/csharp-sdk/tree/v2.3.0)。
