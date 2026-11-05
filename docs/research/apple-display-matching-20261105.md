# Apple 合并 Artist 的离线候选匹配（2026-11-05）

## 状态与范围

本轮只实现纯内存解析器，不调用外网、不操作 Apple、不启动窗口、不安装或下载。
新增范围为 src/amlyrics/apple_external.py、tests/test_apple_external.py 和本记录。
未修改既有歌词来源、公共数据模型、IPC 或入口。

依据是 docs/research/smtc-20261105.md 的既有只读观察：可信来源
AppleInc.AppleMusicWin_nzyj5cx40ttqa!App 的 Artist 原样为
“林俊杰 — 裹着心的光 - Single”或“陶喆 — 陶喆同名专辑”，AlbumTitle 为空。
这些是既有观察；本轮没有重新读取 Apple 会话。
测试中的 LRCLIB 候选和歌词均为合成 fixture，不是已获取的真实 API 响应。

## 可立即集成的接口

~~~python
from amlyrics.apple_external import parse_apple_display_candidates

result = parse_apple_display_candidates(
    candidates,                 # 已解码的 JSON list；不是单个对象/JSON 字符串
    original_playback_state,    # 保持 SMTC 原始 artist、album、source_app
    duration_tolerance_ms=2000, # 与 parse_lrclib_response 的默认值一致
)
~~~

返回 LyricsResult | None。成功时 source="lrclib"，track_key 严格恢复为
original_playback_state.track_key。其他结果字段由共享 parser 生成。
候选至少需提供 trackName、artistName、albumName、duration（秒）及 syncedLyrics。
id 不参与信任判定，也不用于去重。plainLyrics 不作为降级来源。

None 表示不适用、无完整匹配或存在歧义；本接口不提供分类诊断。
非法 duration_tolerance_ms（负数、bool、非 int）抛 ValueError，与共享 parser 一致。
调用者可直接在已获得候选后调用该接口；它本身不需要网络开关，因为没有 I/O。

## 匹配规则

1. source_app 仅接受实测完整 AUMID，允许大小写差异；不接受子串或周围空白。
2. 原始 state.album 必须严格等于空字符串；空格或已有 Album 均不适用。
3. 候选 artistName 和 albumName 必须是非空白字符串。只重建
   f'{artistName} — {albumName}'，再与整个原始 state.artist 分别作
   NFKC → casefold → strip，要求完全相等。不先 trim 候选的每个字段。
4. 不从原始 Artist 拆分字段；候选字段自己包含“ — ”也只作完整重建比较。
   不改连字符、不合并内部空白、不去 Live/Single/Deluxe 等版本标签、不作模糊匹配。
5. 用 dataclasses.replace 临时将 state.artist/album 设为候选明确提供的字段，
   原样传给 parse_lrclib_response；其内部执行规范化后的标题/艺术家/专辑精确比较、
   正时长及闭区间容差、同步歌词与时间轴合法性验证。默认容差为 ±2000 ms。
6. 恰好一个候选通过全部校验才返回，并恢复原始 track_key。多个通过者一律返回 None，
   包括完全重复的对象或相同 id；不按响应顺序选第一个。不合格候选不计入歧义数。
7. 不修改原始 state 或 payload；不把候选拆出的字段写回 SMTC 或公共契约。

模块复用 lyrics_sources 的 _normalize、_check_tolerance 与公共解析函数，并复用
smtc_bridge.APPLE_MUSIC_SOURCE 常量。导入和调用均不启动 bridge，也不读播放状态。
如果负责人今后调整这些内部帮助函数，需要同时检查本模块的引用。

## 运输层留待核实

本地 docs/research/lyrics-source-policy.md 只记载 /api/get 的接入假设与官方文档
入口，未提供 /api/search?track_name=... 的已核实契约。本轮禁止联网，因此未新增
AppleDisplayLyricsResolver、搜索 URL 构造或任何实际请求，也不承诺该接口可用。

