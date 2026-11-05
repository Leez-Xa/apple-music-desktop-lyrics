# Host lifecycle review — 2026-11-05

本次只新增了 `tests/test_host_lifecycle.py` 与本记录，未修改 `live_overlay.py`、`integrated_app.py` 或任何既有文件；未启动 Tk 窗口、Apple Music、后台宿主或 native Win32 button。未派发子 agent，所有工作由当前 `gpt-6.1-sol` agent 完成。

## 审查结论

当前修复满足目标行为：

- `LiveLyricsOverlay(..., close_host_on_exit=False)` 将“退出”按钮、`WM_DELETE_WINDOW`（窗口 X）和 `Escape` 都接到 `hide()`，因此悬浮窗只撤回显示，不调用 `session.close()`，也不调用 native button 的 `close()`。
- `IntegratedDesktopLyricsApp` 的显式 `close()` 仍保留完整回收路径：关闭 native button、停止 Apple lyrics bridge，再进入父类关闭流程。
- `_poll_native()` 在已经见过 Apple 窗口后，发现窗口暂时缺失时只关闭当前 native button，并持续安排下一次轮询。缺失超过 10 秒只记录 `host_kept_alive`，不会把宿主标记为关闭，也不会停止 session。
- Apple 窗口恢复后，轮询会按新的宿主句柄重新 attach owned button，并继续显示；测试使用 fake native button 验证了重挂调用与父窗口句柄更新。

## 新增测试

`tests/test_host_lifecycle.py` 包含三组行为检查：

1. 使用 fake Tk root/widgets 验证 `close_host_on_exit=False` 的退出按钮、窗口 X、Escape 都只触发隐藏。
2. 使用 object-free construction 与 mock 依赖验证显式 `IntegratedDesktopLyricsApp.close()` 仍关闭 native button、停止 Apple lyrics bridge，并进入父类 close 流程。
3. 使用 fake clock、fake Apple window、fake native button 和 fake Win32 API 验证 Apple 窗口缺失两个轮询周期（第二次超过 10 秒）后宿主仍存活，第三次窗口恢复时能够重新 attach。

## 验证命令

仅运行新增测试：

```text
python -m compileall -q tests/test_host_lifecycle.py
python -m unittest tests.test_host_lifecycle -v
```

`python -m compileall -q tests/test_host_lifecycle.py` 通过；`python -m unittest tests.test_host_lifecycle -v` 通过，3 项测试全部通过。没有运行项目全量 `compileall` 或全量 unittest，因此本记录不声称既有测试套件全部通过。

## 限制

这些测试是纯 mock 行为测试，未验证真实 Tk 消息循环、Windows `user32` API、Apple Music UIA/窗口生命周期、跨虚拟桌面行为、DPI 上下文或真实 native button 渲染与输入。超过 10 秒的场景通过 mock `time.monotonic()` 推进时间，没有实际等待。测试只审查当前 Python 控制流，不能替代在真实 Apple Music 客户端上的运行时验证。

