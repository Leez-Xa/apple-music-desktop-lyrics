# Apple Music 运行时侦察记录

观察日期：2026-11-05（Asia/Shanghai）

## 已观察事实

- 进程名：`AppleMusic`
- 可执行文件：`C:\Program Files\WindowsApps\AppleInc.AppleMusicWin_1.1792.39682.0_x64__nzyj5cx40ttqa\AppleMusic.exe`
- 包目录版本特征：`1.1792.39682.0`
- 文件版本：`1.7.0.155`
- 当前工作区侦察方式：只读进程枚举和文件版本信息读取
- 当前适配器状态：`observation-only`

## 尚未观察

- UI Automation 树和播放器控制栏稳定定位方式。
- Apple Music 是否使用 WebView、WinUI/XAML 或混合 UI。
- Windows SMTC 是否暴露 Apple Music 当前会话、歌曲和播放位置。
- 歌词页面是否有可复核的可访问文本或时间轴数据。

## 下一步

1. 在 Apple Music 保持播放一首用户授权歌曲时，读取窗口层级和 UI Automation 属性。
2. 记录歌曲标题、艺术家、播放/暂停按钮和播放位置的可访问名称或 AutomationId。
3. 独立验证 SMTC 会话，不把 UI 注入和 SMTC 验证混在一次实验中。
4. 只有在证据显示存在稳定扩展点后，才为 `1.1792.39682.0` 增加版本适配记录。

不保存账号凭据、Cookie、音频内容或安装包副本；不修改 WindowsApps 目录。


## 2026-11-05 续作：UIA 与真实 SMTC 已验证

本节为追加记录，保留前面的初始记录；前文“尚未观察”清单由本节和关联证据更新。

### observed：客户端与播放器

- 实测客户端包版本 1.1792.39682.0，文件版本 1.7.0.155，进程 PID 20072。PID/HWND 只用于本次观察，不作为永久定位依据。
- 完整 UIA 树 649 节点，其中 XAML 638、Win32 11；主界面有 WinUI/XAML 证据。
- 加载 WebView2 Core/JavaScriptCoreV8 是已观察事实；当前播放器存在可用 WebView/DOM 注入入口仍为 unknown。
- 已定位 TransportBar、LCD、TransportControl_PlayPauseStop、LCDScrubber、LyricsToggleButton、PlayQueueToggleButton。
- 18:40 UIA 快照及后续专用适配器实测：窗口最小化，播放按钮名称为“播放”，歌词开关 Off，滑块 raw value/min/max 为 166/0/273。离屏矩形不可用于挂接布局。
- 新只读适配器 src/amlyrics/apple_uia.py 与 bridge/apple_uia.ps1 仅查询已观察控件 ID，不调用 Invoke/Toggle/SetValue，不恢复窗口或改变播放状态；文件和包目录版本不符时拒绝已验证适配。

### observed：Windows SMTC 与集成

- 系统 Windows PowerShell 5.1 + System.Runtime.WindowsRuntime 真实 WinRT bridge 成功，无需 SDK 或第三方依赖。
- Apple AUMID 为 AppleInc.AppleMusicWin_nzyj5cx40ttqa!App；不会用其他播放器代替 Apple 会话。
- 播放《流沙》时连续采样位置 147000 → 148000 → 149000 → 150000 ms，Playing，rate=1。
- 暂停《裹着心的光》时连续采样固定 166000/273000 ms，Paused，rate=1。未根据 rate 或旧 LastUpdatedTime 额外推进位置。
- 最终 scripts/run_live_overlay.py --headless-seconds 3 的集成样本再次得到相同暂停位置；--duration-seconds 3 成功打开和关闭真实模式窗口。
- LiveSession 有界接收最新状态，在主线程更新；切歌/断连清空旧歌词，拒绝迟到及歌曲不符结果；缺词/查询失败每 30 秒重试。已超过显式 end_ms 的末行不持续显示。
- 停止后子进程、stdout/stderr 线程已回收；集成检查无遗留 smtc.ps1 进程。

