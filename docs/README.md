# Apple Music 桌面歌词

Windows 原生置顶歌词窗口，使用系统媒体会话读取 Apple Music 当前歌曲与播放位置。默认自动从 LRCLIB 匹配真实同步歌词，无需导入。已获用户授权，仅向 LRCLIB 发送歌名、歌手、专辑和时长，不发送音频、账号或令牌。不需要安装额外依赖。

## 开始使用

1. 在项目根目录直接双击 **`Start.exe`**（首次克隆源码如尚未构建，可先运行一次 `.\scripts\Build.ps1` 自动生成 `Start.exe`）。它会**无命令行黑框闪现**地自动关闭运行中的旧实例、重启到最新的完整构建并直接显示歌词浮层（若原本开启了待命守望，也会同步重启到最新版）。
2. 在 Apple Music 中播放歌曲，工具自动读取歌曲信息并匹配同步歌词。
3. 匹配成功后默认显示当前句与下一句；若在「设置 → 歌词」中开启“显示翻译”或“显示音译”且当前歌曲包含对应内容，则以两轨（原词 + 翻译或音译）或紧凑三轨（同时开启且均可用时：原词 + 音译 + 翻译）展示，随播放进度逐句切换。换歌后自动查询新歌。若手动隐藏了浮层，可用 `Ctrl+Alt+L` 或托盘菜单恢复。
4. 网络错误会稍后自动重试，也可点击“重试”。“本地备用”只在你希望手动指定歌词时使用，不是正常使用的前置步骤。没有时间戳的文本始终标为“仅有非同步歌词”。

也可以直接打开 outputs 最新 build-* 目录中的 AppleMusicLyrics.exe。同目录的 MediaSession.ps1 必须保留。构建目录是独立版本，构建不会覆盖旧版本。

## 随 Apple Music 自动待命与启停（可选）

每个新构建目录下还会生成纯原生待命守望程序 `AppleMusicLyricsWatch.exe` 与无黑框启动器 `AppleMusicLyricsStandby.exe`：

- 临时启动待命守望：`.\scripts\StartStandby.ps1`（发现 Apple Music 运行时按上次记忆状态拉起浮层；上次开启则立刻显示，上次关闭则仅保留托盘与快捷键；Apple Music 完全退出后自动收起歌词窗口并停止采集，同时保留原有开关记忆）。
- 管理或预览开机登录自启（不会在未确认前自动写入系统启动项）：
  - `.\scripts\ManageStandby.ps1 -Action Status`：查看当前待命状态与自启安装情况。
  - `.\scripts\ManageStandby.ps1 -Action PreviewInstall`：只读预览将写入的自启路径（指向原生 `AppleMusicLyricsStandby.exe`，不拉起 PowerShell）与撤销命令。
  - `.\scripts\ManageStandby.ps1 -Action Install` / `-Action Uninstall`：启用或彻底撤销登录自启并停止待命守望。

## 操作

| 操作 | 方法 |
| --- | --- |
| 移动窗口 | 解锁后拖动歌词区域 |
| 调整窗口宽度 | 解锁后拖动左右边缘；高度由歌词自动计算 |
| 显示悬浮工具栏 | 未锁定时，鼠标悬停或键盘聚焦浮层即可显示「⚙ / 🔒 / ×」 |
| 设置 | 悬停/聚焦后打开「⚙」，内含「显示」「歌词」「同步」三组（打开设置时悬浮工具栏会自动隐藏以免遮挡） |
| 校时 | 在「⚙ → 同步」中每次提前或延后 0.1 秒或 0.5 秒（范围 ±15 秒），按歌曲独立保存 |
| 换一版歌词 | 「设置 → 歌词 → 换一版歌词」；在同曲多个 LRCLIB 同步版本间切换并按歌曲记住选择 |
| 调整字号和颜色 | 「设置 → 显示」；字号 16–48，也可用 Ctrl + 加号 / 减号；内置调色盘支持 HEX/RGB 输入、16 格自定义颜色持久化（默认选中首个空槽、点击选格带黑框提示、支持清除） |
| 锁定 / 解锁（鼠标穿透） | 点工具栏「🔒」开启锁定并让歌词本体穿透鼠标；解锁可在鼠标移到歌词上时点击外侧浮出的「解锁」按钮，或使用系统托盘菜单 / `Ctrl+Alt+Shift+L` |
| 重新自动获取 | 「设置 → 歌词 → 重试」；会清除该曲的临时未匹配缓存 |
| 显示翻译 / 音译 | 「设置 → 歌词」勾选「显示翻译」「显示音译」；单开一种为双轨，同时开启且可用时为紧凑三轨，状态自动保存 |
| 本地备用 | 「设置 → 歌词 → 本地备用」或 Ctrl+O，选择 UTF-8 LRC / TXT |
| 隐藏 | 悬停/聚焦后点 ×，或用 Esc / Ctrl+H |
| 恢复 | 双击系统托盘图标、托盘菜单，或 Ctrl+Alt+L（再次运行 `Start.exe` 会直接重启浮层到最新构建） |
| 退出 | 托盘菜单或 Ctrl+Q |
| 键盘访问 | 设置面板和悬浮工具栏支持 Tab / Shift+Tab 切换控件，Enter / 空格激活按钮，Esc 关闭设置或隐藏浮层 |

`Ctrl+Alt+L` 或 `Ctrl+Alt+Shift+L` 若已被其他应用占用，会在「设置 → 歌词」状态详情中提示；此时仍可通过系统托盘菜单恢复显示或解锁，也可双击 `Start.exe` 重启浮层。快捷键只在当前程序存活期间注册，不写入系统配置。超长单句保持用户设置字号不缩小，超出宽度时铺满歌词框并在单行内平滑横向滚动展示；关闭动画时以省略号表示溢出，不出现滚动条。

