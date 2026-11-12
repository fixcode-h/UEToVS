# UEToVS

面向 Unreal Engine 5 C++ 工程的 Visual Studio 2022 / 2026 扩展：对标 Visual Assist 的 C++ / UE 感知能力，交互参考 Rider。

- 整体设计：[docs/design/UEToVS-架构设计.md](docs/design/UEToVS-架构设计.md)
- 当前状态：**v0.5.0**。M0–M4 已完成：原型功能对齐（Search Everywhere、导航历史、书签、相关项、Build.cs 依赖修复、资产搜索、Live Coding、Rider 键位）、导航（转到定义/实现、查找引用、类型层次、调用层次、速览定义、导航栏、引用高亮）、IntelliSense（UE 感知成员补全、Quick Info、宏展开、参数信息、语法着色、内嵌提示、UE 规则波浪线、反射说明符补全、通用模板实例化）、重构与生成（Alt+Enter、重命名、更改签名、移动实现、提取方法、引入变量、封装字段、前置声明、生成实现、Live Templates、Surround With）、UE 专项（新建 UE 类、编译错误波浪线、UE 日志窗口、自动化测试运行器、全项目检查、引擎升级助手）。另有给 AI Agent 用的 MCP 服务（见下文）。

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
| Search Everywhere | 双击 Shift，Ctrl+T | 页签：全部 / 类型 / 符号 / 文件 / 资产（蓝图） / 操作；`/types` `/files` 等前缀，`AActor::Tick`，`Foo.cpp:120:8`；再双击 Shift 切换“包含非项目项”；Ctrl+Enter 结果放到工具窗口 |
| 转到类型 / 符号 / 文件 / 资产 | Shift+Alt+T / Shift+Alt+S / Shift+Alt+O / Shift+Alt+B | 直接打开对应页签 |
| 后退 / 前进 | Alt+← / Alt+→，鼠标侧键 | 记录跳转和较大的光标移动（同文件超过 15 行） |
| 最近的文件 / 最近的位置 | Shift+Alt+E / Shift+Alt+L | 弹窗中再按一次 Ctrl+E / Ctrl+Shift+E 只看已编辑 |
| 上一个 / 下一个编辑位置、文件结构、跳转到… | Ctrl+Shift+Backspace / Ctrl+Shift+Alt+Backspace、Ctrl+Shift+Alt+M、Shift+Alt+N | |
| 切换头文件/源文件 | Alt+O | Public/Private 目录感知；多个候选时弹出列表 |
| 相关项 | Shift+Alt+R | `Foo_Implementation` / `Foo_Validate`、对应文件、`.generated.h` |
| 打开所属模块的 Build.cs / 为 #include 添加模块依赖 | Ctrl+Shift+Alt+U, B / Ctrl+Shift+Alt+U, A | 保存 .h/.cpp 时也会自动检查 |
| 书签 | Ctrl+F2 切换，Ctrl+Shift+F2 列表 | 切换、0–9 数字书签、书签列表（Delete 删除） |
| Live Coding / 生成工程文件 / 安装 UEToVSLink 插件 | Ctrl+Alt+F11 / Ctrl+Shift+Alt+U, G / Ctrl+Shift+Alt+U, E | Live Coding 与打开资产需要编辑器启用 UEToVSLink 插件 |
| 应用 Rider 键位方案 / 恢复 VS 键位 | — | 在 Find Action 中执行；修改前备份到 `%LocalAppData%\UEToVS\keymap-backup.json` |

所有功能都注册为操作，可在“查找操作…”和 Search Everywhere 的“操作”页签中搜索。每个命令都有默认快捷键（设置、日志、诊断等工具命令用 `Ctrl+Shift+Alt+U` 再按一个字母），与 VS 自带快捷键冲突时以 UEToVS 为准，修改前自动备份；可在 UEToVS 设置 > 快捷键 中修改、套用预设或还原备份。

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
- 字体（UEToVS 设置 > 外观 > 字体）：扩展自带 IBM Plex Sans（英文）、思源黑体 Noto Sans SC（中文，GB2312 子集）、Maple Mono NL（代码，无连字，自带中文，汉字正好两个字符宽，代码里的中文注释和字符串能对齐），默认用于 UEToVS 界面和代码预览。“由 UEToVS 管理 VS 字体”（默认开）代替 VS 的“字体和颜色”设置文本编辑器、输出、查找结果、环境等类别的字体：内置字体先为当前用户安装（无需管理员），修改前把原字体备份到 `%LocalAppData%\UEToVS\vs-fonts-backup.json`，关闭该选项或把类别设为“不修改”即恢复。字体由 `scripts/build_fonts.py` 生成，许可证（SIL OFL 1.1）在 VSIX 的 `Fonts\<字体>\OFL.txt`。

