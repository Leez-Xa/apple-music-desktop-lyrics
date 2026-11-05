# Apple Music 只读运行时 UIA 侦察 — 2026-11-05

观察窗口：2026-11-05 18:38:37–18:42:25 +08:00（Asia/Shanghai）。研究和验证输出仅新建于 `docs/research/` 与 `research/apple-music/`。本报告供负责人整合；未修改 `apple-music-runtime.md` 或其他 runtime.md，未修改产品源代码、bridge 或公共接口。

## 决策结论

- **observed / high**：运行中的 Apple Music 是 PID `20072`，主窗口 `0x20B30`（133936）。完整 UIA 树 649 节点：XAML 638、Win32 11。播放器是 WinUI/XAML 可访问控件，具有可用 AutomationId 和只读 pattern 状态。
- **observed / high**：播放器显示标题 **裹着心的光**，副标题 **林俊杰 — 裹着心的光 - Single**。播放按钮为“播放”，`LCDScrubber` 的 RangeValue 为 minimum=0、value=166、maximum=273；多个被动快照间值未变。
- **inferred / medium-high**：当前暂停或未播放。该判断来自按钮语义和不变的滑块值，未查询 SMTC，不等价于音频引擎的确定状态。166/273 与秒制播放进度相符，但 UIA RangeValue 本身没有声明单位。
- **observed / high**：`LyricsToggleButton` 名称“歌词”、enabled=true、ToggleState=Off；`PlayQueueToggleButton` 也为 Off。当前暴露的树未识别出歌词正文面板或当前行节点。
- **unknown**：用户打开歌词页后是否能访问正文、当前行和时间轴。本次不点击，不打开歌词页，不读取整段歌词；因此不能把“当前状态不可用”扩大为“Apple Music UIA 永久不支持歌词”。
- **建议**：已观测 ID 足以支撑歌曲元数据、播放器按钮语义和原始进度值的只读 UIA 适配。歌词能力在当前状态应明确报告 unavailable/unknown。内部原生子按钮与可见空位尚未验证，不应声称已经实现或可稳定植入。

## 规则、范围与执行环境

已读取从盘符、用户目录、`.codex`、visualizations 日期层直到工作区的各层 AGENTS；实际存在并适用的是全局 `.codex/AGENTS.md` 和工作区 `AGENTS.md`。已递归检查 docs/research 与 research/apple-music 的目录层级，没有额外 AGENTS。已读取 `docs/RESEARCH_WORKFLOW.md`、当时 `docs/research/` 全部三份研究记录、`research/apple-music/evidence/README.md`。

工作区不是 Git 仓库，`git status --short` 返回 not a git repository。未使用 Git 恢复、清理或改写任何现场。本次没有派发子 agent。

外层 shell 为 PowerShell 7.6.5；UIA、user32 和模块采集实际运行于独立的 **64-bit 系统 Windows PowerShell 5.1.26100.9711 / .NET Framework**。UIA 工作为 STA。后续工具调用采用 `login:false`；本侦察未遇到 Fatal CLR/0x80131506，不把其他会话报告的故障计为本次故障。

没有窗口激活、恢复、移动、播放、切歌、点击、Invoke/Toggle/SetValue、截图、账户值读取、TextPattern 文本提取、远端网络、安装、下载、DLL 注入或 WindowsApps 写入。WindowsApps 仅作为运行进程和已加载模块的路径、版本元数据来源。原生枚举先检验 HWND 所属 PID，再读类名、矩形和顶层标题；未捕获其他进程 UI。未读取进程命令行。

## 客户端、窗口和模块

