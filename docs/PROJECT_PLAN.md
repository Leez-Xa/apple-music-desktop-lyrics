# 项目执行状态

## 已完成

- 建立 Python 标准库项目骨架和分层目录。
- 固定播放状态、歌词行、歌词结果、同步帧和 IPC 消息模型。
- 实现 LRC 解析：重复时间戳、百分之一秒/毫秒格式、`[offset:]`、元数据和结束时间。
- 实现逐行同步：当前行、前后邻居、暂停状态透传和全局偏移。
- 实现本地 JSON-lines IPC 原型。
- 实现模拟播放源和 CLI 演示。
- 实现 Tkinter 置顶悬浮窗原型。
- 实现只读 Apple Music 进程侦察器。
- 实现 SMTC 适配边界，明确未捆绑 WinRT bridge。

## 当前未完成

- Windows.Media.Control 的实际 WinRT bridge。
- Apple Music 当前歌曲到歌词源的实现和匹配缓存。
- Apple Music UI 技术栈和播放器控制栏的运行时证据。
- Apple Music 内部按钮适配器。
- 安装、自动启动、版本签名和正式打包。

## 下一阶段入口

先在 Windows 上启动 Apple Music 并播放一首歌，记录进程、窗口和 UI Automation 树，再决定 SMTC bridge 和按钮入口的实现技术。研究结果必须先写入 `docs/research/`，再允许修改 `src/amlyrics/apple_music_adapter.py`。
