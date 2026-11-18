# 便签服务部署

本地便签直接保存在桌面电脑。需要跨设备同步或接入 ChatGPT 时，可单独运行单用户便签服务。服务器使用 ASP.NET Core 8 Runtime，Windows 桌面挂件使用 .NET 8 Desktop Runtime。

## 本机试用

在源码根目录用 PowerShell 7 发布并准备服务。首次运行时生成两个独立值，保存到自己的密码管理器，后续启动继续使用相同的值。

```powershell
dotnet publish ./DeskMonitor.NotesService -c Release -o ./artifacts/notes-service
$env:NOTES_API_TOKEN = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$env:NOTES_OAUTH_CLIENT_SECRET = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$env:NOTES_DATA_FILE = Join-Path $env:LOCALAPPDATA 'DeskMonitor/notes-service/notes.json'
$env:NOTES_PUBLIC_URL = 'http://127.0.0.1:5088'
dotnet ./artifacts/notes-service/DeskMonitor.NotesService.dll --urls http://127.0.0.1:5088
```

在桌面便签管理窗口填入 `http://127.0.0.1:5088` 和 `NOTES_API_TOKEN`。环回地址只在本机可访问。要连接 ChatGPT，需配置下面的公网 HTTPS 入口。

## 服务配置

| 环境变量 | 设置 |
| --- | --- |
| `NOTES_API_TOKEN` | 桌面 REST 访问令牌，也用于自建服务授权页确认所有者身份。可使用 32 个随机字节的十六进制表示。 |
| `NOTES_OAUTH_CLIENT_SECRET` | 为预登记的 ChatGPT OAuth 客户端单独生成的密钥。两个令牌均接受 32–512 个英文字母、数字、下划线或连字符。 |
| `NOTES_DATA_FILE` | 指向便签 JSON 文件的绝对路径。服务账号需要目录的读写权限。 |
| `NOTES_PUBLIC_URL` | 对外提供的 HTTPS 基础地址，例如 `https://notes.example.com/deskmonitor-notes`。请替换为自己的域名和路径，省略 `/notes` 和 `/mcp`。 |

每个数据目录运行一个服务实例。备份 `notes.json`、同目录的 `oauth-tokens.json` 和 `oauth-keys/`，并单独保管两个环境变量的凭据。这样恢复服务后可以继续使用已有便签和授权。

### Linux 与反向代理

1. 将应用发布到服务器。例如 Linux x64 可从源码根目录运行 `dotnet publish DeskMonitor.NotesService -c Release -r linux-x64 --self-contained true -o artifacts/notes-service-linux`，将产物放到所选安装目录。
2. [systemd 单元示例](deploy/deskmonitor-notes.service) 从 `/opt/deskmonitor-notes/current` 启动可执行文件，监听 `127.0.0.1:5088`。按实际安装位置调整单元，将密钥和 `NOTES_PUBLIC_URL` 写入仅 root 可读的 `/etc/deskmonitor-notes.env`（权限 0600）。单元已设置持久化数据目录和 `NOTES_DATA_FILE`。将单元安装到 `/etc/systemd/system/` 后运行 `systemctl daemon-reload` 和 `systemctl enable --now deskmonitor-notes.service`。
3. 在自己的 HTTPS 站点中包含 [Nginx 配置示例](deploy/deskmonitor-notes.nginx.conf)。示例使用 `/deskmonitor-notes` 前缀，代理会移除该前缀后转发请求，同时保留 Authorization 头。如果选择其他前缀，请同步修改 `NOTES_PUBLIC_URL` 和 Nginx 的所有对应路径，包括两个 `/.well-known/` 发现入口。检查 Nginx 配置通过后重新加载。
4. 使用 ChatGPT Actions 的网址导入功能时，复制 [OpenAPI 模板](../docs/chatgpt-notes-openapi.json)，将 `servers[0].url` 改为实际基础地址。示例 Nginx 从 `/opt/deskmonitor-notes/integration/openapi.json` 提供修改后的文件。也可直接将配置粘贴到 Actions。MCP 通过 OAuth 自动发现端点。

### Docker

也可在仓库根目录构建并启动容器。运行参数假定当前目录已有仅自己可读的 `.env.notes`，其中设置了两个令牌与实际 `NOTES_PUBLIC_URL`。

```sh
docker build -f DeskMonitor.NotesService/Dockerfile -t deskmonitor-notes .
docker run -d --name deskmonitor-notes --env-file ./.env.notes -p 127.0.0.1:5088:8080 -v deskmonitor-notes-data:/data deskmonitor-notes
```

镜像以非 root 用户运行，将便签及 OAuth 文件保存在 `/data`。容器端口同样由前述 HTTPS 反向代理对外提供。

## 上线检查

1. 在源码目录运行 [便签文档的验证命令](../docs/desktop-notes.md#验证)。
2. 访问实际基础地址下的 `/notes`。未带令牌应返回 401，携带 `Authorization: Bearer <自己的 NOTES_API_TOKEN>` 应返回便签数组。
3. 在桌面创建服务便签，确认能分别查看重点和全文。重启服务后刷新，检查同一张便签仍可读取。
4. 按 [ChatGPT MCP 接入说明](../docs/chatgpt-notes-mcp.md) 完成授权。在 ChatGPT 中读取这张便签并更新，再从桌面刷新确认同一 ID 的新版本。