| 项目 | observed 值 |
|---|---|
| 进程 | AppleMusic / PID 20072，启动于 2026-11-05 16:25:42 +08:00 |
| 可执行文件 | `C:\Program Files\WindowsApps\AppleInc.AppleMusicWin_1.1792.39682.0_x64__nzyj5cx40ttqa\AppleMusic.exe` |
| 包目录版本特征 | 1.1792.39682.0 |
| 文件 / 产品版本 | 1.7.0.155 / 1.7.0.155 |
| 主窗口 | `0x20B30` / 133936 / `WinUIDesktopWin32WindowClass` / Apple Music |
| 主内容 HWND | `0x30B36` / `Microsoft.UI.Content.DesktopChildSiteBridge` |
| 内容输入子 HWND | `0x30B3A` / `InputSiteWindowClass` |
| 其他主窗口子类 | `InputNonClientPointerSource`、`ReunionWindowingCaptionControls` |
| 采集数量 | 20 个该进程 HWND（9 个顶层），253 个已加载模块 |
| 可识别后代进程 | 基于父 PID、启动时间，向下最多 4 层未发现后代；不是系统全量 WebView 进程盘点 |
| 前台状态 | 两次树快照均非目标进程前台；未尝试改变 |
| 主窗口状态 | user32 `IsIconic=true`；`IsWindowVisible=true` 不能解释为屏幕上可见 |

关键已加载模块：

- `Microsoft.UI.Xaml.dll`、`Microsoft.UI.Xaml.Controls.dll`：3.2.3.2609。
- `Microsoft.WindowsAppRuntime.dll`、`Microsoft.UI.Windowing.Core.dll`、`Microsoft.UI.Input.dll`、`Microsoft.UI.Composition.OSSupport.dll`、`dcomp.dll`。
- `Microsoft.Web.WebView2.Core.dll`：1.0.3719.77。
- `JavaScriptCoreV8.dll`：1.0.0.0。
- 同时加载 `Windows.UI.Xaml.dll`，不据此单独推断某个具体可见区域采用哪代 XAML。

**框架边界**：WinUI/XAML 主界面有窗口类、UIA FrameworkId、模块三类相互印证，可信度 high。WebView2 Core 和 JS 引擎“已加载”是事实；当前树没有 FrameworkId=Chrome/HTML/Edge 的节点或已识别 Document/WebView 页面边界。可推断客户端具有混合运行时成分，不能推断当前播放器由 WebView 渲染、存在可用 DOM、调试端口或脚本入口。未探测端口、DOM、Cookie 或浏览器资料。

## UIA 定位与当前状态

主窗口 → XAML 内容 Pane → `NavView` → `TransportBar`。应先重新按进程名和路径核对 PID，再取得该进程主 HWND；PID/HWND 和 RuntimeId 不是跨会话稳定配置。

| AutomationId / 上下文 | ControlType / ClassName | pattern 或只读状态 | 定位评价 |
|---|---|---|---|
| `TransportBar` | Group / LandmarkTarget | ScrollItem | 播放器公共父节点候选，完整树中唯一 |
| `TransportControl_PlayPauseStop` | Button / AppBarButton | Invoke、ScrollItem；Name=播放，enabled=true | 读 Name 与 enabled，不调用 Invoke |
| `TransportControl_SkipBack` | Button / AppBarButton | Invoke；Name=向后跳 | 只读候选，不调用 |
| `TransportControl_SkipForward` | Button / AppBarButton | Invoke；Name=向前跳 | 只读候选，不调用 |
| `ShuffleButton` | Button / ToggleButton | Toggle=Off | 只读 Toggle.Current |
| `RepeatButton` | Button / Button | Invoke；Name=不重复 | Name 受语言影响 |
| `LCD`，TransportBar 下 | Group / NamedContainerAutomationPeer | ScrollItem | 歌曲元数据 / 进度范围父节点，唯一 |
| `LCD/myScrollViewer/ScrollingText` | Text / TextBlock | Text、ScrollItem；本次仅定点读 Name | LCD 内有两个同名字段，分别为标题和艺人/专辑组合 |
| `LCDScrubber`，LCD 下 | Slider / Slider | RangeValue=166，min=0，max=273，IsReadOnly=false | 仅读 Current.Value/Minimum/Maximum；不能因非只读而写值 |
| `LyricsToggleButton` | Button / ToggleButton | Toggle=Off；定点 Name=歌词，enabled=true | 可判断当前开关；正文可访问性未知 |
| `PlayQueueToggleButton` | Button / ToggleButton | Toggle=Off；Name=队列 | 可判断当前开关 |
| `VolumeButton` | Button / Button | Invoke；Name=音量 | 未打开弹层 |

