# 按钮可见性最终审查 — 2026-11-05

## 结论

上一张 `button-onscreen-20261105-201655-021212.png` 是一个恒黑裁剪。它与恒黑参考图进行逐点比较时，所有参考“墨迹”位置当然也落在黑色像素上，所以旧记录中的 `onscreen_ink_recall: 1.0` 只表示黑对黑的退化匹配。它没有证明按钮文字、按钮边框或按钮表面被实际显示在屏幕上。后续复核文件 `button-onscreen-review-1793882324241.json` 已把原始 `passed` 降为 `inconclusive`，并明确记录“参考和屏幕都恒黑，暗召回率是假阳性；原始采集不证明可见文字”。原 PNG 与原 JSON 均保留，未覆盖。

## 三个实现的逻辑边界

### `scripts/verify_button_screen.py`

脚本的外层运行时门槛是：

- `find_apple_music_window()` 找到 Apple Music，且 `owner.cloaked` 为假、`owner.minimized` 为假。
- 通过 `EnumWindows` 找到当前 `--host-pid` 创建的、可见的 `AMLyrics.NativeButton.*` 容器，并要求它的 owner 是 Apple Music 的 `owner.hwnd`。
- 在相同 DPI 上下文中读取容器物理屏幕矩形，并找到其直接子 `BUTTON`。
- 对容器九个采样点调用 `WindowFromPhysicalPoint`，九点都必须命中本进程；否则记为 `button_region_is_covered` 并继续等待。
- 从子 `BUTTON` 采集 `expected`，从容器的物理屏幕矩形采集 `actual`；采集前后前台句柄必须不变，尺寸必须一致。
- 对中心点做一次 `WindowFromPhysicalPoint`，命中窗口的 PID 必须仍是 `--host-pid`。
- 最后才调用 `evaluate_screen_pixels`。脚本把无效参考和 `actual_insufficient_contrast` 归为 `inconclusive`，不会把恒定图当作成功。

这段逻辑有一个仍需注意的缺口：代码保存了采集前的 `foreground`，只检查采集后是否改变，没有明确检查采集前台是否就是 Apple Music（或其允许的前台祖先）。因此“前台稳定”不能单独等同于“Apple Music 当前在前台”。真正验收时应增加 Apple Music 前台身份检查，或在证据中明确证明前台句柄属于 Apple Music。

另外，九点命中只能证明采样点未被其他进程命中，不能单独证明矩形内每个像素都无遮挡；它应与实际屏幕裁剪的非退化像素证据一起使用。

### `src/amlyrics/screen_verification.py`

这是一个针对“浅色背景上的深色文字”的像素匹配器，不识别文字语义，也不处理平移、缩放或深色主题。参考图和实际图都必须满足：

- 明度范围至少 `100`；
- 至少 `21` 个暗像素，且暗像素至少占面积 `1%`；
- 至少 `21` 个亮像素，且亮像素至少占面积 `50%`。

实际图还必须与参考图逐点对齐，并达到：暗像素召回率 `>= 0.70`、背景召回率 `>= 0.90`、背景精确率 `>= 0.95`。测试明确覆盖了“恒黑实际图即使暗召回率为 `1.0` 也失败”，因为背景召回率和背景精确率为 `0`；恒黑参考图本身则应被判为 `reference_insufficient_contrast`，不具备有效参考资格。

因此，旧 JSON 只有 `expected_ink_pixels` 和 `onscreen_ink_recall`，没有 `valid_reference`、`capture_reason` 或完整像素指标，无法审计它是否通过了当前的非退化参考约束。它的旧 `passed` 不能作为当前验收依据。

### `src/amlyrics/native_button.py`

原生模块只保证本进程窗口对象的创建和生命周期契约：

- `NativeLyricsButton` 必须在 Python 主线程构造和操作；
- attach 前要求父 HWND 有效，父窗口线程/进程身份可识别，且调用线程与父窗口 DPI awareness context 相同；
- `child` 模式创建 `WS_CHILD | WS_VISIBLE` 容器，`owned_popup` 模式创建初始隐藏的 owned popup；标准 `BUTTON` 是容器的子窗口；
- `set_visible(True)` 只对自有容器调用 `SW_SHOWNOACTIVATE`，不会自动验证 Apple Music 当前可见、无遮挡或前台归属；
- `move()` 只改变自有容器并让按钮填满客户区；`close()` 只销毁本实例窗口；消息泵负责本线程消息和孤儿回收。

模块文档本身已经声明：成功创建不保证 foreign-host layout、rendering 或 input support。既有 `native-apple-attachment-*.json` 的 `passed` 记录证明了句柄、父链、回调、前台未变和清理；其中同时记录 `overlay_show_suppressed: true`、`apple_cloaked_before: true`，所以这些记录不是 Apple Music 中按钮可见的证据。较早的 native button 图像只能说明某次独立窗口截图中出现过按钮样式，不能替代 Apple Music 当前界面的无遮挡验收。

## 当前真正可见验收的必要条件

一次可接受的 `passed` 至少应同时具备以下证据：

1. **目标身份与状态**：Apple Music 的目标顶层窗口已找到，未 cloaked、未 minimized，并且采集时前台句柄明确属于 Apple Music；不得只记录“前台句柄前后相同”。
2. **窗口关系与归属**：容器和 `BUTTON` 都是本进程实时有效的 HWND；owner/parent 链、PID、矩形和 DPI 上下文与本次采集对应；按钮已实际显示，且不是仅有 `WS_VISIBLE` 标志。
3. **屏幕覆盖**：按钮物理屏幕矩形在当前桌面上；采样命中本进程，并由屏幕抓取证明矩形没有被 Apple Music 或其他窗口覆盖。九点命中是现有门槛，不能代替屏幕像素证据。
4. **非退化参考图**：从实际子按钮取得的 `expected` 不是恒黑、恒白、近似常量或仅有孤立异常点；它满足当前 `screen_verification.py` 的有效参考条件。若目标使用深色主题，应使用经过验证的深色主题匹配规则，不能套用当前浅底深字规则。
5. **非退化实际屏幕图**：`actual` 与 `expected` 尺寸、DPI/坐标语义一致；实际裁剪具有可解释的文字/图形暗信号和背景信号，不能只靠暗召回率。当前实现要求实际图通过明度范围、暗/亮信号、暗召回、背景召回和背景精确率全部阈值。
6. **捕获期间稳定**：采集前后 Apple Music 前台身份、按钮矩形、命中归属和桌面上下文保持一致；不得发生桌面切换、宿主窗口激活、Apple Music 激活或焦点窃取。
7. **可复核记录**：JSON 至少保存 `valid_reference`、`capture_reason`、完整 `pixel_metrics`、owner/container/button 句柄与 PID、物理矩形、前台身份、命中窗口 PID、图像路径以及桌面/激活状态。只有 `status: passed` 且这些字段共同自洽，才可作为可见验收。

## 本次审查范围与限制

本次只读审查了 `verify_button_screen.py`、`screen_verification.py`、`native_button.py`、相关测试、既有研究文档和 Apple Music 运行时证据；读取并目视检查了旧黑图及两张 native button 图。没有修改代码，没有运行会显示窗口的脚本，没有切换桌面，没有激活 Apple Music，没有联网，也没有执行测试或重新采集。报告只新增本文件和配套 JSON。