# UEToVS

面向 Unreal Engine 5 C++ 工程的 Visual Studio 2022 / 2026 扩展：对标 Visual Assist 的 C++ / UE 感知能力，交互参考 Rider。

- 整体设计：[docs/design/UEToVS-架构设计.md](docs/design/UEToVS-架构设计.md)
- 当前状态：**M1 原型功能对齐**。早期原型的全部功能（Search Everywhere、导航历史、书签、相关项、Build.cs 依赖修复、资产搜索、Live Coding、Rider 键位）已在新架构上重新实现，`legacy/` 已删除；编辑器内的补全、转到定义等 IntelliSense 功能从 M2/M3 开始实现。

## 结构

```
src/
  UEToVS.Contracts        宿主 ↔ Engine 的 RPC 契约（netstandard2.0）
  UEToVS.Core             通用逻辑：日志、存储路径（.vs/UEToVS）、中英文、匹配、导航历史、Build.cs 解析
  UEToVS.Engine           Engine 进程（.NET 8）：命名管道 + MessagePack RPC、调度、索引编排、性能数据
  UEToVS.Engine.Unreal    UE 工程模型：.uproject / 插件 / 模块 / 引擎定位 / 文件与资产列举
  UEToVS.Engine.Syntax    UTF-8 零分配词法、轻量预处理、UE 宏知识库、容错 C++ 解析器
  UEToVS.Engine.Index     分段、内存映射的符号索引（字符串池、trigram 剪枝、增量段 + 后台合并）
  UEToVS.UI               WPF 组件库（设计令牌、Rider 风格、可在设置页微调、性能面板）
  UEToVS.Host             VSIX：包加载、命令、设置页、接管 VS C++ IntelliSense、Engine 客户端
tests/                    各模块单元测试（dotnet test）
ue/UEToVSLink             UE 编辑器插件（打开资产、Live Coding），由“安装 UEToVSLink 插件”复制到项目 Plugins/
```

## 功能（扩展 > UEToVS）

| 功能 | 默认快捷键 | 说明 |
| --- | --- | --- |
| Search Everywhere | 双击 Shift | 页签：全部 / 类型 / 符号 / 文件 / 资产（蓝图） / 操作；`/types` `/files` 等前缀，`AActor::Tick`，`Foo.cpp:120:8`；再双击 Shift 切换“包含非项目项”；Ctrl+Enter 结果放到工具窗口 |
| 转到类型 / 符号 / 文件 / 资产 | — | 直接打开对应页签 |
| 后退 / 前进 | Alt+← / Alt+→，鼠标侧键 | 记录跳转和较大的光标移动（同文件超过 15 行） |
| 最近的文件 / 最近的位置 | — | 弹窗中再按一次 Ctrl+E / Ctrl+Shift+E 只看已编辑 |
| 上一个 / 下一个编辑位置、文件结构、跳转到… | — | |
| 切换头文件/源文件 | Alt+O | Public/Private 目录感知；多个候选时弹出列表 |
| 相关项 | — | `Foo_Implementation` / `Foo_Validate`、对应文件、`.generated.h` |
| 打开所属模块的 Build.cs / 为 #include 添加模块依赖 | — | 保存 .h/.cpp 时也会自动检查 |
| 书签 | — | 切换、0–9 数字书签、书签列表（Delete 删除） |
| Live Coding / 生成工程文件 / 安装 UEToVSLink 插件 | Ctrl+Alt+F11（Live Coding） | Live Coding 与打开资产需要编辑器启用 UEToVSLink 插件 |
| 应用 Rider 键位方案 / 恢复 VS 键位 | — | 在 Find Action 中执行；修改前备份到 `%LocalAppData%\UEToVS\keymap-backup.json` |

所有功能都注册为操作，可在“查找操作…”和 Search Everywhere 的“操作”页签中搜索；快捷键可在 工具 > 选项 > 环境 > 键盘 中按 `UEToVS.*` 修改。

## 数据与日志位置

