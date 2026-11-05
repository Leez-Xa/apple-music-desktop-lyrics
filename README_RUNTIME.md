# 真实 Apple Music 播放模式（2026-11-05）

当前阶段：已经接通真实 Windows SMTC、只读 UIA 与置顶歌词窗口；Apple 歌词正文与客户端内“桌面歌词”按钮尚未完成。所有操作从这个交接项目目录执行。本轮未安装依赖、未修改 Apple Music 安装目录或持久系统配置。

## 环境

Windows、系统 Windows PowerShell 5.1、Python 3.11 或更新版本（含 Tkinter）。本机 Python 3.13.1 已验证。原 pyproject.toml 声明 >=3.10，但已有 contracts.py 使用 StrEnum，实际至少需要 3.11；为保留用户原配置，本轮记录该差异，未覆盖配置文件。

## 运行真实状态的窗口


```powershell
Set-Location '<project-root>'
python scripts/run_live_overlay.py
```

窗口显示真实歌曲、播放/暂停和当前进度。没有已取得的同步歌词时明确显示“暂无同步歌词”，不会把示例歌词套在真实歌曲上。可拖动、调整字号、隐藏或退出；Escape 隐藏后，本阶段可重新启动恢复，内部按钮尚未接入。

退出时会停止自己启动的 PowerShell bridge。原示例脚本和 windows_smtc.py 边界仍保留；新真实入口使用 smtc_bridge.SmtcBridgeMonitor。

## 只读诊断和有限时验证

```powershell
python scripts/run_live_overlay.py --diagnose
python scripts/run_live_overlay.py --headless-seconds 3
python scripts/run_live_overlay.py --duration-seconds 3
$env:PYTHONPATH = 'src'
python -c "from amlyrics.apple_uia import AppleMusicUiaProbe; print(AppleMusicUiaProbe().inspect())"
```

UIA 仅读取已观察控件 ID；当前文件/包目录版本不符合已验证版本时返回 unsupported。不点击、恢复、切歌或改变播放进度，原始滑块单位保留。

## 本地时间轴验证

用户可提供自己的 LRC，通过 --local-lrc、--match-title 和 --match-artist 显式绑定歌曲；必要时同时提供 --match-album。传入值需要匹配 SMTC 原始字段，Artist 可能包含“艺术家 — 专辑”。这些参数用于测试，正式自动歌词不应依赖用户手工绑定。

## 外部来源

LyricsResolver 的顺序为 Apple callback → 显式本地映射 → 可选 LRCLIB。Apple callback 尚未接入实际 Apple 歌词；外部解析/级联已用离线 fixture 验证，尚未联网实测。

默认完全关闭外部歌词请求。只有用户授权后，运行入口显式加 --allow-external-lyrics 才会同时打开两个开关；请求只包含曲名、艺术家、专辑和时长。保留歌曲版本信息，时长容差默认 2000 ms，只接受 syncedLyrics。当前 Artist 合并字段可能导致精确匹配失败；没有自动拆分或模糊放行。

## 验证结果

82 项单元测试通过；compileall 通过；真实播放模式窗口和无窗口模式运行通过；GUI 置顶、暂停、隐藏/恢复及退出清理通过。SMTC 真实播放位置递增与真实暂停位置固定已有证据。尚未主动操作真实播放/拖动/重启和多播放器竞争。

```powershell
$env:PYTHONPATH = 'src'
python -m compileall -q src tests scripts
python -m unittest discover -s tests -v
python scripts/verify_live_overlay.py
```

## 下一步

恢复 Apple Music 主窗口，播放有同步歌词的歌曲并打开歌词面板后，继续只读采集当前行/时间轴和可见布局证据。之后再验证 Apple provider 与内部原生按钮。自动启动、签名、打包均尚未完成；当前单一真实入口用于开发验证，尚未达到“用户只在 Apple Music 内点按钮即可”的最终交付标准。

详见 docs/research/apple-music-runtime.md 及其关联证据。


## 后续更新：内部按钮整合入口

2026-11-05 已新增并实挂内部原生按钮，单一隐藏入口为 scripts/run_integrated.py。使用方法、在线同步会话授权和已验证/未验证边界见 README_INTEGRATED.md；保留上文独立窗口原型命令用于调试。