这些关键 ID 在完整树中均唯一，`ScrollingText` 例外：恰有两个，父级是不同 `myScrollViewer`。不能把 ScrollingText 当全局唯一 ID，也不能把下标、Name 的本地化文案或本次 RuntimeId 作为长期契约。两个元数据字段当前顺序和内容可观察，但未验证导航、歌曲切换、语言变化、缩放或重启后的布局。艺人和专辑仍是一个组合字符串，未验证通用分隔规则。

三次被动观察对应证据：18:39:51 树、18:40:31 完整树、18:42:25 定点字段。标题字段和歌词按钮名称只在最后的定点证据保留；广域树为保守保护文本而抑制 Text/Document/Edit/容器名称。歌词按钮的 Name 也因 ID 含 Lyrics 在广域树中抑制，因此应结合定点证据读取“歌词”。这不是前两次读不到 Name，也不是歌词正文的证明。

所有上述 XAML 交互节点的 `NativeWindowHandle=0`，控件由 XAML/UIA peer 暴露；user32 窗口树不提供一枚可直接复用的播放器按钮 HWND。

## 歌词与内部原生子按钮的可行性边界

**只读歌词适配**：当前 Off 状态下，完整树未识别出歌词正文容器和逐行节点。`SubtitlesRepeater`、`SubtitleTextBlock` 等是内容卡片/专辑副标题，不能仅凭名字含 subtitle 把它们当歌词。普通 Text 节点的 Name 被采集策略抑制，存在 TextPattern 也不构成歌词来源；本次没有调用其 DocumentRange/GetText。没有当前行、逐字高亮或逐行时间戳证据。负责人可基于已观察 ID 实现播放器状态读取，但歌词端应在缺证据时返回明确不可用状态。

**跨进程 WS_CHILD / 自有 WndProc**：本次未调用 CreateWindowEx、SetParent、SetWindowLong、SetWindowPos、SendMessage、窗口钩子或任何窗口创建/修改 API。该方案属于改变关联窗口状态的后续实验，不在本次只读任务中。

已知候选关联 HWND 是主窗口 `0x20B30` 或其内容宿主 `0x30B36`，并无可供直接替换/扩展的原生歌词按钮 HWND。自有 WndProc、跨进程父子关系、DPI、生命周期、消息路由、键盘/可访问行为、XAML/Composition 遮挡与重绘都未实测。标准 BUTTON 的父级通知与自定义控件自己的鼠标消息处理不能混为已验证的点击回调。

几何证据仅供描述：主窗口 user32 Rect=`[-21333,-21333,-21175,-21307]`（LTRB）；UIA `TransportBar` Rect=`[-31926,-31997,1893,96]`（XYWH）；歌词按钮相对 TransportBar 为 `[1448,23,66,48]`，队列为 `[1517,23,63,48]`，LCD 为 `[546,8,720,79]`。这些相对差值来自同次 UIA 树，**不是推荐插入坐标**。

主窗口已最小化，UIA 仍把一些播放器节点标成 `IsOffscreen=false`。离屏负坐标、user32 与 UIA 不同的矩形表达/比例、隐藏和虚拟化节点的 Infinity，使这些矩形不能用来判定可见空白或混用坐标系。本次没有 DPI/客户区/恢复位置和遮挡实测，没有截图；不能据按钮间隔推导可用空位，也不能保证新的 WS_CHILD 在 XAML 内容上可见。本阶段结论为 **unknown / 未验证，暂不触碰**。

