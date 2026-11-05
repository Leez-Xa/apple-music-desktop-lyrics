# 原生内部按钮原型 — 2026-11-05

## 交付结论

**observed / high**：只用 Python 标准库 ctypes，在当前进程创建隐藏模拟主窗口，成功验证自有容器及标准 BUTTON 的创建、主线程点击回调、位置/尺寸更新、父窗口销毁、失败回滚和资源引用清理。专项 **18 tests OK / 0 skipped**；全项目 **100 tests OK**。没有对 Apple Music 创建子窗口，没有读取/点击/恢复/移动/激活 Apple UI，没有切换虚拟桌面，没有显示自测父窗口。

**unknown / 未验证**：Apple Music 跨进程父子关系是否在目标版本中可接受、按钮可见性、XAML/Composition 遮挡、输入/焦点/可访问性、跨显示器 DPI、虚拟桌面切换及宿主退出后的跨进程销毁时序。此原型不是“已在 Apple Music 中可用”的验收证明。

仅新增以下四个文件，开始前已确认均不存在；后续迭代仅编辑本任务新建内容：

- `src/amlyrics/native_button.py`
- `tests/test_native_button.py`
- `scripts/verify_native_button.py`
- `docs/research/native-button-20261105.md`

未修改公共协议、`__init__.py`、现有配置或产品入口；未派发子 agent。开始时工作区不是 Git 仓库，无法用 git diff 证明范围，执行边界由排他新建四个目标文件及限定编辑保证。没有安装、下载文件、联网歌词服务或外部写入。只读查询了 Microsoft Learn；网络响应在内存解析，没有保存页面。

## 最小接口及主线程契约

```python
from amlyrics.native_button import NativeLyricsButton, NativeButtonError, pump_messages

# 以下代码由负责人在 Python 主线程集成；本分支未对 Apple 执行。
button = NativeLyricsButton(callback=on_toggle, text="桌面歌词")
button.attach(parent_hwnd, (x, y, width, height))  # -> 同一实例
button.move((new_x, new_y, new_width, new_height))  # -> None
button.close()  # -> None，幂等；只销毁本实例的自有容器/按钮
```

- `NativeLyricsButton(callback, text="桌面歌词")`：必须在 Python 主线程构造；后台线程构造会立即抛 `NativeButtonError`。callback 不接受参数，不在后台执行。
- `attach(parent_hwnd: int, rect: tuple[int, int, int, int]) -> NativeLyricsButton`：主线程调用；父 HWND 必须非零、有效，且其 DPI awareness context 与调用线程等价。已经挂接则拒绝重复创建；`close()` 后允许重新 attach。无效 HWND / DPI 不匹配 / Win32 失败会明确报错。
- `rect=(x, y, width, height)`：父 HWND 客户区坐标，按调用线程当前 DPI 坐标语义解释；尺寸必须为正整数，所有值在 signed 32-bit 范围内。既不是屏幕 LTRB，也不是直接接受 UIA 物理像素 BoundingRectangle。坐标换算和布局取样由负责人完成。
- `move(rect) -> None`：仅移动自有容器，并使自有 BUTTON 填满容器客户区。使用 `SWP_NOACTIVATE | SWP_NOZORDER`；不会移动父窗口。父窗口 resize 不会自动推导新布局，负责人应计算后再次调用 move。
- `close() -> None`：必须在主线程调用，重复调用安全；从不销毁传入父窗口，从不向宿主或消息循环发送 WM_QUIT。构造而未 attach 时 close 不加载 Win32 API。
- 只读状态：`container_hwnd`、`button_hwnd`、`attached`。生命周期结束后句柄清零；`attached` 是本实例状态，不是实时跨进程健康证明。
- `last_callback_error`：最近一次用户回调异常；`last_native_error`：WndProc 内部异常。异常被保留并阻止越过 ctypes 回调边界，调用方应检查/报告。回调应短小；同步耗时工作会阻塞主线程。
- `pump_messages(limit=100) -> int`：可选的非等待消息泵，分派**当前线程全部窗口**的消息，最多移除 limit 条，保留 WM_QUIT。若已有主线程原生消息循环，应由该循环正常分派，不要另启线程抽取本按钮消息。
- 每次 pump 还检查父 HWND 是否失效、PID/TID 是否变化、容器 parent 是否变化，并在所属主线程关闭孤儿。已有外部消息循环若不使用本函数，应安排主线程周期调用，或在自己的宿主生命周期观察器通知后调用 close。HWND/PID/TID 不是不可复用身份，不能据此保证彻底防止句柄复用竞态。

