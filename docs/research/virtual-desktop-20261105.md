# 公共虚拟桌面 API 与线程 DPI 上下文 — 2026-11-05

## 交付状态

仅新增 `src/amlyrics/virtual_desktop.py`、`tests/test_virtual_desktop.py`、本记录。未编辑此前 native 按钮模块、其测试或其他已有文件；未派发子 agent。只有标准库 ctypes，无安装、文件下载、歌词服务或配置写入。官方 SDK/API 文档通过只读请求在内存读取。

验证时间：2026-11-05T19:19:00+08:00，Windows / 64-bit Python 3.13.1。

- **observed / high**：离线 ctypes 六槽 COM vtable 测试验证 GUID ABI、Get=4、Move=5、Release=2、COM apartment 配对、PID 拒绝、失败诊断及 DPI finally 恢复。槽位 3 有故意失败的测试桩，确认实现不调用它。
- **observed / high**：真实隐藏自有窗口的线程 DPI 上下文进入与恢复成功；前后用 AreDpiAwarenessContextsEqual 核对。
- **observed / high**：真实隐藏窗口 GetWindowDesktopId 返回 **HRESULT 0x8002802B / TYPE_E_ELEMENTNOTFOUND（找不到元素）**。独立创建的 hidden toolwindow、hidden appwindow、hidden normal 三种窗口均出现相同结果；每个窗口保持不可见并在 finally 销毁。
- **inferred / medium**：可能由于从未显示的窗口没有对应 Shell application view；Shell 内部原因未证实。
- **未验证成功**：以隐藏自有窗口自身为 reference 的真实移动，在查询桌面 ID 阶段停止，未调用 MoveWindowToDesktop。不能声称真实隐藏窗口移动已通过。没有读取 Apple 桌面 ID、移动 Apple/用户窗口、显示自测窗口或切换/激活桌面。
- 专项 **26 tests：24 passed，2 skipped，0 failures/errors**。两项 skip 是真实桌面查询及依赖该查询的同桌面移动；仅明确的 0x8002802B 或 GUID_NULL 可被此测试标记为限制，其他 HRESULT 会使测试失败。
- 全项目 **182 tests，2.762s，OK (skipped=2)**，仅同样两项跳过。主负责人并行更新源码，数量对应此验证时刻。
- compileall 使用内存编译后端，强制编译 src/tests 共 **35 个文件**，无语法错误，没有创建或覆盖 pyc/cache。

## 接口与集成

```python
from amlyrics.virtual_desktop import (
    VirtualDesktopError, get_window_desktop_id,
    move_own_window_to_same_desktop, same_dpi_context,
)

# 接口示例：本分支未对 Apple 执行。
desktop_id = get_window_desktop_id(reference_main_hwnd)
target_id = move_own_window_to_same_desktop(own_hidden_hwnd, reference_main_hwnd)

with same_dpi_context(parent_hwnd):
    native_button.attach(parent_hwnd, rect)
```

`get_window_desktop_id(hwnd: int) -> str` 只读查询顶层窗口，返回小写、无花括号的标准 GUID 字符串。拒绝无效/child HWND、GUID_NULL 和 HRESULT 失败，不枚举窗口。负责人应传 `apple_window.py` 的 **main_hwnd**，不是其 UIA bridge/input child HWND。

`move_own_window_to_same_desktop(own_hwnd: int, reference_hwnd: int) -> str` 先验证目标属于当前 PID，再查询 reference 桌面；在紧邻 Move 前再次核对 PID；仅对 own_hwnd 调用 Move；随后重新核对目标 PID 并读回桌面 GUID，必须与目标一致才返回成功。对 reference 的唯一 COM 方法是 GetWindowDesktopId；另有只读 IsWindow/GetAncestor 参数校验。不会对 reference 调用 Move 或当前桌面判断。

移动函数不 show/hide、不激活、不改变 owner/style。调用方应保持歌词窗隐藏，只有函数返回成功后才考虑显示。任何诊断失败均继续隐藏，不回退到当前桌面直接 show，也不切桌面重试。若 Move 成功、后续读回失败，窗口可能已经移动，异常不代表回滚；实现不会自动移回或再次 Move。

`VirtualDesktopError` 提供 `.operation`、`.hresult`（unsigned 32-bit，非 COM 错误为 None）、`.winerror`；错误文本包含操作和十六进制 HRESULT。窗口失效、PID 不符、GUID_NULL 等也有明确原因。

`same_dpi_context(hwnd) -> contextmanager[None]` 接受顶层或 child HWND，只查询窗口 DPI，并临时调用 SetThreadDpiAwarenessContext；保留 API 返回的旧 context，finally 恢复。支持嵌套和正文抛异常，进入/退出必须同一 OS 线程；恢复失败会抛诊断，正文异常保留在异常链。无进程/系统 DPI setter，不改全局默认。

## 官方 CLSID/IID 与 ABI 核实

只使用微软官方源，2026-11-05 实际读取 Microsoft win32metadata 的 SDK 头文件，不依赖第三方博客或私有桌面接口：

| 项目 | 核实值 | 主源声明 |
|---|---|---|
| CLSID_VirtualDesktopManager | `AA509086-5CA9-4C25-8F95-589D3C07B48A` | ShObjIdl.h，VirtualDesktopManager 的 DECLSPEC_UUID |
| IID_IVirtualDesktopManager | `A5CD92FF-29BE-454C-8D04-D82879FB3F1B` | ShObjIdl_core.h，IVirtualDesktopManager 的 MIDL_INTERFACE |

官方文件：

- https://raw.githubusercontent.com/microsoft/win32metadata/main/generation/WinSDK/RecompiledIdlHeaders/um/ShObjIdl.h
- https://raw.githubusercontent.com/microsoft/win32metadata/main/generation/WinSDK/RecompiledIdlHeaders/um/ShObjIdl_core.h

