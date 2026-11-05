# IPC 协议 v1

当前传输是本机 TCP JSON-lines，每条消息一行 UTF-8 JSON。正式 Windows 集成可以替换为命名管道，但消息字段保持兼容。

## Envelope

```json
{
  "version": 1,
  "type": "ToggleOverlay",
  "request_id": "optional-id",
  "payload": {}
}
```

## 消息类型

- `ToggleOverlay`：切换悬浮窗显示状态。
- `ShowOverlay`：显示悬浮窗。
- `HideOverlay`：隐藏悬浮窗。
- `PlaybackStateChanged`：推送标准化播放状态。
- `LyricsUpdated`：推送歌词来源、置信度和时间轴。
- `AppleMusicUnavailable`：适配器不可用或版本不支持。
- `Diagnostic`：传递可诊断信息。

## 约束

- `version` 当前为 `1`，未知版本必须拒绝处理并记录诊断。
- `request_id` 用于将按钮点击和服务响应关联起来。
- `payload` 只放对应消息的数据，不嵌套任意执行指令。
- IPC 不传递账号凭据、Cookie、音频数据或 DRM 内容。
