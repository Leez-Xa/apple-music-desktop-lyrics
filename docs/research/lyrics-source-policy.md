# 歌词来源与离线验证策略

日期：2026-11-05。此阶段仅新增 `lyrics_sources.py`、对应测试和本文档；公共数据模型、现有 LRC 解析器、同步器、service 和主程序均不在本次修改范围。

## 接口与接入

```python
from amlyrics.lyrics_sources import LyricsResolver, parse_lrclib_response, track_identity

resolver = LyricsResolver(
    apple_provider=apple_callback,  # 可省略；离线 callback，返回 LyricsResult | None
    local_mapping=local_lrc_by_identity,  # 可省略；内存中的 LRC 字符串映射
    enable_external=args.allow_external_lyrics,
)
lookup = lambda state: resolver.lookup(
    state, allow_network=args.allow_external_lyrics,
)
```

主程序负责单独提供 `--allow-external-lyrics`（默认 false），再把 `lookup` 交给服务。上述为接入示例，此次不修改主程序或实现 CLI flag。负责人提供的 LiveSession 接入约定是仅在换歌时查询一次，并在切歌后丢弃迟到的结果；调度和结果过期处理由调用方实现，resolver 本身为同步接口。

- `LyricsResolver.lookup(state, *, allow_network=False) -> LyricsResult | None`：默认离线，可直接作为已有 `LyricsService` 的查询回调。
- `resolver.last_diagnostic: str | None`：最近一次调用的固定诊断码，构造后为 `None`；不会保存异常文本、账号或请求 URL。
- `track_identity(state) -> tuple[str, str, str, int]`：本地映射与外部缓存共用的歌曲身份键；顺序为规范化标题、艺术家、专辑、**完整毫秒时长**。
- `parse_lrclib_response(payload, state, *, duration_tolerance_ms=2000)`：独立的纯内存解析入口，接收已解码的单个响应对象，不访问网络。

本地映射示例：`{track_identity(state): "[00:01.00]第一句\n[00:03.50]第二句"}`。映射值为 LRC 文本，不是文件路径；resolver 不遍历磁盘，也不自行读取歌词文件。

## 来源级联与授权边界

每次查询按以下顺序执行，并在得到有效的有时间戳歌词后立即返回：

1. Apple Music provider callback。
2. 可选本地 LRC 映射。
3. 可选外部 LRCLIB 来源。

构造实例不发请求，`enable_external=True` 也不会触发请求。外部 HTTP 路径要求构造参数 `enable_external=True` **且本次调用显式传入 `allow_network=True`**；缺少任一条件，且前两级没有结果时返回 `None`，诊断为 `disabled`。`allow_network=1`、字符串等非布尔真值不算授权。关闭网络的查询也不复用外部正缓存。

Apple callback 类型为 `Callable[[PlaybackState], LyricsResult | None]`，只接收播放状态，必须返回调用方已合法取得的有时间戳歌词。模块验证曲目键和时间轴，源标记为 `apple_music`；无结果、异常、曲目键不符或无效时间轴时继续回退。该接口不实现、不承诺能够提取 Apple Music 歌词，不读取账号、凭据、Apple 私有缓存或 DRM 内容。任意 callback 是调用方代码，其离线行为是接入约定，不是 Python 沙箱保证；不得用它绕过网络开关。`LyricsResult` 不含歌曲时长字段，callback 还需自行保证返回的是当前歌曲版本。

本地与外部文本复用现有 `parse_lrc`，只保留真实解析出的时间戳。无时间戳、仅空歌词或时间轴超出已知播放时长时不返回结果；不通过字数、行数或平均时长为 `plainLyrics` 伪造时间轴。

## 外部匹配与时长

外部响应只读取 `syncedLyrics`，即使 `plainLyrics` 存在也不作降级补全。响应必须是单个对象，并满足：

- `trackName` 与 `artistName` 分别与播放状态精确匹配。
- 播放状态有专辑时，`albumName` 也必须匹配；专辑未知时不要求响应专辑一致。
- 字符串匹配仅做 Unicode **NFKC → casefold → 去掉首尾空白**。不移除标点、不折叠中间空白、不删除括号内容，不剥离 live、remaster、deluxe、feat. 等版本或艺术家信息，也不进行模糊匹配。
- 播放时长必须是正整数毫秒；响应 `duration` 必须是有限正数，单位秒，不接受布尔值或数字字符串。
- 默认允许的绝对时长差为 **≤ 2000 ms，包含边界**，可通过 `duration_tolerance_ms` 配置为非负整数，包括 0。

不同标题版本、艺术家、专辑或精确时长使用不同本地/缓存键。位置、播放/暂停状态和观察时间不属于身份键。同名同艺人且时长接近的录音仍可能重名；此匹配策略不证明录音身份，未知专辑时也无法核实专辑版本。没有新增歌曲 ID 或修改公共 contracts。

返回的 `LyricsResult.track_key` 始终沿用本次 `state.track_key`，保持 service 接口兼容。规范化缓存键不改变已有 contracts 的 `track_key` 行为。

