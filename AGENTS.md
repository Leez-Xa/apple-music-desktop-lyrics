# 项目执行规则

## 目标

本项目实现 Windows Apple Music 的桌面歌词能力。当前优先完成可验证的核心模块，再基于运行时证据适配 Apple Music。

## Agent 规则

- 所有子 agent 必须使用 `gpt-6.1-sol` 模型；如果调度器无法保证该模型，停止派发并报告。
- 每个子任务必须声明独立写入范围。
- 侦察 agent 只写 `docs/research/` 和 `research/apple-music/`。
- 播放模块只写 `src/amlyrics/playback.py`、`src/amlyrics/windows_smtc.py` 及对应测试。
- 歌词模块只写 `src/amlyrics/lrc.py`、`src/amlyrics/synchronizer.py` 及对应测试。
- IPC 和公共数据模型变更必须由执行负责人统一合并，避免协议漂移。
- Apple Music 适配器在没有证据前只能做只读观察，不得 DLL 注入、补丁安装包或修改 WindowsApps 内容。

## 变更流程

1. 先读取目标文件和工作区状态。
2. 普通新增或低风险编辑前说明目的。
3. 修改后运行 `python -m compileall -q src tests` 和 `python -m unittest discover -s tests -v`。
4. 任何依赖安装、网络下载、系统级配置和外部服务写入都必须先获得用户授权。
5. 每个 agent 完成时报告修改文件、验证结果、已知限制和接口影响。

## 安全边界

不得删除、移动、重命名、覆盖既有用户文件；不得执行危险 Git 操作；不得读取账号凭据或绕过 DRM。Apple Music 运行时研究只针对本机已安装客户端和可观察的 UI/进程行为。