## 歌词规则

- 默认按歌名、歌手和时长匹配，专辑用于优先选择同版本；时长相差超过 3 秒的记录不会自动采用。支持华语歌简繁转换及 CJK 歌手名后的英文别名。
- 时间计算保留小数秒，播放位置每约 150 ms 读取一次，窗口每 50 ms 检查换行；内置默认校准基准但界面只显示当前歌曲的相对偏移（默认显示 `0.0 秒`），手动微调在此基础上每步增减 `0.1 秒` 或 `0.5 秒`，不会重复叠加。校时与 LRCLIB 偏好版本均按歌曲独立保存于 `work/state/settings.json`（校时范围 ±15 秒），跨重启保持记忆且互不影响；“恢复默认”仅清除当前歌曲的调整。
- 自动歌词成功结果在内存缓存，未匹配结果短期缓存，避免重复请求；不向歌词服务上传文件或发布歌词。网络错误会有限次自动重试，并遵守服务端限流等待。

- 导入文件只关联导入时的当前歌曲。标题、艺人、专辑或媒体来源变化会重新选择歌词；旧请求即使迟到，也不能覆盖新歌曲。
- 当前版本的导入关联只保存在内存中，退出后需要重新导入。不会复制、改名、修改或删除用户的歌词文件。
- LRC 支持多时间戳、秒的小数、全局 offset、乱序排序及同时间戳翻译合并。正 offset 使歌词提前，负 offset 使歌词延后，负的最终时间截为零。
- 2 MB 以上文件会拒绝读取。无法识别的编码会提示另存为 UTF-8，不会静默破坏中文。
- 纯文本、时间轴不可用、媒体断开和获取失败均有独立提示；没有可靠时间轴时停止同步。
- 自动歌词标注 LRCLIB 来源；本地备用显示导入文件名。系统未提供唯一歌曲 ID 时，相同来源、标题、艺人与专辑的录音无法进一步区分。

## 本机构建与检查

在项目根目录的 PowerShell 中运行：

```powershell
.\scripts\Build.ps1
.\scripts\Test.ps1
# 在线提供器的离线模拟检查（不向服务发出请求）：
.\scripts\TestOnline.ps1
# 使用实际 Windows PowerShell 检查小数秒计算：
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\TimelinePrecision.ps1
# 将回归窗口放在已经存在的第二桌面：
.\scripts\Test.ps1 -DesktopNumber 2
# 使用真实播放状态验证完整链路，不发送播放控制命令：
.\scripts\VerifyPlayback.ps1 -BuildDirectory '完整的 build-* 目录路径' -DesktopNumber 2
.\scripts\Integration.ps1 -BuildDirectory '完整的 build-* 目录路径'
.\scripts\Probe.ps1
# Stage 2 多轨/原生融合/调色盘回归与 T4 生命周期回归：
.\tests\Stage2Regression.ps1 -BuildDirectory '完整的 build-* 目录路径'
.\tests\LifecycleRegression.ps1 -BuildDirectory '完整的 build-* 目录路径'
```

VerifyPlayback.ps1 将本次创建的验证窗口放到指定的已存在桌面，保持当前桌面不切换。它自动获取当前歌曲的真实 LRCLIB 同步歌词并核对实际播放位置；未获取真实同步歌词时检查失败。结束后自动退出，日志与截图位于新的 work/live-*。不会移动 Apple Music 或其他已有窗口，也不会自动新建桌面。

Build.ps1 每次新建 outputs/build-* 目录。Test.ps1 默认先构建，然后执行逻辑与 WPF 检查，记录和截图放在新建的 work/tests-*；可用 `.\tests\OverlayRegression.ps1` 与 `.\tests\PreferencesRegression.ps1` 分别复核像素排版和持久化。Integration.ps1 对指定构建中的 MediaSession.ps1 执行只读媒体采集集成测试并验证退出后无孤儿工作进程。Probe.ps1 只读检查媒体会话与可访问性树，不操作 Apple Music。

本机已验证可用的构建路径是 Windows 自带 .NET Framework 4.x 的 x64 C# 编译器和 WPF；不要求 .NET SDK。媒体读取使用 Windows PowerShell 5.1 与系统 WinRT API。运行脚本时的 ExecutionPolicy Bypass 仅影响该子进程，不改变用户或系统执行策略。若其他电脑缺少这些组件，应先记录缺失情况，经授权后处理安装；本项目不会自动安装。

## 原生歌词融合与当前限制

- 若当前歌曲在 LRCLIB 与 Apple Music 右侧「歌词」面板（同桌面未隐藏状态）均可用，程序会自动通过只读 UI 自动化接口提取 Apple Music 原生官方翻译与音译，融合到 LRCLIB 精确毫秒时间轴上并在会话内缓存；随后切回其他虚拟桌面时，仍可继续同步显示该曲已缓存的翻译与音译。
- 若关闭或未能匹配 LRCLIB 同步歌词，且同桌面 Apple Music「歌词」面板处于展开状态，也会实时只读跟随 Apple Music 原生歌词进度。
- **跨虚拟桌面限制（实机已验证）**：当 Apple Music 窗口位于另一个虚拟桌面（被 Windows DWM 隐藏 `Cloaked`）时，WinUI 3 会隐藏其 XAML 内容子树（仅暴露标题栏，无法跨桌面直接读取 `TimeBasedLyrics`）。切回同一桌面并展开歌词面板后即可恢复自动融合与缓存。LRCLIB 覆盖范围、版本和时间戳质量仍会影响匹配结果；未找到可靠匹配时会如实提示，不拿其他版本或纯文本伪装同步歌词。

当前界面与架构说明见 [DESIGN.md](../DESIGN.md)。
