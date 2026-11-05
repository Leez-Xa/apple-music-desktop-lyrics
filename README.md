# Apple Music Windows 桌面歌词原型

这是一个面向 Windows 版 Apple Music 的模块化桌面歌词原型。当前实现优先保证可验证性和安全边界：核心歌词解析、逐行同步、IPC 消息模型、模拟播放源和 Tkinter 悬浮窗已经提供；Apple Music 的 SMTC 和内部按钮注入保留在明确的适配边界之后。

## 当前状态

- 已实现：统一播放/歌词/同步数据模型。
- 已实现：LRC 解析，支持重复时间戳、毫秒、元数据和结束时间。
- 已实现：按播放位置选择当前行、上一行和下一行，并支持延迟校准。
- 已实现：本地 JSON-lines IPC 原型。
- 已实现：模拟播放源，可直接运行同步演示。
- 已实现：Tkinter 置顶桌面歌词窗口原型，含拖动和 Windows 鼠标穿透占位。
- 已实现：Apple Music 进程只读侦察器。
- 未实现：Windows.Media.Control 的 WinRT bridge。
- 未实现：Apple Music 进程内 UI 注入；当前适配器明确拒绝注入调用。

## 运行

在项目根目录执行：

```powershell
python scripts/run_demo.py --lrc examples/demo.lrc --duration-ms 10000 --step-ms 500
```

运行测试：

```powershell
$env:PYTHONPATH = "src"
python -m unittest discover -s tests -v
```

本项目不依赖第三方 Python 包，不需要联网安装依赖。

## 目录

- `src/amlyrics/contracts.py`：公共数据模型和 IPC 消息类型。
- `src/amlyrics/lrc.py`：LRC 解析。
- `src/amlyrics/synchronizer.py`：逐行同步核心。
- `src/amlyrics/playback.py`：播放监控接口和模拟实现。
- `src/amlyrics/windows_smtc.py`：SMTC 适配边界。
- `src/amlyrics/overlay.py`：桌面歌词窗口原型。
- `src/amlyrics/apple_music_adapter.py`：Apple Music 安全侦察占位。
- `docs/`：架构、协议、兼容性和研究流程。

## 安全边界

当前不会修改 Apple Music 安装目录，不会注入 DLL，不会读取账号凭据，不会绕过 DRM，也不会下载或安装外部依赖。Apple Music 适配器只有在完成运行时证据记录后才允许进入下一阶段。
