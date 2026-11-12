# 更新日志

格式说明（发布脚本依赖这个格式）：

- 每个版本一节，标题为 `## vX.Y.Z`，最新的在最上面。合并到 main 时，CI 读取最上面一节：如果这个版本还没有发布，就自动打包并发布 Release，发布说明由这一节生成。
- 节内用 `### 新功能`、`### 改进`、`### Bug 修复`、`### 注意` 等小标题，条目写成 `- **短标题** — 说明`。
- 改了 `src/` 的 PR 必须在最上面加一个新版本节（PR 检查会验证），这样每次合并都会出一个带更新日志的包。

## v0.3.1

### 改进

### Bug 修复

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
- **C++ / 蓝图索引线程数可配置** — 两者各自设置并发线程数，默认 = 逻辑核心数 / 4（至少 1）；运行时修改立即生效，不用重启引擎（引擎命令行也可用 `--cpp-threads` / `--asset-threads` 指定初始值）
- **编译期间的索引策略** — 编译时可选“暂停 / 单线程继续（默认）/ 全速”；索引线程平时为 BelowNormal，编译期间降为 Lowest，不拖慢编译
- **分开重建** — 可单独重建项目索引、引擎索引、蓝图索引，或全部重建；进度显示在状态里，连续点多次会合并成一次
- **引擎索引与项目索引分开存放** — 引擎部分在 `.vs\UEToVS\Index\engine`，项目部分与增量在 `Index\project`；改项目文件只更新项目部分。可选把引擎部分放进按引擎版本 + 构建号共享的目录（`%LocalAppData%\UEToVS\EngineIndex`，默认关闭），同一引擎的第二个工程几秒内就能打开
- **自动注册到 Visual Studio** — 打开 UE 解决方案时把服务写进 `.vs\mcp.json`，在 Copilot Agent 模式的工具列表里就能勾选。可在 UEToVS 设置 > 常规 > MCP 中关闭
- **写入 .mcp.json 到解决方案目录** — 新命令（UEToVS 设置 > 常规 > MCP，或 Find Action），生成 Claude Code / Cursor / VS Code 也能读的 `.mcp.json`；已有文件会合并，不覆盖其他服务器
- **UEToVS 设置窗口** — 扩展 > UEToVS > 设置…、工具栏最左侧的 UEToVS 图标或 Find Action 打开；分类 常规 / 外观 / 索引 / 快捷键 / 导航 / 日志与诊断 / 性能，支持搜索，底部 恢复默认 / 取消 / 应用 / 确定。旧的 工具 > 选项 设置自动迁移
- **索引设置** — C++ 和蓝图索引线程数（默认逻辑核心数的 1/4，至少 1）、编译期间暂停 / 单线程 / 全速、是否索引引擎源码、实时索引状态；“重建项目索引 / 重建引擎索引 / 重建蓝图索引”三个按钮
- **引擎索引位置** — 默认放在本解决方案的 `.vs\UEToVS\Index`；打开“在多个工程间共享引擎索引”后，同一引擎版本的工程共用一份（默认 `%LocalAppData%\UEToVS\EngineIndex\<引擎版本>`，可修改）
- **快捷键页** — 列出所有 UEToVS 命令及当前快捷键，点击即可录制新快捷键；一键套用 Visual Assist / Rider / VS 默认 预设；检测并可一键解除与其他 VS 命令的冲突；每次修改前自动备份，可还原
- **默认快捷键补充** — Shift+Alt+O 转到文件、Shift+Alt+S 转到符号、Ctrl+Shift+Alt+A Find Action（其余沿用 Alt+G / Alt+Shift+G / Shift+Alt+F / Alt+M / Alt+O / Alt+←→ / 双击 Shift）
- **UEToVS 工具栏** — 默认显示：UEToVS（打开设置）、Search Everywhere、后退、前进、转到定义、查找引用、性能面板；整个工具栏和每个按钮都可以在设置里隐藏
- **品牌图标** — 新的 UEToVS 图标，用于扩展图标、工具栏和设置窗口标题栏
- **UE 感知补全（M3）** — VS 原生补全界面：输入标识符、`.`、`->`、`::`、`#include "`/`<`、`UCLASS(` / `UPROPERTY(` / `meta=(` 等说明符括号内、Ctrl+Space 时弹出；图标按符号种类，UE 反射符号带徽章，继承成员带“继承自 X”标记；右侧显示类型 / 签名；每次按键本地按模糊匹配（精确 > 前缀 > 驼峰 > 子串，可省略 UE 前缀）+ 本类优先 + 最近使用重排；提交时按 VA 习惯把 `.` 改成 `->`（或反之，可在设置关闭）。未接管 VS IntelliSense 时只补 VS 没有的 UE 增强项（说明符、#include、override、UE 反射成员），不重复刷屏
- **Quick Info 与参数信息（M3）** — 悬停 / Ctrl+K,I 显示与编辑器同色的声明、“+N 个重载”、文档注释、UE 反射摘要（蓝图可调用、服务器 RPC 等标签）和可折叠的宏展开；`(`、`,` 与 Ctrl+Shift+Space 显示参数信息，当前参数加粗并随光标移动
- **VA 风格语义着色（M3）** — 类型、函数、变量、宏、枚举值、命名空间、UE 反射宏与说明符等 21 类，出现在 工具 > 选项 > 字体和颜色（“UEToVS …”），深 / 浅色两套默认值随主题切换且不覆盖你改过的颜色；只请求可见区域 ± 50 行，滚动 / 编辑节流，编辑时沿用旧结果避免闪烁
- **导入 Visual Assist 配色** — Find Action 中“导入 Visual Assist 配色…”，选择 VA 导出的 .vssettings，把 VA Class / Method / Variable / Macro / Enum Member / Namespace（含旧版 VA X 名称）映射到 UEToVS 着色项；另有“切换语义着色”“恢复默认语义配色”
- **UE 感知补全（Engine 端）** — 通用补全按 局部变量/参数 → 当前类及基类 → 外围命名空间 → 全局类型/函数/宏 → 关键字与 UE 代码片段 排序，前缀 + 驼峰匹配，近作用域、项目代码、UE 反射成员优先；`.` / `->` 成员补全会推断链式表达式、函数返回值、`TArray` / `TMap` 下标与元素、`TSharedPtr` / `TObjectPtr` / `TWeakObjectPtr` / `TUniquePtr`、`GetOwner()` / `GetWorld()`、委托的 `AddDynamic` / `Broadcast`；`X::` / `Super::` / 枚举值；`UCLASS / UPROPERTY / UFUNCTION / USTRUCT / UENUM / UINTERFACE(...)` 与 `meta=(...)` 里补说明符（已用的和互斥的不再出现）；`#include` 补模块 Public 下的相对路径；类体内 `virtual ` 后补可重写的基类虚函数并插入完整签名（含 BlueprintNativeEvent 的 `_Implementation`）；指针用了 `.` 或值用了 `->` 时给出 AccessorFix。每次补全写 `is.completion` 日志，超过 50 ms 记 Warn
- **Quick Info / 参数信息 / 语义着色（Engine 端）** — Quick Info 显示格式化声明、`/** */` `///` `//` 文档注释、UE 反射摘要（含 meta）、宏的一层与完全展开、重载数；参数信息能正确定位嵌套括号、模板尖括号、字符串和 lambda 中的实参序号，列出全部重载并按实参个数选中、保留默认值；语义着色只算请求的行，按文本版本缓存，编辑后复用名字判定，区分 UE 类型 / UE 宏 / 说明符 / 虚函数 / 静态成员 / 参数 / 局部变量等，声明处带 Declaration
- **UE 宏知识库补齐说明符表** — UCLASS / UINTERFACE / USTRUCT / UENUM / UMETA / UPARAM / UPROPERTY / UFUNCTION / UDELEGATE 的说明符与 meta 键，含说明、取值提示和互斥关系

