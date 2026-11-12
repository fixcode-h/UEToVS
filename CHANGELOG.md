# 更新日志

格式说明（发布脚本依赖这个格式）：

- 每个版本一节，标题为 `## vX.Y.Z`，最新的在最上面。合并到 main 时，CI 读取最上面一节：如果这个版本还没有发布，就自动打包并发布 Release，发布说明由这一节生成。
- 节内用 `### 新功能`、`### 改进`、`### Bug 修复`、`### 注意` 等小标题，条目写成 `- **短标题** — 说明`。
- 改了 `src/` 的 PR 必须在最上面加一个新版本节（PR 检查会验证），这样每次合并都会出一个带更新日志的包。

## v0.3.0

### 新功能
- **MCP 服务（给 AI Agent 用）** — 自带 `UEToVS.Mcp.exe`（stdio），让 VS Copilot Agent 模式、Claude Code、Cursor、VS Code 直接查 UEToVS 的索引：`search_symbols`、`search_files`、`search_assets`（蓝图等资产）、`get_file_outline`、`go_to_definition`、`find_references`、`get_type_hierarchy`、`get_members`、`index_status`。VS 开着时问 Engine，没开时读磁盘索引。合成工程上常见查询比 rg 快 13–25 倍，详见 docs/design/MCP.md
- **自动注册到 Visual Studio** — 打开 UE 解决方案时把服务写进 `.vs\mcp.json`，在 Copilot Agent 模式的工具列表里就能勾选。可在 工具 > 选项 > UEToVS > 启用 MCP 服务 关闭
- **写入 .mcp.json 到解决方案目录** — 新命令（扩展 > UEToVS），生成 Claude Code / Cursor / VS Code 也能读的 `.mcp.json`；已有文件会合并，不覆盖其他服务器

## v0.2.3

### 改进
- **IntelliSense 接管 / 恢复合并成一个菜单项** — 扩展 > UEToVS 里不再同时列出“接管”和“恢复”两项，只显示当前能做的那一个：还没接管时是“接管 VS C++ IntelliSense”，接管后变成“恢复 VS 自带 C++ IntelliSense”，恢复后又变回“接管”。Find Action 里两个操作仍然都能搜到

## v0.2.2

### 新功能
- **可选关闭 VS C++ 后台代码分析** — 工具 > 选项 > UEToVS > 常规 > “关闭 VS C++ 后台代码分析”（默认关闭）。VS 每次打开或保存 .cpp 都会在后台完整编译分析整个文件，UE 源文件常要几十秒（任务中心里的“C++ 后台 Code Analysis”）。开启后打开 UE 解决方案时自动关掉它，原值先备份，关闭此项即恢复；手动“分析 > 运行代码分析”不受影响

## v0.2.1

### Bug 修复
- **覆盖升级不再装出两个 UEToVS** — v0.2.0 换了扩展的标识 (Identity Id)，VS 把它当成另一个扩展，和旧版并排显示。现在改回旧版一直使用的标识，以后的版本都会直接覆盖升级

### 注意
- 如果“扩展 > 管理扩展”里已经有两个 UEToVS：先卸载描述为“Unreal Engine aware C++ assistant...”的那个（v0.2.0），关闭 VS 完成卸载，再安装 v0.2.1，它会覆盖升级旧版那一个
- 设置、`.vs\UEToVS` 下的索引和日志都会保留

## v0.2.0

### 新功能
- **全新架构** — 按设计文档重建：独立 Engine 进程负责解析与索引，VS 内只保留界面与编辑器集成，崩溃互不影响
- **UE 感知的 C++ 解析器** — 识别 UCLASS / UFUNCTION / UPROPERTY / GENERATED_BODY / 委托宏等，不依赖 `.generated.h`，约 90 MB/s
- **分段索引** — 内存映射加载，存放在 `<解决方案>\.vs\UEToVS\Index`；25 万符号搜索 p95 约 15 ms
- **性能面板** — 扩展 > UEToVS > 性能面板：Engine 与 VS 内存、CPU 曲线、索引各阶段耗时、各操作 p50 / p95 延迟
- **Find Action** — 扩展 > UEToVS > Find Action，搜索并执行所有 UEToVS 命令
- **外观设置** — 工具 > 选项 > UEToVS > 外观，Rider 风格，字号、行高、圆角、强调色、徽章颜色等都可微调
- **接管 VS C++ IntelliSense** — 可一键关闭 / 恢复 VS 自带 C++ 感知（修改前自动备份）
- **中英双语** — 跟随 VS 界面语言：英文 VS 显示英文，其他语言显示中文

### 改进
- **日志** — 宿主与 Engine 的日志写到 `<解决方案>\.vs\UEToVS\Logs`（JSON Lines），包含异常堆栈、资源采样和延迟统计，方便排查与 AI 分析
- **一键诊断包** — 扩展 > UEToVS > 生成诊断包，打包日志与设置

### 注意
- 这是新架构的第一个版本，补全、转到定义等编辑器内功能从后续版本开始提供；旧版的 Search Everywhere 等导航功能会在新架构上重新实现
- “接管 VS C++ IntelliSense”目前默认关闭，自有补全完成前不建议开启