没有窗口枚举、进程发现或 Apple 路径识别逻辑；`apple_window.py` 及 parent 选择由主负责人独立负责。无需把其 UIA child HWND 自动当作本按钮的 parent；应由布局负责人明确传入已验证的父 HWND。

## 窗口和消息所有权

```text
调用方传入的 parent HWND（可为远程宿主；本分支仅测自有隐藏 parent）
└─ container HWND：本进程 / Python 主线程 / 自有 WndProc
   └─ BUTTON HWND：本进程 / Python 主线程 / 系统标准 BUTTON WndProc
       └─ BN_CLICKED -> WM_COMMAND -> 自有 container -> callback()
```

`CreateWindowExW(WS_CHILD)` 从创建起指定 parent，不调用 SetParent。不向 Apple HWND 写 WndProc/user data；不注入、不 hook、不 subclass 宿主，也不使用 DLL 构建或安装目录补丁。仅通过 ctypes 调用 Windows 自带的 user32/kernel32。

两级子窗均设置 `WS_EX_NOPARENTNOTIFY`，减少创建/销毁时向上级发送的 WM_PARENTNOTIFY；这不等于跨进程父子关系零影响或绝不阻塞。WM_COMMAND 只接受本 BUTTON 的 sender HWND、control ID=1、notification=BN_CLICKED=0。外部父窗不负责回调路由。

WM_SIZE 使 BUTTON 填满容器；WM_NCDESTROY 解除实例表强引用并清空句柄。活跃 HWND 期间用进程内表保留实例，以保证 Python 对象不会先于 WndProc 生命周期被回收。进程内仅注册一个随机名称的原生窗口类；类及其 WndProc 在进程结束前保留，避免反复注册类或过早释放回调。每次 close 不注销共享类，不属于窗口/实例泄漏，也不写全局配置。

## 跨进程支持条件与限制

1. **最低 API 条件**：Windows 10 1607+ 的 DPI context 查询/比较函数必须存在；ctypes 为系统标准库，无 Tk/第三方模块。导入模块在非 Windows 可完成，真正 attach 会拒绝；本机测试是 64-bit Python 3.13.1。
2. **DPI fail closed**：调用 `GetThreadDpiAwarenessContext`、`GetWindowDpiAwarenessContext`、`AreDpiAwarenessContextsEqual`，不同或未知即拒绝创建。模块不调用任何 DPI setter，不通过 SetParent 改变父子关系。微软 SetParent 文档的跨进程 DPI 强制重置表专指 SetParent；不能把它当作本原型 CreateWindowEx 的已验证行为，也不能仅凭避开 SetParent 宣称所有宿主无 DPI 风险。
3. **宿主前提**：负责人应确认父窗仍是预期进程/窗口类、处于允许交互的会话及 Win32 desktop、完整性权限允许，且宿主能响应。模块只核对传入窗口有效性、PID/TID 和 DPI，不提升权限、不绕过 UIPI，不替代 Apple 版本/布局/权限 gating。Windows 虚拟桌面与 Win32 desktop 是不同层面的概念，本分支不据此宣称跨虚拟桌面可用。
4. **阻塞边界**：本原型不创建工作线程；CreateWindowEx、跨线程父子关联和窗口消息分派可能进入同步 Win32 路径。`pump_messages(limit)` 只限消息数量，不是硬超时，PeekMessage/DispatchMessage 也可能分派同步消息。异进程父窗挂起时的时限和隔离由集成方设计，不能宣称 attach 始终非阻塞。
5. **布局/遮挡**：新建子窗默认 Z-order、宿主 XAML/Composition 重绘、父窗无 WS_CLIPCHILDREN 等均可能导致遮挡或覆盖；原型不修改宿主 style、不自动抬高窗口层级、不争抢焦点。成功返回 HWND 不证明屏幕可见。外观字体、键盘 Tab 导航、焦点转移和 UIA 可访问性未验收。
6. **生命周期**：本进程同线程父窗销毁已证明收到 WM_NCDESTROY 并清理；远程宿主退出/崩溃/重建及跨线程通知时序未实测。集成方应持续观察宿主生命周期、周期 pump，并在主线程 close，不能长期缓存本次 HWND。
7. **虚拟桌面与真实客户端**：用户已把 ChatGPT/Apple Music 放到虚拟桌面 2，在桌面 1 学习。本分支的所有模拟父窗从创建到销毁始终隐藏；没有 ShowWindow/SetForegroundWindow/SetActiveWindow，也没有虚拟桌面接口调用。没有实际挂接 `0x20B30` 或任何 UIA bridge/input HWND。主负责人报告的新歌词/UIA child-root 证据不属于本分支复测结果。