## 证据索引

证据目录为 `research/apple-music/evidence/`。所有文件以 CreateNew 新建，不覆盖；SHA-256 汇总另见本次 evidence manifest。

| 文件 | 用途与可信边界 |
|---|---|
| `20261105T183837.3708048+0800-891306f2-inventory-snapshot.json` | 主进程身份、版本、253 模块、20 HWND、后代查询和窗口状态；errors=[] |
| `20261105T183951.8519855+0800-81268965-uia-snapshot.json` | 首次成功树：600 节点，其中 XAML 592；命中节点上限，不能作为完整树 |
| `20261105T184031.4897073+0800-e3cb2953-uia-snapshot.json` | 权威完整树：649 节点，XAML 638 / Win32 11；所有上限标志 false，节点及全局错误均为 0 |
| `20261105T184225.3417685-targeted-player.json` | 只定点查已知 TransportBar/LCD ID；保留两个元数据字段、播放按钮、进度、歌词和队列开关，不再扫描整树 |
| `20261105T183955.2231116-uia-supervision.json` | 首次成功独立 UIA worker，总超时 45 秒，exit=0，约 3.64 秒完成 |
| `20261105T184034.9928052-uia-supervision-expanded.json` | 扩大节点上限后的完整树 worker，45 秒总期限，exit=0 |
| `20261105T184304.4940247-validation.json` | 脚本解析、当时 10 份 JSON 校验、41 单元测试通过；首次 compileall 长路径错误和启动问题保留 |
| `20261105T184355.3118187-compileall-retry.json` | 使用进程内扩展路径后的 compileall exit=0 |

同前缀 `*-started.json` 是启动/身份检查点，不能当作采集成功。`20261105T183810.1436396+0800-81f362d4-inventory-started.json` 属于编译失败的实验启动记录，没有对应原生快照。

## 最终复用脚本与运行命令

**唯一选定的最终采集脚本**：`research/apple-music/observe-runtime-uia-readonly-20261105.ps1`。

SHA-256：`B92C2B42D1813A5478C6E414B1172DDE98F4A9AD11E3C0ECCBF443CF4150741C`。

其余 `observe-runtime-uia-readonly.ps1`、`observe-runtime-uia-readonly-v2.ps1` 为保留实验文件：前者直接 -File 启动受执行策略阻止，后者 CodeDOM 缺 System.Core 引用。都未删除、未覆盖；不要把它们用于最终复测。

最终脚本分 Inventory/Uia 两阶段，所有输出名含时间戳和随机后缀；先输出 started 检查点，成功后输出 snapshot。CodeDOM 仅把本观察器的 user32 声明编译进观察器自己的进程，临时文件限定在新建 `research/apple-music/work/<run>-native-compiler/`，不是将 DLL 加载进 Apple Music。

本机禁止直接执行 -File，未修改执行策略。以下启动配方从当前工作区运行（外层可为 PowerShell 7，UIA 始终在独立系统 5.1 STA 进程）。每次 `Phase` 选择 Inventory 或 Uia。MaxSeconds=35 是两次 provider 调用之间的协作期限；外层 WaitForExit(45000) 才是包含启动的硬期限，不能省略而声称无阻塞风险。