## 请求、缓存与诊断

仅在双开关许可后使用 Python 标准库 `urllib.request` 构造 HTTPS GET，目标为 LRCLIB `/api/get`，参数为 `track_name`、`artist_name`、`album_name`、`duration`（秒）。不做搜索候选遍历，不创建定时任务或后台重试，不安装依赖。默认请求超时 5 秒，响应读取上限 1 MiB，超限按无效响应处理。urllib 的超时约束用于底层阻塞 I/O，不是整个同步查询的严格总耗时预算。

缓存仅在实例内存中保存，不写磁盘。只缓存外部查询，每次仍先检查 Apple callback 和本地映射，因此外部正/负缓存不会掩盖后来可用的优先来源。默认正缓存 300 秒、负缓存 30 秒，使用单调时钟；TTL 为 0 时不保存对应缓存。缓存最多 128 项，满时按插入顺序淘汰。`positive_cache_ttl_seconds`、`negative_cache_ttl_seconds`、`max_cache_entries` 均可配置。缓存命中保留原诊断码，不附加账号或曲目内容。

| 情况 | 返回 | `last_diagnostic` |
| --- | --- | --- |
| Apple callback 有效 | `LyricsResult` | `apple_music` |
| 本地 LRC 有效 | `LyricsResult` | `local` |
| 外部匹配且有同步歌词 | `LyricsResult` | `lrclib` |
| 外部开关未获双重授权 | `None` | `disabled` |
| 外部所需标题、艺术家或时长缺失 | `None` | `missing_metadata` |
| 连接/读取超时，包括 URLError 包装的超时 | `None` | `timeout` |
| HTTP 404 | `None` | `not_found` |
| HTTP 429 | `None` | `rate_limited` |
| 其他 HTTP 错误 | `None` | `http_error` |
| 连接或其他网络 I/O 错误 | `None` | `network_error` |
| 无同步歌词、元数据不符或响应不是预期对象 | `None` | `no_synced_match` |
| 无效 UTF-8/JSON、过深 JSON 或响应超限 | `None` | `invalid_response` |

超时、404、429、无同步歌词等外部失败均参与负缓存；不会在同一次调用中自动重试。当前 429 使用配置的负缓存 TTL，未实现 `Retry-After` 调度。Apple callback 异常只触发继续级联，不输出异常详情；最终诊断描述最后实际采用的来源或回退原因。

## 文档依据与验证限制

负责人已报告只读查阅官方 `https://lrclib.net/docs`，该页提供 `docs.md`、`docs.txt` 的 plain text/Markdown 入口。本子任务遵守禁止联网约束，未再打开网页、下载文档文件或请求实际歌词；未独立核实这些入口内容。代码中的 `/api/get`、查询字段和响应字段按上述接入模型实现并使用自建 fixture 验证，**接口未经实际请求验证，不保证当前服务可用性、字段完整性、返回匹配质量或歌词覆盖率**。

所有来源测试只使用内存 LRC、JSON fixture、mock callback、mock urllib 返回值和可控时钟；同时阻断真实 DNS 和 socket 连接。mock 代码路径中的 `allow_network=True` 只用于验证授权逻辑，不执行实际 HTTP。测试覆盖构造/调用默认禁网、双开关、来源优先级、Unicode 与版本信息、时长容差、无同步歌词、错误回退、正/负缓存版本隔离、过期、容量和服务回调兼容性。

当前本机 Python 为 3.13.1。项目 `pyproject.toml` 声明 `requires-python >=3.10`，但既有 `contracts.py` 导入 Python 3.11 引入的 `enum.StrEnum`，所以当前整体实际至少需要 Python 3.11。这里只记录不一致，不修改依赖声明或公共模型；运行环境下限的统一由负责人处理。

## 已观察的 SMTC 元数据限制

负责人在主整合实测中观察到：`Artist='林俊杰 — 裹着心的光 - Single'`、`AlbumTitle=''`。resolver 将该 Artist 作为完整艺术家字段使用，因此会拒绝仅返回 `artistName='林俊杰'` 的外部响应；这是预期的安全拒绝，可能减少命中率。

不基于破折号、连字符或 `Single` 等字样自行拆分、猜测或改写艺术家与专辑，也不通过放宽精确匹配规避此限制。若要转换此类混合字段，需要独立、可核实的元数据证据，确认真实艺术家、专辑及版本；应由上游元数据适配负责，而非歌词 resolver 自行推断。本条源于负责人提供的本地实测观察，本子任务没有发起外部请求核实。

## 本次验证结果

- 新增歌词来源离线测试：31 项通过。
- 当前整合工作区全量本地测试：79 项通过；运行时阻断外部 HTTP、DNS 和连接，仅允许既有本机回环 IPC 测试。
- `compileall -q src tests` 通过，编译缓存隔离在任务生成的临时目录中，检查后移除，未覆盖已有项目缓存。
- 未执行实际外部歌词 API、文档下载、联网安装或 Apple 私有缓存读取；未扩展来源实现或主程序职责。
