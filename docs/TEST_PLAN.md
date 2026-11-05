# 测试计划

## 已自动化

- LRC 元数据、重复时间戳和小数时间解析。
- 无时间戳文本忽略。
- 同步器在第一句前、句中、句间和偏移后的行为。
- IPC 消息 JSON round-trip。
- 播放状态 track key 规范化。
- SMTC bridge 的明确能力边界。
- Apple Music 适配器拒绝注入调用。

## 手工验证

- Apple Music 播放、暂停、切歌、拖动进度。
- Windows SMTC 是否提供 Apple Music 会话和位置。
- 悬浮窗置顶、拖动、鼠标穿透和多显示器。
- Apple Music 关闭和重启后的服务恢复。
- 未知 Apple Music 版本的安全降级。

## 验收命令

```powershell
$env:PYTHONPATH = "src"
python -m compileall -q src tests
python -m unittest discover -s tests -v
python scripts/run_demo.py --lrc examples/demo.lrc --duration-ms 6000 --step-ms 1000
```