## 在 VS 自带聊天里用自定义 API 的模型（Agent / API）

UEToVS 设置 > Agent / API：添加 API（名称、地址、密钥、密钥的发送方式、并发上限），“测试连接”或“从接口获取”模型，勾选要用的模型后应用。勾选的模型会自动写入 VS Copilot 聊天的自定义模型（Ollama 类型，地址是 UEToVS 的本地转发服务 `http://127.0.0.1:47811`），重新打开聊天窗口即可在模型选择器里选择。密钥用 Windows DPAPI 加密保存在本机，不会交给 VS。目前支持 OpenAI Chat Completions 兼容的 API。原理和限制见 [docs/design/Agent-API.md](docs/design/Agent-API.md)。

## 给 AI Agent 用（MCP）

UEToVS 自带一个 MCP 服务（`Engine\Mcp\UEToVS.Mcp.exe`），让 AI Agent 直接查 UEToVS 的符号索引和蓝图资产，不用 grep 全工程。

- **Visual Studio 2022 17.14+ / 2026**：打开 UE 解决方案后扩展自动把服务写进 `<解决方案目录>\.vs\mcp.json`。在 Copilot Chat 切到 Agent 模式，在工具列表里勾选 `uetovs` 即可。开关：UEToVS 设置 > 常规 > MCP > 启用 MCP 服务（默认开）。
- **Claude Code**：在 UEToVS 设置 > 常规 > MCP（或 Find Action）中执行“写入 .mcp.json 到项目根目录”。有版本管理（Git / SVN / Perforce / Mercurial / Plastic）时写到版本库根目录，否则写到 UE 项目根目录（解决方案目录）；已有文件会合并，不会覆盖别的服务器。Claude Code 在该目录启动即可识别。条目不含本机绝对路径（通过 `%LOCALAPPDATA%\UEToVS\Mcp\uetovs-mcp.cmd` 启动器启动，`--solution-dir` 写相对路径），可以提交进版本库，装了扩展的同事直接能用。
- **Cursor / VS Code**：它们分别读 `.cursor\mcp.json` 和 `.vscode\mcp.json`，写法见 [docs/design/MCP.md](docs/design/MCP.md#4-各客户端配置示例)。
- **工具**：`search_symbols`（按名字/种类/UE 宏找符号）、`search_files`、`search_assets`（蓝图、关卡、材质…）、`get_file_outline`、`go_to_definition`、`find_references`、`get_type_hierarchy`、`get_members`、`index_status`；UE 专用的 `get_reflection_info`（反射说明符、复制、RPC）、`get_module_info`（模块类型、插件、Build.cs 依赖）、`suggest_include`（include 路径 + Build.cs 依赖检查）、`search_config`（ini、config 属性、CVar、GameplayTag）、`find_delegate_bindings`（委托/输入/定时器绑定）；Lua 脚本用 `search_lua`、`search_lua_files`、`get_lua_outline`。
- **Lua 脚本**：在 UEToVS 设置 > 索引 > Lua 脚本 填写“Lua 脚本路径”（如 `Content/Script`，多个用 `;` 分隔，相对路径相对于 .uproject 所在目录），应用后自动索引，也可以点“索引 Lua 脚本”重新解析全部。之后保存、新增、删除、重命名 .lua 文件会增量更新，和 C++ 项目索引一样。索引写在 `.vs\UEToVS\Lua\lua-index.json`，MCP 直接读这个文件，VS 没开也能查。
- **数据来源**：VS 开着时问正在运行的 Engine；VS 没开时直接读磁盘上的索引（需要之前用 VS 打开过一次该解决方案）。
- **比 grep 快多少**：8,000 个源文件的合成工程上，找定义 / 找调用者 / 找子类等问题，rg 要 18–20 ms，MCP 一次往返 1–3 ms（快 13–25 倍），工程越大差距越大；而且结果是结构化的（种类、UE 标记、所在类、位置），Agent 不用再翻文件确认。详细数据见 [MCP.md §5](docs/design/MCP.md#5-比-grep-快多少)。
- **日志**：`<解决方案目录>\.vs\UEToVS\Logs\mcp-*.log`，每次调用一行 `mcp.tool.*`。
