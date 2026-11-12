# 更新日志

格式说明（发布脚本依赖这个格式）：

- 每个版本一节，标题为 `## vX.Y.Z`，最新的在最上面。合并到 main 时，CI 读取最上面一节：如果这个版本还没有发布，就自动打包并发布 Release，发布说明由这一节生成。
- 节内用 `### 新功能`、`### 改进`、`### Bug 修复`、`### 注意` 等小标题，条目写成 `- **短标题** — 说明`。
- 改了 `src/` 的 PR 必须在最上面加一个新版本节（PR 检查会验证），这样每次合并都会出一个带更新日志的包。

## v0.3.0

### 新功能
- **Ctrl+鼠标左键点击 = 转到定义** — 不再是搜索。按住 Ctrl 悬停在标识符上会加下划线、光标变成手形；Ctrl+Alt / Ctrl+Shift 点击仍是 VS 的多光标 / 扩展选择
- **转到定义 / 声明（Alt+G）与转到实现（Alt+Shift+G）** — Alt+G 在声明和定义之间来回切换；一个目标直接跳，多个目标弹出选择器（类名::成员、文件:行、声明/定义/重写徽章、模块与来源），找不到时状态栏说明原因。已接管 VS C++ IntelliSense 时 F12 也走 UEToVS
- **查找引用（Shift+Alt+F）** — 弹窗显示，按文件分组，行号 + 行文本并高亮匹配处，读 / 写 / 调用等徽章，仅按名称匹配的结果灰显；工具条可重新搜索、只看写入、包含引擎代码；双击或 Enter 跳转，弹窗保持打开方便逐条查看；底部显示命中数、文件数、耗时
- **类型层次** — 弹窗分“基类 / 派生类”两个标签，未在索引中的类型灰显，双击跳转
- **引用高亮** — 光标停留约 150 ms 后高亮当前符号在本文件中的其他出现处，读和写用两种颜色，颜色和开关都可设置
- **导航栏** — C++ 编辑器顶部显示当前类型和当前函数，点击弹出可模糊过滤的列表，选中即跳转，可关闭
- **成员列表（Alt+M / Alt+Shift+M）** — 当前文件的声明，或光标所在类型的全部成员（含继承，继承来的灰显），输入即过滤
- 以上命令都在 扩展 > UEToVS、编辑器右键 > UEToVS 和 Find Action 中
- **Search Everywhere（双击 Shift）** — 在 VS 任意位置连按两次 Shift 打开 Rider 风格全局搜索，分 全部 / 类型 / 符号 / 文件 / 资产（蓝图） / 操作 六个页签；支持 `/types` `/files` 等前缀、`AActor::Tick`、`Foo.cpp:120:8`、路径片段；最近打开的文件优先；项目里没有结果时自动显示引擎/插件中的匹配；再次双击 Shift 切换“包含非项目项”；Ctrl+Enter 把结果放到“UEToVS 搜索结果”工具窗口
- **导航历史** — 后退 Alt+← / 前进 Alt+→（鼠标侧键也可以），记录跳转和较大的光标移动；上一个/下一个编辑位置；最近的文件、最近的位置（再按一次快捷键只看已编辑）；文件结构；跳转到… 菜单。历史保存在 `<解决方案>\.vs\UEToVS\Navigation`
- **头文件/源文件切换 Alt+O 与相关项** — 识别 Public/Private 目录；相关项包括 `Foo_Implementation` / `Foo_Validate`、`.generated.h`、所属模块的 Build.cs
- **书签** — 切换书签、0–9 数字书签（设置 / 跳转）、书签列表（Delete 删除），编辑器左侧显示标记并随编辑移动
- **Build.cs 模块依赖修复** — 保存 .h/.cpp 时检查 `#include` 的头文件所在模块是否已加入 Build.cs，缺失时一键加入 Public/PrivateDependencyModuleNames；也可在 `#include` 行或类型名上手动执行
- **UE 编辑器联动** — 通过 UEToVSLink 插件在编辑器中打开搜索到的资产（蓝图），一键 Live Coding（Ctrl+Alt+F11）；提供“安装 UEToVSLink 插件到当前项目”和“生成工程文件”
- **Rider 键位方案（可选）** — Find Action 中“应用键位方案 — Rider 默认键位 / Rider（Visual Studio 键位）”，修改前自动备份，可一键恢复
- **核心导航（Engine 端）** — 转到定义 / 声明 / 实现与 VA 式 Alt+G 互跳都由 Engine 按作用域解析：光标在声明上直接命中；`A::B`、`Super::`、`this->`、`obj.x` / `p->x`（从局部变量、字段、`Cast<T>` 推断类型）、函数内局部变量与参数、当前类及基类成员、外围命名空间、全局依次查找；重载按实参个数和类别挑选；`#include "X.h"` 打开头文件；`GENERATED_BODY` 等宏不乱跳。每次解析都写 `nav.resolve` 日志（单词、原因、候选数、耗时）
- **UE 规则** — `UFUNCTION(BlueprintNativeEvent / Server / Client / NetMulticast)` 的 Foo 与 `Foo_Implementation` / `Foo_Validate`（WithValidation）互相跳转；转到实现列出派生类里的 override（类型则列出派生类型）
- **查找引用** — 按名字查索引里的出现位置并合并未保存的缓冲区，再逐个按作用域过滤（不同类的同名成员不会混在一起），标出声明 / 定义 / 读 / 写 / 调用 / 类型使用，带行文本和所在函数；支持结果上限、取消、是否包含引擎代码
- **类型层次、引用高亮、导航栏上下文、成员列表** — 基类 / 派生树（防环、深度与节点上限，未索引的基类按原文显示）；高亮只看当前文档、按文本版本缓存；导航栏给出外→内作用域和全文件声明；成员列表含继承成员与 GENERATED_BODY 注入成员，派生类 override 只保留最派生的
- **MCP 服务（给 AI Agent 用）** — 自带 `UEToVS.Mcp.exe`（stdio），让 VS Copilot Agent 模式、Claude Code、Cursor、VS Code 直接查 UEToVS 的索引：`search_symbols`、`search_files`、`search_assets`（蓝图等资产）、`get_file_outline`、`go_to_definition`、`find_references`、`get_type_hierarchy`、`get_members`、`index_status`。VS 开着时问 Engine，没开时读磁盘索引。合成工程上常见查询比 rg 快 13–25 倍，详见 docs/design/MCP.md
- **自动注册到 Visual Studio** — 打开 UE 解决方案时把服务写进 `.vs\mcp.json`，在 Copilot Agent 模式的工具列表里就能勾选。可在 工具 > 选项 > UEToVS > 启用 MCP 服务 关闭
- **写入 .mcp.json 到解决方案目录** — 新命令（扩展 > UEToVS），生成 Claude Code / Cursor / VS Code 也能读的 `.mcp.json`；已有文件会合并，不覆盖其他服务器

### 改进
- **索引记录继承关系** — 类的基类（含模板实参原文）和“基类 → 派生类”倒排表写入索引段；项目文件额外索引声明里出现的类型名（字段 / 参数 / 基类），查找引用能找到头文件里的用法

### 注意
- 旧原型目录 `legacy/` 已删除，原型的全部功能已在新架构上重新实现
- 原型的“全文搜索（Text 页签）”暂未迁移：Engine 还没有全文搜索接口，`/text` 前缀暂时按“全部”搜索
- 索引格式升级（段格式 v2），升级后第一次打开会自动重建索引

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
