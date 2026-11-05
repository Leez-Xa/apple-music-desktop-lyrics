# REA 与运行时研究流程

REA 只作为研究工具，不作为歌词产品的运行时依赖。研究输出必须能脱离 REA 被其他 agent 复核。

## 研究顺序

1. 记录 Apple Music 版本、安装来源和进程路径。
2. 记录主进程、子进程、加载模块和窗口层级。
3. 使用 UI Automation 检查播放器控制栏、歌词按钮和可访问名称。
4. 观察启动、播放、暂停、切歌、拖动进度和打开歌词页的状态变化。
5. 若存在 WebView，再记录页面边界和可观察 DOM/脚本入口；若无证据，不假设存在 WebView。
6. 将结论写入证据文件，并标注 `observed`、`inferred` 或 `unknown`。

## 证据格式

```text
observed_at: 2026-11-05T00:00:00+08:00
client_version: unknown
operation: open-player-and-play
source: UI Automation / process inspection / REA
fact: ...
confidence: high|medium|low
next_check: ...
```

## 禁止事项

- 不提取账号凭据、Cookie 或私有密钥。
- 不绕过 DRM，不抓取或解密音频内容。
- 不修改 WindowsApps 安装文件。
- 不注入未知模块，不在没有回滚方案时执行 Hook。