## 验证与复现

验证时间：2026-11-05T19:04:36+08:00。所有测试使用本进程隐藏 STATIC 顶层父窗；不带 WS_VISIBLE，不调用 show；自有子窗虽有 WS_VISIBLE，受隐藏父窗约束，`IsWindowVisible` 实测也均为 false。没有桌面 1 调试 UI。

```powershell
python -B scripts/verify_native_button.py
```

脚本没有真实 HWND 入口和显示模式，仅运行本模块测试；标准库 unittest/ctypes，无安装。脚本先添加项目 src/tests 导入路径并关闭字节码写入，结束输出机器可读 JSON。最新结果：

```json
{"status":"passed","tests_run":18,"failures":0,"errors":0,"skipped":0,"parent_mode":"hidden-only","parent_process":29980,"foreign_parent_tested":false,"live_native_instances":0,"pending_native_instances":0,"show_window_called":false}
```

覆盖内容：

- 两个新 HWND 的 PID/TID、父子链均属于当前进程/主线程。
- PostMessage(BM_CLICK) 入队，在泵消息前不回调；泵后标准 BUTTON 经 WM_COMMAND 回调一次，并核对 `threading.main_thread().native_id`。
- 伪 sender、错误 ID、非 BN_CLICKED 不触发回调。
- 父窗和容器 resize/move，验证相对父窗坐标、按钮填充尺寸及父窗始终隐藏。
- 父窗销毁后两个 HWND 均失效、实例句柄为零，重复 close 安全。
- 回调内关闭自身、回调抛异常、后台线程错误调用和后台构造拒绝。
- DPI 不匹配时确认 CreateWindowEx **没有被调用**；标准按钮创建失败时回滚已创建容器。
- 活跃窗口保留对象，父窗销毁后对象可被 GC；重复 attach/close 20 次实例表/待创建表恢复基线。
- 消息泵保留 WM_QUIT；输入参数验证。

全项目回归使用 `python -B`，在启动代码中仅为当前进程加入 `src` 到 sys.path，再运行等价的 `python -m unittest discover -s tests -v`：**100 tests, 2.755s, OK**。现有 SMTC 测试启动的子进程是带 CREATE_NO_WINDOW 的夹具/函数测试，不访问 Apple UI；现有歌词源测试仅模拟 HTTP，IPC 仅本机 loopback。

按项目规则执行 compileall 的 src/tests 检查，并额外覆盖本次验证脚本。为严格只写四个交付文件，在当前检查进程用 in-memory 编译后端替代 py_compile 的缓存写入，`compileall -q -f src tests scripts/verify_native_button.py` 实际将 **26 个源文件**交给 Python 内建 compile，0 语法错误；没有创建或覆盖 pyc/cache。此处不声称执行了磁盘字节码写入测试。

## 官方 API 依据（只读）

以下为 2026-11-05 实际读取的 Microsoft Learn 主文档，均为 API 条件说明，不是 Apple 兼容性实测证据：

- CreateWindowExW：parent 参数、标准 BUTTON、创建消息和默认子窗层级。
  https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-createwindowexw
- WM_COMMAND：control notification 的 HIWORD/LOWORD 及 lParam sender HWND。
  https://learn.microsoft.com/en-us/windows/win32/menurc/wm-command
- Window Features：父子生命周期、隐藏关系以及父窗绘制/裁剪限制。
  https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features
- DestroyWindow：只能由创建线程销毁窗口及 WM_PARENTNOTIFY 行为。
  https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-destroywindow
- SetParent：DPI awareness 不同的风险与跨进程强制重置表；本模块不调用。
  https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setparent
- High DPI Desktop Application Development：根据父窗 DPI 计算子窗尺寸/位置，响应 DPI 变化。
  https://learn.microsoft.com/en-us/windows/win32/hidpi/high-dpi-desktop-application-development-on-windows
- SendMessage：跨进程消息受 UIPI/完整性等级约束；本测试只对自有子窗发送 BM_CLICK。
  https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendmessage

接口影响：新增独立可选模块，不接入现有观察模式、不改变公共模型或任何既有入口行为。接入时由负责人提供已验证 parent/rect、主线程回调和消息循环，并独立决定版本/布局 gating；本分支到此交付，不进行真实挂接。