负责人核实官方搜索参数、响应结构与限制后，可另行实现 LyricsResolver 子类。
合并 Artist 场景可把已获得的候选列表交给本接口；非 Apple 或非合并场景应回退父实现。
任何实际外部调用须继续受 enable_external=True 与每次 allow_network=True 双开关控制。
现有父类缓存键不含 source_app，未来若同一 resolver 混用不同来源，必须隔离来源或调整
缓存策略，避免 Apple 合并展示匹配结果跨来源命中；本轮纯解析器没有缓存或此类旁路。

## 验证

新增测试在内存中封锁网络入口、子进程启动和 SMTC 启动/检查；覆盖两个已观察展示形式、
Unicode/大小写、严格 AUMID、原始空 Album、分隔符和版本保留、缺失字段、非法列表、
容差边界、共享 parser 委托、原始 track_key 恢复、候选不变性及重复/多分割歧义。

执行结果（Python 3.13.1，项目根目录，当前测试进程 PYTHONPATH=src）：

- python -m compileall -q src tests：退出码 0。
- python -B -m unittest discover -s tests -p test_apple_external.py -v：21 项全部通过。
- python -B -m unittest discover -s tests -p test_lyrics_sources.py -v：31 项全部通过。
- 全目录 discover 的受限内存运行：发现 151 项，执行 121 项全部通过，跳过 30 项。

全目录检查使用 unittest.defaultTestLoader.discover("tests") + TextTestRunner，
仅在测试进程内标记以下类跳过，未改写其源文件：HiddenNativeButtonTests、
PowerShellSelectionTests、ProcessTests、IpcTransportTests。它们需要原生窗口、
PowerShell/Python 子进程或本地套接字，不符合本轮全部 fixture/mock 的限制。
AppleMusicInspector.inspect 用纯内存 AppleMusicObservation fixture 替代；
进程级 audit hook 拒绝 subprocess.Popen、socket.connect、socket.bind、socket.getaddrinfo。
因此不是未受限的完整原生集成回归，未执行标准命令下的上述运行时行为。

验证期间观察到并行任务修改 apple_lyrics.py/test_apple_lyrics.py；本任务未写入这些文件。
一次跨模块回归曾因 LyricsResolver 被当作函数调用而报错；并行任务修正为 lookup 后，
按相同隔离条件复验得到上述 121 项通过结果。

真实 API 的响应、搜索排序、覆盖率和运行时联网均未验证。


## 最新注解：负责人核实官方搜索文档后新增 resolver（2026-11-05）

以上“运输层留待核实”“未新增 AppleDisplayLyricsResolver”等表述保留为上一轮的历史记录。
截至本注解，负责人已报告只读实际获取官方 https://lrclib.net/docs 的 alternate href
/docs.md，HTTP 200，文档长度 22199 字符，并核对 “Search for lyrics records” 小节：

- GET /api/search 返回 JSON 数组。
- q 或 track_name 至少提供一个；track_name 为标题关键词，artist_name/album_name 可选。
- 文档当前描述最多 20 条、无分页；此限制可能变化，不作为客户端截断常量。

这是负责人提供的官方文档核实结果，本子任务没有独立联网或发起任何歌词 API 请求。
“文档已核实”不等于运行时接口、覆盖率、返回排序或匹配质量已经验证。

### 新增接口与写入范围

新增 src/amlyrics/lrclib_search.py、tests/test_lrclib_search.py，并只在本记录末尾追加说明。
未修改 src/amlyrics/apple_external.py、lyrics_sources.py 或既有解析测试。

~~~python
from amlyrics.lrclib_search import AppleDisplayLyricsResolver

resolver = AppleDisplayLyricsResolver()  # 与父类相同构造参数，默认离线
result = resolver.lookup(state)          # Apple/local 可用；不会调用外部服务

# 主脚本日后按用户授权启用外部来源时，必须同时满足：
# resolver = AppleDisplayLyricsResolver(enable_external=True)
# result = resolver.lookup(state, allow_network=True)
~~~

