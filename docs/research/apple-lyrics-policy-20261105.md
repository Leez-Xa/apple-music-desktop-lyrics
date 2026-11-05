# Apple 歌词观测与逐行同步边界（2026-11-05）

## 证据范围

依据主负责人提供的本机观察：UIA `TimeBasedLyrics` pane 下的 Button Name
可提供逐句文字，其中含 U+2060 WORD JOINER；暴露的 patterns 为 Invoke、
ScrollItem，没有 SelectionItem，也没有取得每句时间戳的证据。
后续 Apple 会话消失，SMTC disconnected，UI LCD 仅 AppleLogo。
本任务没有重新观察运行中播放器，也没有操作播放、激活窗口或切换桌面。
这些事实只支持“观察到文字”，不支持 Apple 原始时间轴或当前句。

## 公共接口与合同

实现独立于公共 contracts、歌词 resolver、主入口及 bridge，不改这些既有文件。

```python
parse_apple_lyrics_observation(
    payload: object, state: PlaybackState,
) -> AppleLyricsObservation | None

get_apple_timed_result(
    observation: AppleLyricsObservation | None, state: PlaybackState,
) -> LyricsResult | None

combine_apple_text_with_timing(
    observation: AppleLyricsObservation | None,
    external: LyricsResult,
    state: PlaybackState,
) -> LyricsResult | None
```

`normalize_apple_text(text: str) -> str` 也是可用纯函数。
`AppleLyricsObservation` 为 frozen、slots dataclass：version、status、
observed_at（aware UTC datetime）、title、subtitle、lines
（`tuple[AppleLyricLine, ...]`）、timing_status、current_index、reason。
`AppleLyricLine` 同样不可变，仅有 index、text，无时间或选中语义。

parser 仅消费已解码的 dict，以下八项均必填：version=整数 1；
status=observed 或 unavailable；observed_at；title；subtitle；
lines=[{index,text}]；timing_status=not_exposed；current_index=null。
布尔值不作为版本或索引整数。额外 handles、布局、patterns 等元数据忽略；
reason 可选，为有界字符串或 None。缺字段、无效类型、超限、身份不符返回 None。

UTC ISO 时间须包含日期、T、时分秒及 Z 或 +00:00，支持 1–7 位小数秒，
包括 PowerShell `DateTimeOffset.UtcNow.ToString('o')` 的七位输出。
Python datetime 保存到微秒，末位亚微秒精度不保留；它仅为抓取时间，
从不作为歌词时间。拒绝无时区、非 UTC、仅日期及无效日历时间。

observed 标题必须与当前 state.title 在 NFC 比较意义上相等；
subtitle 必须与完整 state.artist 相等。比较前仅移除 U+2060 与 U+FEFF。
不忽略大小写、标点、外侧空格或版本名，不拆解 SMTC 的艺术家/专辑合并字段。
当前标题与艺术家必须非空。unavailable 保留 reason，但 title、subtitle、
lines 必须为空，不接受残留歌词；同样要求 observed_at，不能自行补造时间。

## 大小与文本边界

| 项目 | 上限与规则 |
| --- | --- |
| 行数 | 2,048 |
| 单行原始文字 | 4,096 Unicode 码点 |
| 所有行原始文字总数 | 262,144 Unicode 码点 |
| title / subtitle / reason | 每项 1,024 Unicode 码点 |
| 时间字符串 | 64 字符 |
| index | 严格递增、唯一、整数 0–2,047；允许缺号，不排序修复 |

原始长度先于清理检查，避免不可见字符绕过大小限制。
文字不允许 CR、LF、NUL 或未配对 UTF-16 surrogate。
只移除 WORD JOINER 和 BOM，不笼统删除 Unicode Cf，保留可能影响实词的
ZWJ、ZWNJ、零宽空格、variation selector。保留大小写、标点、空格和原有
组合字符表示。NFC 仅用于相等比较，不采用 NFKC、casefold、模糊/近似匹配。

parser 不接收 JSON 字节、不打开文件，不为额外元数据设置反序列化资源预算；
bridge 应在 JSON 解码前另行限制原始响应字节数与嵌套深度。

