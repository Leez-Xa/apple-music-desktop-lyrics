# 架构

```text
Apple Music
   │
   ├─ future: Windows SMTC bridge ──> PlaybackState
   ├─ future: safe UI adapter ──────> IPC command
   │
   ▼
LyricsService ──> LyricsResult ──> LyricSynchronizer ──> SyncFrame
                                                          │
                                                          ▼
                                                  DesktopLyricsOverlay
```

## 模块边界

- `contracts.py`：跨模块稳定类型。任何 IPC 或公共字段变更先更新这里和协议文档。
- `playback.py`：播放监控接口和模拟实现。
- `windows_smtc.py`：Windows SMTC bridge 的唯一入口；当前只提供能力诊断和测试注入点。
- `lrc.py`：纯函数式 LRC 解析，不访问网络、不访问播放器。
- `synchronizer.py`：时间轴选择，不负责获取歌词或绘制 UI。
- `service.py`：把播放状态、歌词查询和同步器串起来。
- `overlay.py`：独立 Windows 顶层窗口原型，不直接读取 Apple Music。
- `apple_music_adapter.py`：只读进程侦察，未来承载版本化适配器。
- `ipc.py`：当前使用本机 TCP JSON-lines；正式 Windows 版本可替换为命名管道，消息模型不变。

## 数据流

1. 播放监控产生 `PlaybackState`。
2. 服务以 `track_key` 识别换歌并调用歌词查询。
3. 查询结果转为 `LyricLine` 时间轴。
4. 同步器使用 SMTC 播放位置选当前行。
5. 悬浮窗只消费 `SyncFrame`。

这样可以在没有 Apple Music 注入时，用模拟播放源独立验证歌词逻辑；注入失败也不会让歌词窗口直接访问播放器进程。