### 改进
- **索引记录继承关系** — 类的基类（含模板实参原文）和“基类 → 派生类”倒排表写入索引段；项目文件额外索引声明里出现的类型名（字段 / 参数 / 基类），查找引用能找到头文件里的用法
- **索引速度** — 2 万文件的合成 UE 工程（2 线程）：首次索引 41.9 s → 10.2 s，引擎解析 723 → 2301 文件/秒；解析结果按批写成索引段，写段与解析并行，不再逐文件进增量再反复合并
- **索引顺序** — 项目源码先于引擎；引擎里 Public/Classes 头文件先于其他头文件，再到 .cpp，ThirdParty 最后
- **吞吐统计** — 日志新增 `index.throughput`（每 5 秒）和 `index.done`（每阶段汇总：文件/秒、MB/秒、每文件解析 p50/p95/max、最慢 20 个文件、IO/CPU 占比、线程利用率），性能面板快照也带这些数据
- **跳过的目录可配置** — 默认跳过 Intermediate、Binaries、Saved、DerivedDataCache 和点开头的目录
- **VS 2026 风格** — 组件库整体改为 VS 2026 / Fluent 视觉：控件圆角 4、弹窗圆角 8、仅键盘焦点时显示的焦点环、输入框聚焦强调色下划线、列表选中指示条、细滚动条、Fluent 开关与复选框；默认界面风格改为“VS 2026”（保留紧凑 / 宽松），旧的“Rider 风格”自动迁移
- **分段控件与筛选芯片** — 改为圆角矩形，选中项用强调色填充
- **性能面板与组件画廊改为浮动窗口** — 记住位置和大小，双击标题栏恢复默认；性能面板的采样间隔可在设置里调整
- **精简菜单** — 扩展 > UEToVS 只保留搜索、导航、性能面板和设置…；重启引擎、接管 / 恢复 IntelliSense、日志、诊断包、组件画廊移到设置窗口，仍可在 Find Action 中搜索并绑定快捷键

### Bug 修复
- **重启 VS 后索引缓存不生效** — 以前每次打开都把全部 C++ 文件重新解析一遍（2 万文件 62 s），现在直接复用缓存（0.6 s）
- **.ini 等非 C++ 文件不再进入解析** — 配置文件、插件描述等只用于文件搜索；它们变化时也不再计入“已解析”进度

### 注意
- 工具 > 选项 > UEToVS 下只剩一个跳转到新设置窗口的按钮
- 状态栏的“正在从 X.uproject 中扫描 Blueprint … Config/*.ini”来自 VS 自带的 Unreal Engine 集成，不是 UEToVS；它没有公开的开关，设置窗口 常规 页有说明
- **默认接管 VS 自带 C++ IntelliSense** — UEToVS 已能提供完整的 C++ 感知，打开 UE 解决方案时默认关闭 VS 自带 C++ IntelliSense（原设置先备份）；首次接管会在信息栏说明并提供“恢复”。接管后 Ctrl+Space / Ctrl+J / Ctrl+Shift+Space / Ctrl+K,I / 悬停 / F12 都由 UEToVS 处理
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
