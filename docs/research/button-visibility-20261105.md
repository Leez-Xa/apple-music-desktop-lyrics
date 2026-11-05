# 按钮可见性证据与 owned popup 隐藏验证 — 2026-11-05

## 结论与证据纠正

用户提供的截图证明：此前 WS_CHILD 方案虽然成功创建 HWND，按钮在 Apple Music 实际界面上仍不可见。截图结论由本任务上下文提供，本分支未重新截图或操作 Apple Music。此前的句柄、PID/TID、父子链、回调、几何和生命周期验证，不能代替屏幕像素可见性验收。

既有 `docs/research/native-button-20261105.md` 的 18 项测试使用隐藏模拟父窗，报告本来已将 Apple 可见性列为 unknown。不能把“18 tests OK”“HWND 存在”“WS_VISIBLE 已设置”或结构查询成功，转述为“Apple 内按钮已显示”。本分支保留既有文件，不覆盖其内容。

负责人正在 `native_button.py` 与 `integrated_app.py` 中实现 owned popup：只在 Apple 内部已测空隙放置本进程拥有的顶层容器，标准 BUTTON 仍作为该容器的子窗口。其目标是避开宿主 WinUI 合成覆盖；根因尚未由本分支做渲染跟踪，因此不把合成覆盖认定为已独立证明的唯一原因。没有 DLL 注入，不修改 Apple 文件、样式或代码。

## 接口与新增测试范围

约定接口为 `NativeLyricsButton(callback, text, *, placement_mode="child" | "owned_popup")`。默认 `child` 保持兼容。`owned_popup` 的 `attach(parent_owner_hwnd, screen_rect)` 创建时隐藏，矩形为 `(screen_x, screen_y, width, height)`，支持负坐标。保留 `placement_mode`、`container_hwnd`、`button_hwnd`、`move`、`close`。`set_visible(bool)` 仅对自有容器发出 `SW_SHOWNOACTIVATE` 或 `SW_HIDE`。

新增 `tests/test_owned_button.py` 只验证新模式的差异与必要兼容契约，不复制原 18 项通用测试：

- 默认和显式 `placement_mode` 可查询；纯构造不创建窗口。
- 容器样式带 `WS_POPUP`，不带 `WS_CHILD`，扩展样式带 `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`；用独立 User32 查询 `GetWindow(container, GW_OWNER)`，确认 owner 为隐藏自有夹具；BUTTON 的 parent 是自有容器。
- 自有 owner、popup、BUTTON 的 HWND 属于当前进程、当前主线程；初始 popup 无 `WS_VISIBLE`，没有显示调用。
- 初始定位和后续移动采用屏幕坐标，包括负坐标；非零位置、带边框 owner 避免误把客户区坐标当成屏幕坐标；owner 移动后再验证 popup 几何。
- mock 验证 `set_visible(True)` 使用 `SW_SHOWNOACTIVATE=4`、`False` 使用 `SW_HIDE=0`，且目标只为本实例容器。mock 返回零以覆盖“此前隐藏”，不把 ShowWindow 的零返回误当错误。
- 向本进程隐藏 popup 同步发送 `WM_MOUSEACTIVATE`，返回 `MA_NOACTIVATE=3`；没有真实鼠标输入或激活操作。
- `close` 释放两个自有 HWND 和实例表引用，保留 owner；隐藏 owner 销毁也应释放其 owned popup。

## 测试桌面安全边界

用户在桌面 1 学习，本次禁止任何可见测试 UI。所有真实 fixture owner 创建时不带 `WS_VISIBLE`，始终隐藏。Owned popup 是顶层 owned 窗口，不能依赖 owner 隐藏来假定其永远不可见，因此测试另做以下约束：

1. 在创建 fixture 前替换模块 API 与对应 User32 对象的 `ShowWindow`，所有 show/hide 请求只落到 mock，绝不转发真实调用。
2. 创建窗口时，除隐藏容器内的标准 BUTTON 外，拒绝任何带 `WS_VISIBLE` 的窗口创建，拒绝可见顶层窗口。
3. 包装 `SetWindowPos`，在进入真实 API 前拒绝 `SWP_SHOWWINDOW`、缺少 `SWP_NOACTIVATE` 或目标不是本测试创建的 HWND。
4. 阻止 ShowWindowAsync、ShowOwnedPopups、SetForegroundWindow、SetActiveWindow、BringWindowToTop、SetFocus；清理前继续核对所有有效 fixture 都不可见。
5. 不启动产品入口、不运行旧 GUI/show 验证脚本、不使用 Apple HWND、不进行 UIA 操作、不切换桌面、不联网安装、不派发其他 agent。

## 官方语义核对（2026-11-05，只读）

- [Window Features](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features)：WS_CHILD 的父子关系与 owned 顶层窗口不同；隐藏 owner 不应被当作禁止 owned popup 显示的测试保障。窗口样式和关系不构成屏幕无遮挡证据。
- [GetWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getwindow)：`GW_OWNER=4` 查询 owner；不能仅凭 GetParent 推断 WS_CHILD。
- [ShowWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-showwindow)：`SW_SHOWNOACTIVATE=4` 显示而不激活；返回值表示此前是否可见，零不等于调用失败。本次只验证调用参数，没有执行真实显示。
- [SetWindowPos](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos)：测试拒绝 `SWP_SHOWWINDOW` 并要求 `SWP_NOACTIVATE`。屏幕坐标契约另以 GetWindowRect 的真实结果验证。

只读取官方页面，未下载保存文件、安装依赖或调用远程写入接口。官方语义说明不是 Apple 兼容性实测。

## 执行状态与交付边界

首次审阅时主模块仍只有 child 接口；先完成证据审阅和测试编写，待负责人加入接口后执行。最后验证时间：2026-11-05T19:55:00+08:00。

实际结果：**9 tests, 0.035s, OK；0 failures / 0 errors / 0 skipped**。全部真实 fixture owner/popup 始终隐藏；ShowWindow 只到 mock。结束时两个实例注册表恢复基线；关闭窗口和销毁 owner 两条路径均释放 popup/BUTTON。

复现仅执行新增专项，不启动产品或 GUI 脚本：

```powershell
python -B -c "import sys, unittest; sys.path[:0] = ['src', 'tests']; unittest.main(module='test_owned_button', verbosity=2)"
```

语法检查使用 compileall 的 src/tests 遍历，但在当前检查进程用内存编译替代 py_compile 的磁盘缓存写入：**41 个 Python 文件通过，未创建/覆盖 pyc**。检查完成后编译钩子自动恢复，不改变配置或磁盘源文件。

按本次明确要求，不重跑既有 18 项测试，也不运行全量 discover（其中包含原 18 项），没有运行原 GUI/show 脚本。原 child 测试文件保持原样；这里只证明默认 child 属性兼容，不声称已复验全部 child 行为。负责人正在改动的 integrated_app 的前台/遮挡 gate 不属于本次专项覆盖。

写入范围仅新增 `tests/test_owned_button.py`、`docs/research/button-visibility-20261105.md`。不改动现有源文件、测试、验证脚本或既有研究文档。此目录没有 Git 仓库，未执行任何 Git 写操作。

本分支不验收真实显示、Apple 空隙布局、遮挡、鼠标命中、焦点、跨进程 owner、虚拟桌面归属或跨 DPI；这些需由负责人安排另一次用户允许的可见性验收。隐藏结构测试全部通过也不能替代该验收。