## 无时间观测不能成为时间轴

`get_apple_timed_result` 对当前合同始终返回 None，适合作为 resolver 的
Apple callback 适配器。observed_at、列表顺序、index、屏幕位置、居中行、
Invoke、ScrollItem、播放进度及播放状态均不会被转成句子时间或 current_index。
即使文字看起来完整，单独的 Apple 观测也不产生 LyricsResult。

## 与外部时间的严格组合

组合是可选的独立 helper；本任务未连接 resolver 或主入口。
调用者须先从已验证的来源取得 external LyricsResult，再单独调用此函数。

1. 再核对 observation 的标题与完整 artist 对应当前 state。
2. external.track_key 必须严格等于 state.track_key，包括专辑；不重写 key
   来掩盖错曲或版本差异。外部提供者须在产生结果前完成自己的元数据校验。
3. 验证外部结果为 line 精度，confidence 有限且在 0–1；所有行（包括空行）
   都有合法、非负、单调不减的整数起始时间。结束时间可为 None，否则不早于
   起始时间；已知时长不得越界，时间也不超过有符号 64 位毫秒上限。
4. 两侧仅为判空使用 strip；去除空行后，整个非空行序列必须非空、数量相同，
   并逐项通过 NFC 精确比较。匹配不 trim 实际文字，不做局部匹配、子序列搜索、
   重分句、文本字典映射、插值或偏移估计。重复句按出现顺序逐项对应。
5. 结果使用 Apple 文字、对应外部行原有 start_ms/end_ms，以及外部 language、
   confidence；source 固定 `apple_music+external_timing`，不提高置信度。
   仅空行被移除，剩余外部时间之间的空隙保留，不补造时间或结束点。

任何一处字句差异、缺行、额外非空行、重排、元数据变化或无效时间都会返回 None。
逐行选中应交给既有 synchronizer，依据外部时间与播放进度计算，不能声称来自
Apple 当前行观测。组合结果不得作为 Apple 原始时间轴回灌 resolver 的 Apple 分支，
因为该分支可能将来源重标为 apple_music；组合应保持独立，保留复合来源。

## 可验证能力的限制

成功只证明本次提供的完整非空序列与该外部序列逐项相等，并保留外部时间。
该 bridge v1 没有专辑、录音 ID、完整性标记或逐句时标；parser 无法单独证明
UIA 抓取涵盖整首、未知版本实际相同、外部节奏正确或同名曲确为同一录音。
不对部分窗口的文字做局部对齐。即使整序列相等，也不承诺 Apple 原始同步精度。
observed_at 在本层仅作格式验证，不实施 TTL；快照新鲜度、窗口身份及采集一致性
由 bridge/调用者负责，换曲后必须重新传入当前 PlaybackState 进行校验。

## 离线验证

新增测试共 30 项：无时间回调及 resolver 回退、不可见字符白名单、NFC、完整
SMTC 字段、UTC/PowerShell 时间、unavailable、额外元数据忽略、索引/大小边界、
重复行顺序、整序列拒绝、换曲/专辑拒绝、空行配对、外部时间合法性与来源标记。
测试文字均为虚构短标签，未抄录真实整首歌词。

2026-11-05 的共享工作区回归快照：discover 180 项，161 项通过、19 项跳过，
无失败；跳过真实 Win32 窗口、PowerShell 执行与实时进程观察等运行时测试。
只允许既有回环 IPC 和无窗口 Python 模拟子进程，阻断外部 HTTP、DNS 和连接。
`compileall` 对 src、tests 通过；缓存采用本任务独立临时目录和短文件名，
检查后清理，不覆盖原项目编译缓存。首次检查曾受 Windows 缓存路径长度和
过严的测试进程拦截影响，调整验证环境后通过，未因此修改既有实现。

未联网、下载、安装、读取私有缓存/凭据、启动播放器、显示窗口或切换桌面。
没有运行 live Apple 采集；完成离线测试不代表运行时歌词能力已得到验证。
