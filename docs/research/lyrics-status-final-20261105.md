# 歌词状态最终审查（2026-11-05）

## 审查范围

本报告只审查现有实现，不修改既有代码：

- `src/amlyrics/apple_preferred.py`
- `src/amlyrics/lyrics_sources.py`
- `src/amlyrics/live_session.py`
- Apple 无时间观测的边界实现：`src/amlyrics/apple_lyrics.py`

本次新增文件只有：

- `tests/test_lyrics_status.py`
- `docs/research/lyrics-status-final-20261105.md`

未使用网络、未下载或安装依赖、未操作 Apple Music、未读取账号凭据或私有缓存。没有调度子 agent；因此没有无法保证模型的问题。

## 可以保证的行为

### 1. 默认离线

`LyricsResolver` 构造时 `enable_external` 默认是 `False`。即使调用者把单次参数设为 `allow_network=True`，外部阶段仍会直接返回 `None`，诊断码为 `disabled`，不会进入 LRCLIB 请求路径。

`LyricsResolver.lookup(state)` 的默认调用方式同样离线。即使实例构造为 `enable_external=True`，单次调用仍必须显式传入 `allow_network=True` 才能进入外部查询阶段。

构造 `LyricsResolver` 本身不执行 I/O。Apple provider 和内存本地 LRC 映射会先被检查；它们属于本机/调用者已经提供的数据，不会因为默认离线而被误判为网络请求。

`ApplePreferredLookup` 的 `allow_network` 默认值为 `False`，并按该值调用 `resolver.lookup(..., allow_network=...)`。因此默认整合链路不会启用外部同步查询。

### 2. 用户明确在线授权后的双开关

外部网络查询必须同时满足：

1. 构造 resolver 时 `enable_external=True`；
2. 本次 lookup 传入严格的 `allow_network=True`。

缺少任一开关时，resolver 返回 `None` 并设置 `last_diagnostic = "disabled"`。只有两个开关同时满足，才会构造并发送 LRCLIB `/api/get` 的请求。

现有整合交互测试还验证了用户同意在线同步时同时打开 `network_lookup.allow_network` 与 `resolver.enable_external`，然后刷新歌词；用户拒绝时两个开关都保持关闭，也不会刷新查询。

本次新增测试使用内存响应 fixture 验证了三种组合：构造期开关关闭、单次授权关闭、两个开关同时打开。验证全程把真实 `urlopen` 替换为离线 mock，因此“请求成功”只代表请求构造和解析路径可被离线 fixture 覆盖，不代表实际服务可用。

### 3. diagnostic 到中文提示

`LiveSession._missing_lyrics_message()` 已将当前 resolver 诊断码映射为用户可读中文，包括：

| 诊断码 | 中文提示 |
| --- | --- |
| `disabled` | 请先启用在线同步 |
| `timeout` | 歌词查询超时，稍后重试 |
| `network_error` | 连接歌词源失败，稍后重试 |
| `rate_limited` | 歌词源暂时限制请求，稍后重试 |
| `not_found` | 未找到这首歌的同步歌词 |
| `no_synced_match` | 当前歌曲版本暂无匹配的同步歌词 |
| `missing_metadata` | 等待完整歌曲信息 |
| `invalid_response` | 歌词源返回内容无效，稍后重试 |
| `http_error` | 歌词源暂时不可用，稍后重试 |

未知诊断码不会直接展示给用户，而是回退为 `暂无同步歌词`。查询进行中会显示 `正在查找同步歌词`；没有 Apple Music 会话时会显示 `等待 Apple Music 会话`。查询异常只展示 `歌词查询失败：异常类型名`，不把异常对象文本直接放到用户界面。

`ApplePreferredLookup` 仍保留内部诊断码，例如 `disabled`、`lrclib` 或 `apple_music+external_timing`；中文化发生在 LiveSession 的用户界面状态层。日志记录诊断码，不记录整段歌词或凭据。

### 4. Apple plain lines 不冒充实时同步

Apple 观测合同中的 `AppleLyricLine` 只有顺序索引和文字，没有时间戳或当前句证据。`get_apple_timed_result()` 当前始终返回 `None`，不会把以下信息推断成时间轴：

- `observed_at` 抓取时间；
- 行列表顺序或 index；
- UIA 的 Invoke/ScrollItem pattern；
- 屏幕位置、居中状态或几何信息；
- SMTC 播放进度或播放/暂停状态。