所有数据都在解决方案目录的 `.vs\UEToVS\` 下，删除 `.vs` 即彻底清理：

- `Index\` 索引
- `Logs\` 日志：`host-*.log`、`engine-*.log`，JSON Lines，含会话信息、异常堆栈、每 60 秒资源采样、每 5 分钟各操作延迟统计，可直接交给 AI 分析
- `Navigation\` 导航历史、最近文件、编辑位置、书签
- `Cache\` 其他缓存

## 构建

需要 .NET 8 SDK；VSIX 打包需要 Windows + Visual Studio（“Visual Studio 扩展开发”工作负载）。

```
dotnet test                       # 全部单元测试（Linux / Windows 均可）
msbuild src\UEToVS.Host\UEToVS.Host.csproj /restore /p:Configuration=Release
```

产物：`src\UEToVS.Host\bin\Release\net48\UEToVS.Host.vsix`。Engine 进程会被发布并打包进 VSIX 的 `Engine\` 目录。运行机器需要 .NET 8 运行时。

## 发布

版本号和更新日志都在 [CHANGELOG.md](CHANGELOG.md)。改了 `src/` 的 PR 要在最上面加一节 `## vX.Y.Z`（PR 检查会验证）；合并到 main 后 CI 自动打包 `UEToVS-X.Y.Z.vsix` + `SHA256SUMS`，并用这一节生成发布说明发布 Release。

## 注意

- 接管 VS 自带 C++ IntelliSense（UEToVS 设置 > 常规 > C++ IntelliSense）开启后，打开 UE 解决方案时自动关闭 VS 自带的 C++ IntelliSense（修改前备份到 `%LocalAppData%\UEToVS\intellisense-backup.json`），可在 UEToVS 设置 > 常规 > “立即接管 / 恢复” 一键还原（Find Action 中也有）；卸载扩展前请先执行恢复。自 M3 起该选项**默认开启**（UEToVS 已提供补全、Quick Info、参数信息、语义着色与跳转），首次接管时信息栏会说明并提供“恢复”。
- 界面语言跟随 VS：英文 VS 显示英文，其他语言显示中文。

## 给 AI Agent 用（MCP）

UEToVS 自带一个 MCP 服务（`Engine\Mcp\UEToVS.Mcp.exe`），让 AI Agent 直接查 UEToVS 的符号索引和蓝图资产，不用 grep 全工程。

- **Visual Studio 2022 17.14+ / 2026**：打开 UE 解决方案后扩展自动把服务写进 `<解决方案目录>\.vs\mcp.json`。在 Copilot Chat 切到 Agent 模式，在工具列表里勾选 `uetovs` 即可。开关：UEToVS 设置 > 常规 > MCP > 启用 MCP 服务（默认开）。
- **Claude Code**：在 UEToVS 设置 > 常规 > MCP（或 Find Action）中执行“写入 .mcp.json 到项目根目录”。有版本管理（Git / SVN / Perforce / Mercurial / Plastic）时写到版本库根目录，否则写到 UE 项目根目录（解决方案目录）；已有文件会合并，不会覆盖别的服务器。Claude Code 在该目录启动即可识别。
- **Cursor / VS Code**：它们分别读 `.cursor\mcp.json` 和 `.vscode\mcp.json`，写法见 [docs/design/MCP.md](docs/design/MCP.md#4-各客户端配置示例)。
- **工具**：`search_symbols`（按名字/种类/UE 宏找符号）、`search_files`、`search_assets`（蓝图、关卡、材质…）、`get_file_outline`、`go_to_definition`、`find_references`、`get_type_hierarchy`、`get_members`、`index_status`。
- **数据来源**：VS 开着时问正在运行的 Engine；VS 没开时直接读磁盘上的索引（需要之前用 VS 打开过一次该解决方案）。
- **比 grep 快多少**：8,000 个源文件的合成工程上，找定义 / 找调用者 / 找子类等问题，rg 要 18–20 ms，MCP 一次往返 1–3 ms（快 13–25 倍），工程越大差距越大；而且结果是结构化的（种类、UE 标记、所在类、位置），Agent 不用再翻文件确认。详细数据见 [MCP.md §5](docs/design/MCP.md#5-比-grep-快多少)。
- **日志**：`<解决方案目录>\.vs\UEToVS\Logs\mcp-*.log`，每次调用一行 `mcp.tool.*`。