### 歌词来源与未完成能力

- 已新增 LyricsResolver：Apple callback → 显式本地映射 → 可选 LRCLIB。只接受同步时间轴，不把纯文本编造成同步歌词。
- 网络默认关闭，必须构造时 enable_external=True 且该次 lookup 的 allow_network=True 才能发送请求。本轮没有发送外部歌词请求，没有安装或下载依赖。
- Apple callback 只是已定好的接口，尚未接入 Apple 正文/时间轴提取；未声称已取得 Apple 同步歌词。
- SMTC Artist 实测为“林俊杰 — 裹着心的光 - Single”，AlbumTitle 为空。UIA LCD 副标题一致，但字段拆分语义还需独立证据。当前保留原值，严格外部匹配可能因艺术家字段不同而拒绝结果。
- 当前内部“桌面歌词”按钮、点击后自动启动、自动启动安装、版本签名和正式分发包均未完成。旧 windows_smtc.py 保持原能力边界；真实入口显式使用 SmtcBridgeMonitor。

### 验证

- python -m compileall -q src tests scripts：通过。
- PYTHONPATH=src；python -m unittest discover -s tests -q：82 项通过。
- python scripts/verify_live_overlay.py：置顶、当前/下一行、暂停标记、隐藏/恢复、退出清理通过；采用本地测试文字，不操作 Apple Music。
- 截图目视检查通过：research/apple-music/evidence/live-overlay-20261105-184105-131041.png；后续回归截图 live-overlay-20261105-185010-326102.png。
- 未修改 WindowsApps、未做 DLL 注入/客户端补丁、未改系统或用户持久配置；原项目文件仅对此研究文档作追加。

### 下一次必须具备的运行时状态

请用户恢复 Apple Music 主窗口，播放一首有同步歌词的歌曲并打开歌词面板。只读复测歌曲信息、当前行/选中状态、正文节点、是否有时标以及多次采样间变化。先确认时间轴和当前行证据，再实现 Apple 优先歌词 provider；原生内部按钮需独立验证可见区域、DPI、父子窗口关系和进程退出时清理，不能把旁置工具窗当作内部按钮交付。持久自动启动、外部歌词联网实测仍需按全局 AGENTS.md 取得对应授权。

### 证据入口

- docs/research/runtime-uia-20261105.md：UIA 树、窗口/模块、稳定 ID 候选、失败与边界。
- docs/research/smtc-20261105.md：原始 SMTC 样本、时间/速率语义、生命周期与测试。
- docs/research/lyrics-source-policy.md：离线匹配、双开关授权、缓存与已知元数据限制。
- README_RUNTIME.md：实际运行命令和本阶段支持边界。


## 2026-11-05 第二次续作：打开的歌词面板与内部按钮

本节更新上次“尚未挂接按钮/尚未提取正文”的状态，保留历史记录。用户已打开 Apple 原生歌词面板；它不等于本项目新建的桌面悬浮窗。测试中“保持隐藏”仅指本项目自己的悬浮窗。

### observed

- 用户把 Apple Music 和聊天窗口放在虚拟桌面二，在桌面一学习。采样与测试未切换桌面、未激活或移动 Apple 窗口。
- 主 HWND 的 UIA 树在另一虚拟桌面可能只剩外壳；从同 PID、已验证原生层级的 Microsoft.UI.Content.DesktopChildSiteBridge / InputSiteWindowClass 直接进入 XAML，可读取 TransportBar 和 TimeBasedLyrics。已接回专用播放器适配器。
- 《天黑黑》读到 38 个 Apple 歌词行；2026-11-05 19:12（Asia/Shanghai）的三次采样，原始进度 58、59、60，面板滚动 29.6667 → 33.375。证据 lyrics-properties-1793877151616.json 保留哈希和属性，不转录整首歌词。
- 当前探测的歌词按钮无行时间戳和 SelectionItem 标记；不以滚动、几何中心或行索引推算当前行。另桌面的渲染探测返回空白，不能用于可靠高亮识别。
- SMTC 读取继续正常，其他播放器并存时仍只选 Apple；中途曾失去会话，用户恢复后再次连接成功，没有由工具发送播放命令。