因此，Apple 只提供 plain lines 时，`ApplePreferredLookup` 不会把它们返回为 `source="apple_music"` 的实时歌词。没有另一个带逐行时间的有效来源时，结果保持 `None`。

Apple 文本只有在已有外部 `LyricsResult` 且该结果满足严格逐行时间合同、同一完整 track key、整段非空文本序列逐项 NFC 相等时，才可通过 `combine_apple_text_with_timing()` 组合。组合结果保留外部的 `start_ms`/`end_ms`，来源为 `apple_music+external_timing`，不会声称时间来自 Apple。此路径仍不能证明 Apple 原始同步精度，只能证明本次 Apple 文本与外部时间歌词逐项匹配。

LRCLIB 响应的 `plainLyrics` 从不作为时间歌词回退。解析器只接受有效 `syncedLyrics`；没有同步字段、同步字段为空或同步字段只是无时间纯文本时，返回 `None`。

## 离线测试与验证

新增 `tests/test_lyrics_status.py`，覆盖：

- resolver 默认离线；
- 构造期开关与单次调用开关的双重授权；
- ApplePreferred 默认/显式 `allow_network` 传递；
- LiveSession 已知诊断码的中文提示和未知码安全回退；
- Apple plain lines 不生成实时 `LyricsResult`；
- 真实网络入口被 mock 阻断，成功外部分支使用内存 JSON 响应。

验证结果：

- `python -m compileall -q src tests`：通过。
- 裸执行 `python -m unittest discover -s tests -v`：当前环境未自动把 `src` 加入模块搜索路径，18 个测试模块在导入阶段报 `ModuleNotFoundError: No module named 'amlyrics'`；未修改项目配置来绕过这一环境差异。
- `PYTHONPATH=src python -m unittest tests.test_lyrics_status -v`：5 项通过。
- `PYTHONPATH=src python -m unittest tests.test_lyrics_status tests.test_lyrics_sources tests.test_live_session tests.test_integrated_pipeline tests.test_apple_lyrics -v`：96 项通过。
- `PYTHONPATH=src python -m unittest discover -s tests -v`：249 项中 247 项通过、2 项跳过、2 项失败。失败均在既有窗口生命周期测试：`test_missing_apple_window_past_ten_seconds_keeps_host_alive_and_reattaches` 的 `close_calls` 断言，以及 `test_explicit_integrated_close_still_closes_session_native_and_apple_bridge` 的 `_closed` 断言；不涉及本次新增文件或歌词状态审查路径。

以上测试均为离线运行。新增测试将真实 `urlopen` 替换为阻断器，双开关开启场景使用内存 JSON 响应 fixture；没有实际外部请求。本报告不把离线 mock 的通过当作真实 LRCLIB 可用性或 Apple 运行时可观测性的证明。

## 当前不能保证的事项

- 没有发起真实 LRCLIB 请求，不能保证当前域名、接口字段、服务状态、覆盖率、限流策略或实际歌词质量。
- 没有操作 Apple Music 或重新采集 UIA/SMTC，不能保证所有客户端版本、窗口状态、歌曲版本和会话重连场景都能得到稳定的 plain lines。
- Apple plain lines 没有逐句时间和当前句语义，不能保证仅凭 Apple 观测实现实时逐句同步。
- 即使 Apple 文本和外部逐行文本严格匹配，也不能保证外部歌词对应同一录音、完整覆盖整首或其时间点与 Apple 内部时间点一致；实现只能保留并使用外部时间来源。
- `ApplePreferredLookup` 没有对构造参数 `allow_network` 做严格布尔类型校验。正常调用路径传入布尔值时，双开关语义可验证；若外部代码传入其他真值对象，不能把参数类型约束视为已保证。
- `LiveSession` 的中文提示覆盖当前已知诊断码；未知码会安全回退，但未来新增诊断码若未同步加入映射，只能显示通用提示。
- 本次没有修改既有代码，因此没有修复或扩大任何运行时能力；报告只记录当前实现的行为边界。

## 结论

当前实现可以保证：默认离线；只有构造期开关和本次调用开关同时授权时才进入外部查询；已知诊断码在 LiveSession 中转为中文提示；Apple 无时间 plain lines 不会单独冒充实时同步。

当前实现不能保证：真实网络服务可用性、Apple 运行时抓取稳定性、Apple 原生时间轴、以及外部时间与 Apple 内部同步的一致性。

