# DeskMonitor

Windows 11 原生桌面行情挂件，.NET 8 / WPF，无浏览器内核，无第三方 NuGet 依赖。

## 下载与运行

从 [GitHub Releases](https://github.com/Emiya6070/deskMonitor/releases/latest) 下载 `DeskMonitor-v0.3.6-win-x64.zip`，完整解压后运行 `DeskMonitor.exe`。不要直接在压缩包内运行。

运行环境：Windows 11 x64、[.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)（选择 Windows x64 的 Desktop Runtime）。Codex 用量为可选功能，需要本机安装 Codex 并登录 ChatGPT；行情观察不需要 Codex 或币安 API Key。

升级时先从托盘退出挂件，再替换解压目录内的程序文件；个人设置保存在 `%LOCALAPPDATA%/DeskMonitor/settings.json`，不随发布包分发。移动安装目录后请重新开启自启动以更新路径。

## 从源码编译

```powershell
./scripts/build.ps1
./artifacts/app/DeskMonitor.exe
```

编译需要 .NET 8 SDK。保留 `artifacts/app` 整个目录；制作 Windows x64 发布包可运行 `./scripts/package.ps1`。

发布新版本时，在 `DeskMonitor/DeskMonitor.csproj` 同步维护 `Version` 和 `ReleaseDate`（`yyyy-MM-dd`）；“关于”页直接读取编译后的版本与发布日期，不使用文件复制时间。

## 设置与使用

点击顶栏齿轮，或**右键卡片 → 设置与币种管理**。小号隐藏顶栏，右键和托盘设置入口始终可用。

**右键卡片 → 置顶显示**可直接开关置顶，勾选代表已开启；与顶栏图钉、设置中的“始终置顶”共用状态，并保存到下次启动。

「关于」页显示当前版本、更新日期、运行环境，并提供 GitHub 项目主页及版本说明/下载链接；页面沿用当前皮肤。

「外观与性能」页可选择五套内置皮肤，保存前提供示例预览：森林绿（原有配色）、石墨（冷灰深色）、纸白（简洁浅色）、柔雾紫（柔和圆角）、冰川（深蓝静态渐层）。行情、用量、窗口和设置页共用主题。设计参考 [Fluent 2 的色彩与材质](https://fluent2.microsoft.design/material)、[Material 3](https://m3.material.io/) 与 [Apple 的玻璃层次设计](https://developer.apple.com/videos/play/wwdc2025/219/)；使用纯色或静态渐变，没有背景模糊和实时折射。

- **小号圆角**：在「外观与性能」选择跟随皮肤，或自定义 0–24 DIP，同时作用于小号行情与用量卡片；不改变屏幕角的窗口贴合。
- **隐藏标题栏**：中、大号可隐藏顶栏，保存后默认窗口高度相应缩短；仍可右键卡片或通过托盘打开设置。
- **小号趋势与用量**：币种和价格之间有足够空间时显示迷你走势图，宽度不足自动隐藏；沿用每项已保存的跨度（默认 2 分钟），不影响价格显示。用量卡片按实际显示的周期自动收紧，可独立增加留白。
- **文字与布局缩放**：85%–130%，同步调整卡片文字、间距和默认窗口大小。
- **价格与额度数字大小**：90%–130%，可独立于其他文字调整；可选等宽数字字体。宽度不足时价格仍会缩小以完整显示。
- **趋势开关与刷新频率**：可隐藏趋势图；界面每 1 / 2 / 5 秒更新，默认 1 秒。调整界面频率不会改变行情连接；曲线正常更新最多每 5 秒一次，切换跨度或调整尺寸时立即重绘。
- 「行情与行为」页管理币种、三档样式、卡片顺序、自启动和吸附；「附加显示」页管理 Codex 用量。改变字体缩放或数字大小会重新计算默认窗口尺寸；仅换皮肤保留自定义尺寸。
- **卡片顺序**：在「行情与行为 → 卡片顺序」选中卡片，点击上移/下移，保存后生效。行情与 Codex 用量可混合排序；暂时关闭用量仍保留其位置，新增行情追加到末尾。旧设置沿用原先的用量优先顺序。

| 样式 | 可见信息 | 默认宽度 / 每项高度 |
| --- | --- | --- |
| 小 | 币种、价格、合约资金费率；宽度足够时显示迷你趋势图 | 280 / 54 DIP |
| 中 | 币种、价格、24h 涨跌、右侧趋势图、行情状态和来源；合约增加费率和结算时间 | 360 / 现货 150、合约 174 DIP |
| 大 | 中号信息 + 实时轨迹、24h 高低价 | 400 / 现货 266、合约 290 DIP |

- **多币种**：设置中切换现货 / USDT 永续 / USDC 永续，搜索币种、选择后点击「添加所选」，保存。最多 20 项，支持移除；在「卡片顺序」中上下排序。BTC 现货与 BTC 永续可以同时观察，互不覆盖。
- **行情颜色**：价格与 24h 涨跌按滚动 24 小时涨绿、跌红、持平中性显示。趋势线颜色按当前所选区间的首尾采样价格判断，可能与 24h 颜色不同。
- **趋势跨度**：中号卡片右侧、大号曲线上方可单独选择 2 分钟、5 分钟、15 分钟、1 小时；按每个交易对及现货/永续身份保存，切换不重连行情。图表展示本次运行的本地真实采样，尚未采集的时间留空；重启后重新积累。
- **手动大小**：开启「允许手动调整大小」后拖动窗口边缘或角落。卡片宽度随窗口适配；各档信息布局和行高固定，高度不足时滚动，避免挤压内容。切换样式或增减币种会恢复对应默认尺寸；重新启动记住自定义宽高。
- **拖动**：按住价格、图表或空白处移动整个挂件。按钮、输入框、滚动条保留自身操作。
- **吸附**：开启「自动吸附屏幕边缘」后，拖动到当前显示器工作区边缘 12 DIP 内时吸附，避开任务栏。吸附后，鼠标沿离开边缘的方向累计移动 24 DIP 即可拖走；横纵方向分别判断，慢慢拖动也能脱离。设置中可以关闭。贴住屏幕工作区的四个边角时，对应圆角自动变为直角，拖离后恢复；其余圆角保留。
- **自启动**：开启「登录 Windows 时自动启动」后写入当前用户 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\DeskMonitor`。无需管理员权限；关闭开关只移除本应用的项。默认关闭。Windows 任务管理器中的禁用设置可能覆盖启动项。
- **托盘与单实例**：挂件和设置窗口均不显示任务栏图标，仅保留通知区域的托盘图标（Windows 可能将其放在隐藏图标区）。减号以短暂淡出动画收起，双击托盘恢复，托盘右键可打开设置/退出。动画遵循 Windows 动画开关，仅在收起时创建临时快照，恢复或退出会取消未完成的动画。再次运行程序会恢复现有挂件，不产生重复行情进程。叉号退出时先隐藏窗口和托盘图标，再完成后台清理。
- **持久化**：设置保存到 `%LOCALAPPDATA%/DeskMonitor/settings.json`；旧版单币种设置自动迁移，保留币种、置顶及位置。

## 行情数据源代理

在「行情与行为 → 行情数据源代理」选择跟随系统、直连或自定义。自定义输入 `http://127.0.0.1:7890` 或 `socks5://127.0.0.1:1080`；本版支持无账号密码代理。保存后目录、价格查询和实时连接统一生效，重连期间显示连接状态；不可用代理不会自动回退直连。本选项不更改系统网络设置；Codex 用量查询仍沿用本机 Codex 的网络环境。

## Codex 用量卡片

设置的「附加显示」页开启「显示 Codex 用量卡片」，可与行情同时显示，也可移除全部币种后仅保留用量。小号显示各周期剩余百分比；中、大号增加进度条、重置倒计时和本地重置时间。中、大号点击「刷新」，小号右键「刷新 Codex 用量」可立即查询。

通过本机 `codex.exe app-server --stdio` 的 `account/rateLimits/read` 读取当前登录账户的共享额度；不消耗重置券、不发起模型请求、不读取会话正文或自行读取登录凭据。窗口名称依接口实际周期显示；未提供的额度窗口整行隐藏，再次返回时恢复，不能推断为满额。剩余百分比为 `100 - usedPercent`，优先读取 `rateLimitsByLimitId.codex`。这不是本地 Token 总数、费用或当前任务专属额度。

每分钟查询一次，每次查询后退出子进程；隐藏挂件或关闭卡片时停止查询。读取失败时保留并淡化上次成功数值，标明上次更新时间，悬停查看原因；继续按每分钟周期自动重试，也可手动刷新。尚未读取成功时不补造数据。超过两分钟的数据变淡，到达重置时间显示「待更新」，不会自行补成 100%。查询超时为 20 秒；正常退出、终止后退出和日志管道清理各有最多 2 秒等待，防止清理一直占用刷新状态。设置可指定 Codex EXE；留空依次查找用户级标准安装位置和 PATH 中的 `codex.exe`。登录与 `CODEX_HOME` 沿用本机 Codex 环境；API Key 模式不保证有 ChatGPT 额度。

参考 [QuotaLoom 的数据接入设计](https://github.com/EricsmOOn/quota-loom/blob/main/docs/ARCHITECTURE.md)，本实现使用 C# 原生卡片，未引入其 Tauri/React 运行时或复制其源码。协议依据 [Codex App Server 官方文档](https://learn.chatgpt.com/docs/app-server#6-rate-limits-chatgpt)。

额度查询返回内部错误 `-32603` 时，清理该次进程后等待 2 秒，最多再读一次；再次失败才显示错误，随后恢复每分钟查询。初始化失败、其他错误码、缺失额度和超时不触发这次额外重试。收起挂件或关闭卡片会取消重试等待；单次尝试仍受上述超时限制。内部错误不等同于未登录，无法仅凭错误码判断底层网络或服务原因。

Codex 进程的启动和清理在后台执行；主动取消查询时跳过正常退出的 2 秒宽限，立即终止本次查询进程，避免退出或收起时阻塞界面。

## 行情口径

提供币安 **USDT 现货、USDT / USDC 本位永续合约的最新成交价**。不是标记价格或指数价格，未包含币本位、交割合约或期权；没有交易/下单功能，也不需要 API Key。

价格单位按交易对为 **USDT 或 USDC，不是美元**。24h 涨跌由 `(last / open24h - 1) × 100%` 计算，采用滚动 24 小时。各档曲线均为本次观察期间选定时间范围的真实采样；横轴使用实际事件时间，超过 15 秒的采样间隔断开，不补造历史。

记录交易所事件时间（UTC 毫秒）和本机接收时间，任一超过 15 秒即过期。中、大号显示状态；小号将旧价淡化，悬停可查看过期、连接状态、来源和时间。未取得行情显示 `—`。一个币种没有更新时，不会因其他币种仍在推送而被标记为新鲜。

## 接口与资源

- 现货目录：`https://data-api.binance.vision/api/v3/exchangeInfo?permissions=SPOT&showPermissionSets=false`
- 现货行情：`wss://data-stream.binance.vision/stream?streams=<symbol>@ticker/...`
- 合约目录：`https://fapi.binance.com/fapi/v1/exchangeInfo`，筛选 TRADING / USDT 或 USDC / PERPETUAL 或 TRADIFI_PERPETUAL。
- 合约行情：`wss://fstream.binance.com/market/stream?streams=<symbol>@ticker/...`

USDT 与 USDC 合约共用一条连接，现货和合约合计最多两条，故障分别处理；修改显示样式不重连。更改订阅前先取消旧连接。真实网络中断使用 2–30 秒退避重连，30 秒无数据则重建连接；不自动改换交易所。

币种目录按需流式读取并在本次运行缓存，可手动刷新；不周期 HTTP 轮询。UI 默认每秒刷新一次，曲线每 5 秒最多采样及重建一次，每项只保存最近一小时、最多 721 个点（含边界）；小号宽度不足或关闭趋势图时不创建图表数据数组。用量数据未变化时，仅在显示分钟、过期或重置状态变化时更新；复用冻结的主题画刷。隐藏后停止 UI 刷新和采样，仅保留行情连接，恢复时保留空白区间。无常驻动画、常驻透明分层窗口或浏览器进程。程序依赖的 .NET / WPF / 图形驱动仍有常驻内存开销，实测与限制见 [验证记录](docs/verification.md)。

官方协议：[现货 WebSocket](https://github.com/binance/binance-spot-api-docs/blob/master/web-socket-streams.md)、[合约市场行情](https://developers.binance.com/en/docs/catalog/core-trading-derivatives-trading-usd-s-m-futures/api/ws-streams/market)、[合约 REST](https://developers.binance.com/en/docs/catalog/core-trading-derivatives-trading-usd-s-m-futures/api/rest-api/market-data)。公开接口不等于任意商业再分发授权；本版本用于用户本机观察，保留来源标识。

## 验证命令

```powershell
dotnet run --project DeskMonitor.Tests -c Release
dotnet run --project DeskMonitor.Tests -c Release -- --live
dotnet run --project DeskMonitor.Tests -c Release -- --live-usage
dotnet run --project DeskMonitor.Tests -c Release -- --live-usdc
```

确定性检查覆盖数据校验、现货/永续身份隔离、目录筛选、时间/单位、旧设置迁移、多币种配置、尺寸及边缘吸附。`--live` 另验证两类目录、四个真实行情和两条合并连接同时接收及取消。




## 合约资金费率与 TradFi

USDT/USDC 永续自动显示资金费率。小卡片在名称下方显示费率，中/大卡片同时显示下一次结算的本地时间；悬停查看完整日期、数据时间和付款方向。正值为多头付给空头，负值为空头付给多头。显示币安当期费率，结算前仍可能变化，非已结算费率或年化值。不假定周期恒为 8 小时。

沿用合约合并 WebSocket，对自选合约增加 `<symbol>@markPrice`（默认 3 秒），读取 `r`（比例，显示乘 100）、`T`（下次结算毫秒时间戳）、`E`（事件毫秒时间戳）。来源时间/本地接收超过 15 秒或连接中断时独立标记费率过期；结算时间已到而新周期未确认时显示待更新。缺失展示 `—`，现货不订阅或显示资金费率。无新增进程、HTTP 轮询或 WebSocket 连接。

合约目录接收 `TRADIFI_PERPETUAL` 并标记“TradFi 永续”。在 USDT 永续下搜索 TSLA、AAPL 等实际上市标的。价格仍为币安合约最新成交价、USDT 计价，涨跌幅仍为滚动 24 小时。

协议：[币安 Mark Price Stream](https://developers.binance.com/en/docs/catalog/core-trading-derivatives-trading-usd-s-m-futures/api/ws-streams/market#mark-price-stream)。实测命令：`dotnet run --project DeskMonitor.Tests -c Release -- --live-funding`。

## 用量卡片高度

在“附加显示 → 用量卡片高度”调整：0 为按实际可见内容自动收紧，最大可额外增加 120 个逻辑像素留白，随整体字号缩放。三种尺寸均支持，缺少周期时卡片自动变矮，放大字体时自动撑开。自动尺寸的挂件跟随卡片高度；自由调整窗口大小时保留手动尺寸，更改用量高度设置后重新贴合内容。

当前仅读取本机 Codex。远程主机可后续通过 SSH 调用该主机已登录的 Codex `app-server` 并读取 `account/rateLimits/read`；此接口返回账户共享额度，不是各主机独立消耗。当前版本尚未实现远程主机配置。