### 新实现与真实挂接

- apple_window.py 原生枚举/核验目标与内容 HWND，支持当前包在 WindowsApps 不同卷上的路径映射；不把旧 PID/HWND 固化为配置。
- apple_lyrics_bridge.py + bridge/apple_lyrics.ps1 后台只读正文和布局；观察仅存内存，不输出完整歌词到日志。超时/失败清空快照，子进程受控回收。
- native_button.py 自有 Win32 容器与标准 BUTTON，以 WS_CHILD 创建在 Apple 主 HWND 中；标准按钮 WM_COMMAND 进入本程序自己的 WndProc。没有 DLL 注入、远程补丁、Apple WndProc 子类化或 SetParent。
- button_layout.py 在 RepeatButton 与 LCD 的已观察空隙中找位置，检查碰撞、DPI、客户区、快照和窗口几何是否仍一致；条件不满足即撤下自有按钮。
- scripts/verify_apple_attachment.py 已对真实 Apple 父窗验证：子按钮属于宿主 PID，父子链正确，向自有按钮 PostMessage(BM_CLICK) 回调一次；前台 HWND 不变，Apple cloaked 状态不变，歌词窗隐藏，两个新 HWND 和辅助进程退出后消失。此验证不等价于真实鼠标命中或可见悬浮窗验证。
- 实测证据 native-apple-attachment-20261105-193747-831863.json；本程序新按钮图 native-button-20261105-193747-542265.png 可辨认“桌面歌词”。
- run_integrated.py 是一个隐藏宿主入口；只在前台 Apple 的按钮点击后显示新悬浮窗。会话内在线同步需在窗内明确确认；默认不开网络。无需另开控制程序。

### 同步与外部来源

- apple_lyrics.py 解析未计时正文，明确返回无 Apple 原始时间轴。
- apple_external.py 使用外部候选的 artistName/albumName 重建已观察副标题，严格检查标题、版本、时长及唯一候选，解决 SMTC 合并 Artist 字段；不猜拆和改写原始状态。
- lrclib_search.py 仅在允许网络且满足 Apple 特征时使用官方 /api/search?track_name；其余元数据走原 /api/get 路径。已核对官方 docs.md，工具未请求真实歌词 API；测试均 mock。
- apple_preferred.py 优先 Apple 原始计时接口；不可得时用外部同步结果。Apple 完整非空行序列与外部逐行严格一致时才组合 Apple 文本和外部时标，并保留 apple_music+external_timing 来源。
- 同歌新来源触发刷新，迟到旧请求不覆盖当前结果。无同步数据时不显示虚构时间轴。

### 虚拟桌面与显示边界

- virtual_desktop.py 仅提供公开 COM 读桌面 ID / 移动自身窗口的辅助接口和临时线程 DPI context。
- 从未显示的工具窗口不被 Shell 登记，查询返回 0x8002802B。对本程序临时隐藏窗口的试验在验证阶段失败，已销毁，不记作成功移动；没有移动任何用户窗口。
- 生产显示路径只允许当前前台 Apple 的用户点击，不依赖该移动接口，不在后台或另一桌面自动弹窗。跨桌面置顶/实际显示行为尚需真实点击验证，不能声称已固定到所有虚拟桌面。

### 验证与当前运行