```powershell
$observer = Join-Path (Get-Location) 'research\apple-music\observe-runtime-uia-readonly-20261105.ps1'
$psi = [Diagnostics.ProcessStartInfo]::new()
$psi.FileName = 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe'
$psi.EnvironmentVariables['AM_OBSERVER_PATH'] = $observer
$psi.Arguments = '-NoLogo -NoProfile -NonInteractive -STA -Command "[Console]::OutputEncoding = New-Object Text.UTF8Encoding($false); $env:PSModulePath = Join-Path $PSHOME Modules; Import-Module Microsoft.PowerShell.Utility,Microsoft.PowerShell.Management,CimCmdlets -ErrorAction Stop; & ([scriptblock]::Create([IO.File]::ReadAllText($env:AM_OBSERVER_PATH))) -Phase Uia -TargetProcessId 20072 -MaxNodes 2000 -MaxSeconds 35 -CollectorPath $env:AM_OBSERVER_PATH"'
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.StandardOutputEncoding = [Text.Encoding]::UTF8
$psi.StandardErrorEncoding = [Text.Encoding]::UTF8
$worker = [Diagnostics.Process]::Start($psi)
$outTask = $worker.StandardOutput.ReadToEndAsync()
$errTask = $worker.StandardError.ReadToEndAsync()
if (-not $worker.WaitForExit(45000)) {
    $worker.Kill() # 仅本行代码刚刚启动的观察器，不是 Apple Music
    $worker.WaitForExit()
    throw 'Read-only observer exceeded 45 seconds; started checkpoint may be incomplete.'
}
$outTask.Result
$errTask.Result
if ($worker.ExitCode -ne 0) { throw "Observer exit code: $($worker.ExitCode)" }
```

PID 是本次示例；脚本在任何读取前验证 ProcessName 和 executable 文件名，PID 复用时拒绝其他进程。后续运行仍需重新核对，不依赖旧 HWND。PSModulePath 调整只在子进程内，解决外层 PS7 环境继承导致 Get-FileHash 无法自动加载的问题，不涉及全局配置。

最终脚本广域采集的 Text/Document 名称默认抑制，并不自动保存歌曲字段。定点证据的查询算法是：主 HWND → AutomationId=TransportBar → LCD → FindAll(ScrollingText)，仅对其中两个非密码、同 PID 的元数据节点读 Name（最多 96 字符）；另按相同 TransportBar 下的四个已观测 ID 读播放按钮 Name、滑块 Current 和两枚 Toggle.Current。未调用 TextPattern/ValuePattern 内容接口；本次定点 worker 设 15 秒总期限，成功返回，没有再扩大树扫描。

## 验证、失败与限制

- 最终脚本由系统 5.1 实际成功运行 Inventory 与两次 Uia；最终脚本语法解析 0 错误。
- 完整树 errors=[]，每节点 errors=[]，未触发节点/深度/时间限制；这只表示本次 provider 暴露的树遍历完成，不代表所有隐藏、虚拟化、其他页面和未来状态均已发现。
- `python -X pycache_prefix=<research 内新目录> -m unittest discover -s tests -v`：41 tests，OK。现有 IPC 测试只使用本机 loopback，无远端网络。源码由负责人并行演进，该结果对应验证时刻，并非冻结版本验收。
- `python -X pycache_prefix=<research 内新目录> -m compileall -q src tests`：第一次因缓存路径过长 exit=1，已保存；对相同项目检查使用 `\\?\` 扩展绝对缓存路径重试 exit=0。未改系统长路径设置，未往 src/tests 写缓存。
- 初始 -File 执行策略拒绝、v2 CodeDOM 编译失败、第一次独立 UIA worker 模块发现失败均已如实记录。后续成功结果不能抹掉这些启动限制；未出现被观察应用崩溃或 UIA provider 超时。
- 不保存歌词段落；本次连当前歌词行也没有获取。元数据候选与控件标签做长度限制/邮箱脱敏，密码和编辑值不采集。
- 模块枚举不能证明具体渲染路径、内部 DOM、API、Hook 或扩展点。UIA pattern “支持”不等于本次执行过动作。
- 当前没有活动播放、恢复窗口、打开歌词、切歌、语言/DPI/重启验证；不能声称歌词同步、稳定布局或内部按钮已可用。
- 接口影响：**无**。仅提供研究脚本、证据和报告；`src/amlyrics/apple_uia.py` 与 `bridge/apple_uia.ps1` 由负责人负责，本侦察不写这些路径。