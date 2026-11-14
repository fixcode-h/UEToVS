# ue/UELink：UE 编辑器插件的副本

`UELink/` 是公共库 [fixcode-h/UELink](https://github.com/fixcode-h/UELink) 发布版源码包的原样副本，版本和 sha256 记在 `uelink.json`。UEToVS 和 Agent 面板的 UE 插件共用这个插件；它合并了原来的 UEToVSLink（推送通道：打开资产、Live Coding、日志、自动化测试）和 Agent 面板的 AgentPanelBridge。

- **不在这里改 C++**：改动去 UELink 仓库做、发版，再用 `python scripts/sync_uelink.py [版本]` 换过来（不写版本取最新正式版；会按发布清单校验 sha256），同时在 CHANGELOG 里写一条。
- VSIX 带着这份副本（`EditorPlugin/UELink`），断网、内网也能装；联网时安装会和公共库的最新正式版比，取高的，项目里已有更新的（例如 Agent 面板装的）就不动（`Logic/Link/LinkPluginInstaller.cs`、`Logic/Link/UELinkRelease.cs`）。
- 线上协议和安装约定见 UELink 仓库的 `docs/protocol.md`。