SDK 的 IVirtualDesktopManagerVtbl 从零起依次为：

| slot | 方法 | 本实现 |
|---|---|---|
| 0 | IUnknown.QueryInterface | 不额外调用 |
| 1 | IUnknown.AddRef | 不额外调用 |
| 2 | IUnknown.Release | finally 释放创建的接口引用 |
| 3 | IsWindowOnCurrentVirtualDesktop(HWND, BOOL*) | 不调用 |
| 4 | GetWindowDesktopId(HWND, GUID*) | 查询 reference 和移动后读回 |
| 5 | MoveWindowToDesktop(HWND, REFGUID) | 仅当前进程 PID 检查通过的 own HWND |

WINFUNCTYPE 遵循 Windows COM ABI；HRESULT 为 c_int32，Release 返回 c_uint32，GUID 为 uint32 + uint16 + uint16 + 8 bytes，共 16 字节，以 UUID.bytes_le 构造。未使用 IVirtualDesktopManagerInternal、未公开 CLSID 或桌面切换 API。

## COM 与异常生命周期

每次公开桌面操作独立 CoInitializeEx(NULL, COINIT_APARTMENTTHREADED)，不跨调用/线程缓存 COM 引用：

1. S_OK/S_FALSE 均是成功初始化，finally 各配对一次 CoUninitialize。
2. RPC_E_CHANGED_MODE（0x80010106）沿用已存在的 apartment，不切换其模式，不代替调用方 CoUninitialize。CoCreateInstance 仍可能失败，按原 HRESULT 报告。
3. 其他 init 失败立即诊断，不 CoCreate、不 CoUninitialize。
4. CoCreateInstance 使用 CLSCTX_INPROC_SERVER 创建 IVirtualDesktopManager。Get/Move/读回的所有退出路径都会在 finally Release 非空接口，然后仅对本次成功 init 配对 CoUninitialize。
5. 即使 Release 路径出现异常，嵌套 finally 仍负责反初始化。没有额外 QueryInterface/AddRef 产生的引用。

首轮测试曾出现 7 项 ERROR：ctypes.FormatError 接收 signed C long，不能直接传 unsigned HRESULT 大整数。现已修复为格式化时转换 signed 32-bit，字段和十六进制仍保留 unsigned HRESULT；格式化本身失败也不会遮蔽原 HRESULT。新增对应回归，以上结果为修复后结果。

## 静默复现与测试

项目根目录下执行下列单行命令；仅在当前进程加入 src 导入路径，不写环境配置：

```powershell
python -B -X utf8 -c "from pathlib import Path; import sys, unittest; sys.path.insert(0,str(Path('src').resolve())); r=unittest.TextTestRunner(verbosity=2).run(unittest.defaultTestLoader.discover('tests',pattern='test_virtual_desktop.py')); raise SystemExit(0 if r.wasSuccessful() else 1)"
```

真实测试窗口均由本进程新建，style 无 WS_VISIBLE，创建后/销毁前均断言 IsWindowVisible=false，并验证 PID。无 ShowWindow、SetForegroundWindow、SetActiveWindow 或桌面切换。真实移动测试将自身 HWND 同时作为 own/reference，绝不选取用户窗口。独立三种隐藏样式探测也是本任务新建窗口，查询失败即诊断并销毁，没有显示来换取 Shell 识别。

离线测试包含：官方 GUID 布局和槽位、外 PID reference 只查询、拒绝移动外进程窗口、Move 前拥有权变化拒绝、HRESULT 不重试、读回失败、S_FALSE/CHANGED_MODE、Create/Get/Move 失败释放、GUID_NULL、空 interface、无效/child HWND、DPI 正常/正文异常/嵌套/进入失败/恢复失败。

全项目回归以 -B 禁止字节码写入，当前进程加入 src 后 discover tests；详细 runner 输出在内存汇总，无额外日志文件。按项目规则运行 compileall 的 src/tests 检查，py_compile 后端在本次检查进程替换为内建 compile，执行等价的 compileall -q -f src tests，既强制验证源码又不改现有缓存。

## 已知限制

- 真实隐藏窗口移动尚未成功验证。工具窗口或从未显示的窗口能否被 Shell 接受，应由负责人在授权范围内继续验证；不能从离线测试推断跨桌面显示链路已可用。
- 本模块同时绑定桌面 COM/DPI API，要求 Windows 10 1607+ 对应 API 和 Shell 服务可用；不提升权限或绕过会话边界。
- IsWindow/PID 检查与 COM Move 不原子，HWND 可能复用。应在自有窗口 UI 线程调用并串行管理窗口生命周期；即时 PID 校验不提供永久身份保证。
- reference 可能在读取后被用户移动；本次操作只使用刚读取的 GUID。Apple 身份、版本、布局和桌面跟踪仍由主负责人负责。
- COM/Shell 操作同步，没有硬超时。已有 MTA 按原模式使用；不支持时返回 HRESULT，不强制重建 apartment。
- DPI scoped 恢复不改变已经创建的 HWND 的创建 DPI awareness，也不自动换算 UIA 物理像素/客户区坐标。
- 不修改 native_button.py 的主线程及 DPI matching 规则，主负责人可在主线程 with 内 attach。没有自动调用、产品入口或自动 show 的行为变化。

其他实际读取的官方文档：

- https://learn.microsoft.com/en-us/windows/win32/api/combaseapi/nf-combaseapi-coinitializeex
- https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ivirtualdesktopmanager-movewindowtodesktop
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setthreaddpiawarenesscontext

用户在桌面 1 学习期间，本分支保持静默；所有真实窗口均为自己创建、始终隐藏、最终销毁。