保留父类 Apple callback → local mapping → external 的优先级、公共契约、TTL、缓存条数限制、
每次调用的双开关检查和 last_diagnostic。仅以下条件全部满足时外部阶段改用搜索：
完整可信 AUMID（只忽略大小写）、原始 album == ""、原始 artist 含字面量 " — "。
其他状态调用 super()._fetch_external(state)，保留原有 /api/get 行为。
条件判定只用于选择获取候选的路径，不拆解 Artist，也不构成成功匹配证据。

搜索构造 GET https://lrclib.net/api/search?track_name=<原始标题的 URL 编码>，
仅发送 track_name；不发送 q、artist_name、album_name 或 duration，不增加分页或重试。
搜索结果仍交给未修改的 parse_apple_display_candidates，按完整展示串重建、精确标题、
已知正时长与容差、同步歌词合法性进行唯一性校验，成功后保留原始 track_key。
唯一性只针对本次响应候选；服务端限制可能漏掉其他记录，因此不宣称覆盖整个曲库。

### 运输与诊断

- 使用 urllib，传递继承的 timeout_seconds；响应只 read(1048576 + 1)，
  超过 1 MiB 即 invalid_response。刚好 1 MiB 的合法 JSON 可以接受。
- 非法 UTF-8/JSON、过深 JSON 或非数组顶层为 invalid_response。
- 空列表、无严格匹配、缺少同步歌词或多个完整匹配统一为 no_synced_match；
  不按返回顺序选择，不去重相同 id，不把 plainLyrics 变成同步歌词。
- 成功结果 source 和 last_diagnostic 均为 lrclib。
- HTTP 404 → not_found；429 → rate_limited；其他 HTTP → http_error。
- 直接超时或 URLError 中的超时 → timeout；其他 URL/OSError → network_error。
- 与原 provider 一致关闭响应及 HTTPError 响应体，不把服务端/异常中的敏感文本写入诊断。
- 搜索请求头为 Accept: application/json、
  User-Agent: apple-music-desktop-lyrics/0.1.0，与当前 pyproject.toml 名称和版本一致。
  当前项目无主页，未虚构 URL；名称版本用于当前开发原型，正式配置仍需补充真实项目主页。
  非搜索分支按要求回退父实现，父实现既有请求头未改动。

### 缓存来源隔离

父类 track_identity 不含 source_app；只覆盖 _fetch_external 会导致来源变化时直接命中
旧外部缓存，绕开搜索/回退选择。新子类因此额外薄覆写 lookup：规范化来源大小写，
来源变化时清空该实例的外部正负缓存，再执行 super().lookup。这样 Apple → 其他来源、
其他来源 → Apple 都重新走正确分支；同 AUMID 大小写变化保留缓存。

未改变父类对 Apple/local 的优先级和 track_key 恢复。仍沿用父类的单调用方缓存使用方式，
不新增并发安全承诺。来源切换会丢失旧来源的外部缓存，属于可重新查询的内存状态。

### 本轮验证

- python -m compileall -q src tests：退出码 0。
- python -B -m unittest discover -s tests -p test_lrclib_search.py -v：21 项全部通过。
- 全目录 discover 的受限内存运行：发现 203 项，执行 170 项全部通过，跳过 33 项。

新增测试把 HTTP 候选全部放在内存中，并封锁真实网络、子进程启动、SMTC 检查/启动。
覆盖默认离线与每次双开关、查询参数/请求头/超时、父实现回退、Apple/local 优先级、
严格匹配和歧义、容差、响应大小边界、UTF-8/JSON/结构错误、HTTP/网络错误、响应关闭、
正负缓存过期、原始 track_key 以及双向跨来源缓存隔离。另用 21 条候选验证客户端不会
把文档当前“最多 20 条”固化为截断规则。

全目录检查仍用 unittest.defaultTestLoader.discover + TextTestRunner，测试进程内跳过
HiddenNativeButtonTests、HiddenOwnWindowTests、PowerShellSelectionTests、ProcessTests、
IpcTransportTests，以免启动原生窗口、子进程或套接字。AppleMusicInspector.inspect
替换为内存观察 fixture；audit hook 拒绝 subprocess.Popen、socket.connect、socket.bind、
socket.getaddrinfo。因此本结果不包含真实 UI、PowerShell、IPC 或在线歌词集成验证。