- python -m compileall -q src tests scripts：通过。
- python -m unittest discover -s tests -q：219 项，217 通过，2 项隐藏窗口桌面 ID 测试跳过。
- 隐藏整合入口 --duration-seconds 4：正常挂接并回收，日志无错误。
- 三名 gpt-6.1-sol 子 agent 均已完成并关闭；其历史报告的计数是各自交付时快照，以本节整合计数为准。
- 2026-11-05 本轮以 pythonw 启动默认离线隐藏宿主（启动 PID 39060），只用于本次运行；未安装开机自启、未改注册表或全局配置。当前实际存活及挂接以工作日志为准。
- 未改 WindowsApps、未安装依赖、未做 DLL 注入、未请求真实歌词 API。正式签名、安装、自启动、多版本验证与真实授权后的联网同步测试尚未完成。

使用说明：README_INTEGRATED.md。先前 README_RUNTIME.md 对应独立原型入口，最新单入口实现以此节为准。


## 2026-11-05 续作：按钮不可见纠正与恢复诊断

### 用户证据与撤回结论

用户提供普通主窗口截图，顶部目标空隙没有“桌面歌词”。原 WS_CHILD 句柄、父子链、模拟回调等结构测试仍是事实，但不足以证明实际可见。撤回“该结构已经让按钮在 Apple Music 画面可见”的推论。

按钮现改为本进程自有 WS_POPUP + WS_EX_TOOLWINDOW + WS_EX_NOACTIVATE 的贴附层，owner 指向已验证的 Apple 主窗口；标准 BUTTON 在自有容器内。它只覆盖原测得的内部空隙，随 owner 的几何/最小化/虚拟桌面状态隐藏；Z 序立即位于 Apple owner 上方、覆盖应用之下，不设 TOPMOST、不激活用户窗口。此方案是贴附绘制，不是 Apple 进程内新增 XAML 控件；没有 DLL 注入或安装文件补丁。

### 另一项验收缺陷

button-onscreen-20261105-201655-021212.json 曾因 dark recall=1 报 passed，但参考与实际截取都是纯黑（4680/4680 像素暗），该结论无效。原始文件保留，新增 button-onscreen-review-* 明确撤回为 inconclusive。

screen_verification.py 现在先检查参考和实拍的亮度变化、暗文字与亮背景，再检查暗/亮区域对应；全黑、全白、近常量和大块遮挡均不能通过。verify_button_screen.py 已接入。未取得可信像素证据时不得报告“可见性已通过”。

### 实际交互与中断现场

- 旧宿主 PID 39060、26132 通过向本程序自己核实过的 Tk 主窗口发送 WM_CLOSE 正常替换，没有结束 Apple Music。
- 20:14 的新宿主日志记录 20:17:24 online_sync_authorized_by_user，20:17:34 host_stopped_cleanly。说明存在在线同步授权交互；日志没有查询结果与关闭触发点，不能补推退出原因或歌词成功。
- 用户随后中断并要求继续；恢复时没有集成宿主进程。没有把已退出那次运行限定的网络授权扩展到新运行。

### 诊断改进与验证

- ApplePreferredLookup 记录 lookup started/finished、来源、行数、耗时和诊断代码，不记录整段歌词或凭据。
- LiveSession 把 disabled/timeout/network_error/no_synced_match 等映射为明确的中文状态，并在重试时恢复“正在查找同步歌词”。未知诊断文本不直接显示。
- 新宿主记录 overlay opened/hidden 与 host close requested reason；有限时自检标记 verification_timeout，Apple 主窗口持续消失标记 apple_window_unavailable。
- compileall 成功。全项目 241 项，239 通过、2 项因未显示窗口的 Shell 桌面登记限制跳过。
- --duration-seconds 4 的最新整合检查：读到真实歌曲 Bloody Paradise；默认 allow_network=False，查询立即返回 disabled（0 ms），按钮层准备成功；4 秒后 reason=verification_timeout 正常回收。没有发真实歌词 API。
- 本轮一个 gpt-6.1-sol 子 agent 完成像素验收模块及 10 项纯离线测试，已关闭。生产显示/真实外部同步仍需实际运行结果，不能以单元测试代替。
