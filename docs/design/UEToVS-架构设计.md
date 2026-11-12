# UEToVS 设计文档：Unreal Engine 感知的 Visual Studio C++ 助手

> 版本 0.1（草案） · 2026-11-12
> 目标：在 Visual Studio 2022 / 2026 中提供对标 Visual Assist（下称 VA）的 C++ 与 Unreal Engine 能力，吸收 JetBrains Rider for Unreal（下称 Rider）的交互优点，并在 VA 没覆盖的 UE 场景上做出差异化。本文档只做设计，不含实现代码。

---

## 目录

1. [背景与目标](#1-背景与目标)
2. [现有代码：功能参考与重构依据](#2-现有代码功能参考与重构依据)
3. [竞品调研](#3-竞品调研)
4. [功能规划：三档清单](#4-功能规划三档清单)
5. [总体架构](#5-总体架构)
6. [核心引擎设计（语义层）](#6-核心引擎设计语义层)
7. [VS 宿主层设计](#7-vs-宿主层设计)
8. [UI 组件系统](#8-ui-组件系统)
9. [性能与内存设计](#9-性能与内存设计)
10. [工业级工程约束](#10-工业级工程约束)
11. [关键架构决策（ADR 摘要）](#11-关键架构决策adr-摘要)
12. [路线图与里程碑](#12-路线图与里程碑)
13. [风险与对策](#13-风险与对策)
14. [附录](#14-附录)

---

## 1. 背景与目标

### 1.1 要解决的问题

VS 自带的 C++ IntelliSense（EDG 前端 + 浏览数据库）在 UE 工程上有几个老问题：

- **慢且重**：UE 5 引擎源码数万个文件，首次解析动辄十几分钟，`devenv.exe` 内存常到数 GB。
- **对 UE 宏不友好**：`UCLASS()` / `GENERATED_BODY()` / `UFUNCTION(...)` / `UPROPERTY(...)` 依赖 UHT 生成的 `.generated.h`，生成文件缺失或过期时，IntelliSense 会出现大量误报红线，跳转和补全失效。
- **不懂 UE 语义**：不知道 `Foo_Implementation` / `Foo_Validate` 与 `Foo` 的关系，不知道 Build.cs 模块依赖，不知道蓝图和资产，不懂反射说明符。
- **交互落后**：没有 Rider 那样统一的 Search Everywhere、Alt+Enter 上下文动作、Code Vision 等一致体验。

### 1.2 产品目标

| 目标 | 衡量方式 |
| --- | --- |
| UE 宏上的导航、补全、重构结果准确 | UE 语料回归集上的准确率（见 §10.4），目标 ≥ VA |
| 打开大型 UE 工程后秒级可用 | 冷启动“文件搜索可用” < 1 s，“符号可用（有缓存）” < 3 s |
| 编辑时零卡顿 | UI 线程单次占用 < 16 ms；补全 p95 < 50 ms |
| 内存可控 | VS 进程内增量 < 80 MB；后台引擎进程常驻 < 400 MB（典型 UE5 项目，见 §9.2） |
| 架构可长期演进 | 模块边界清晰、契约版本化、每个功能可独立开关与测试 |

### 1.3 非目标（至少第一阶段）

- 不实现完整 C++ 编译器前端，不追求模板元编程、SFINAE、概念约束层面的 100% 精确（必要时委托 Clang，见 ADR-02）。
- 不替代编译器诊断。错误以编译结果为准，我们的红线只做“快速、高置信度”的提示。
- 不做 C# / 其他语言的通用支持（HLSL/USF 例外，UE 刚需）。

---

## 2. 现有代码：功能参考与重构依据

当前仓库（`main`，约 1.1 万行）是一个 Rider 风格导航功能的原型。**本设计以第 5 章的目标架构为准，现有代码只作为功能清单和行为参考，不作为架构约束**：结构不符合目标架构的部分直接重写，只有在算法和接口都已经符合目标设计时才保留（例如模糊匹配打分、Build.cs 解析这类纯逻辑），并且保留的代码也要搬进新的模块边界。

### 2.1 现有功能清单与处置

| 位置 | 已有功能（作为需求参考） | 处置 |
| --- | --- | --- |
| `src/UEToVS.Core` | 模糊匹配 `FuzzyMatcher`、UE 命名 `UnrealNaming`、查询解析 `SearchQuery`、导航历史、双击 Shift 检测、键位预设、Build.cs 解析、RPC 契约 `IndexProtocol` | 纯逻辑部分审查后迁入新的 `Core`；`IndexProtocol` 废弃，按新的 `Contracts` 重新定义 |
| `src/UEToVS.Indexer` | 文件/资产扫描、启发式声明扫描器、快照式索引、二进制缓存、ripgrep 文本搜索、命名管道 JSON-RPC | **重写**为 `UEToVS.Engine.*`。进程外这一点保留；词法、解析、索引存储、缓存格式、RPC 序列化全部按 §6、§9 重新实现。现有扫描器里的 UE 关键字表和测试样例可作为参考数据 |
| `src/UEToVS`（VSIX） | Search Everywhere、最近文件/位置、导航历史、文件结构、Navigate To、相关项、书签、键位方案、模块依赖修复、Live Coding、资产搜索 | **重写**为 `UEToVS.Host` + 功能模块 + 组件库。功能和快捷键约定作为需求保留，代码不沿用（UI 用代码直接拼 WPF、功能间直接互相调用，不符合 §5.2 和 §8） |
| `ue/UEToVSLink` | localhost TCP：打开资产、Live Coding | 按 §6.9 的新协议重写为“编辑器桥” |
| `tests/*` | Core 与 Indexer 单元测试 | 用例作为行为规格迁移到新测试项目，补充语料回归与性能基准 |

### 2.2 现有实现不能直接沿用的原因

这些是重写的依据，也是新设计必须避免的问题：

1. **符号对象模型偏重**：`ScannedSymbol` 是 class，每个符号带多个 `string` 字段。25 万符号时还能接受，但加入“引用/出现位置”（数千万级）后无法用对象表示。→ 改为结构体数组 + 字符串池 + 32 位 ID（§9.3）。
2. **全量重建快照**：`IndexStore.Rebuild()` 每次变化都把全部文件的符号拷进新数组并重建字典，O(总符号数)。→ 改为“不可变基段 + 可变增量段”的分段索引（§6.4）。
3. **文本以 UTF-16 string 读入**：`ReadText` 把 UTF-8 文件转成 .NET string，内存翻倍且产生大对象。→ 词法分析直接跑在 UTF-8 `ReadOnlySpan<byte>` 上。
4. **缓存整文件反序列化到字典**：`IndexCache.Load` 把引擎缓存全部读进托管堆。→ 引擎索引改为只读 mmap，多进程共享页缓存。
5. **启发式扫描只识别声明**：没有函数体内的引用、作用域、类型信息，因此“转到定义 / 查找引用 / 成员补全”只能交给 VS IntelliSense（README“已知限制”已注明）。→ 新增容错解析器与语义层（§6）。
6. **包加载时机**：`UEToVSPackage` 在 `NoSolution` 与 `SolutionExists` 时都后台加载。→ 改为基于规则的 UIContext（检测到 `.uproject` 或 UE 生成的 sln 才激活）。
7. **RPC 用 JSON**：大结果集（引用列表、补全列表）序列化开销明显。→ 交互路径改用 MessagePack，并支持流式分页（§9.6）。
8. **UI 散落**：`SearchWindow`、`ListPopup`、`ResultList` 各自拼控件、各自处理主题。→ 统一的组件库（§8）。

---

## 3. 竞品调研

### 3.1 Visual Assist（截至 2026.5）

VA 的核心思路：**自研的快速容错 C++ 解析器负责日常的导航/补全/重构，Clang 负责代码检查与宏展开等需要精确语义的点功能**。它不依赖 VS IntelliSense，可在关闭 IntelliSense 时正常工作。

#### 导航

- Open File in Solution（Shift+Alt+O）、Find Symbol in Solution（Shift+Alt+S）
- Goto Implementation / Declaration（Alt+G），Goto Related（基类、派生类、成员、类型）
- Goto Member（列出当前类成员）、List Methods in File（Alt+M）
- Open Corresponding File（头文件 ↔ 源文件，Alt+O）
- Find References / Find References in File（区分读写、可按层级展开），引用高亮
- Navigation Bar（上下文 + 定义字段）、Hovering Class Browser
- VA Navigate Back / Forward、Move Scope（按作用域跳转）
- VA Outline、VA View（文件 / 符号 / MRU / 继承浏览）、VA Hashtags（注释里的 #tag 书签）
- Source Links（注释中的链接）

#### 重构

- Rename（UE 下支持 Core Redirects，2022.2 起）、Rename Files、Move Class（2023.6）
- Change Signature、Encapsulate Field、Extract Method、Introduce Variable
- Move Implementation to Source / Header、Move Selection to New File
- Move Method to Base Class（2024.9）、Promote Lambda to Method（2024.7）
- Modify Expression（2021.2）、Convert Pointer ↔ Instance、Simplify Instance Declaration
- Add/Remove Braces、Expand Macro

#### 代码生成

- Create Declaration / Create Implementation（含 UFUNCTION 的 `_Validate` 版本，2021.5；2026.5 修复了 UFUNCTION 前置宏导致签名重复的问题）
- Create from Usage、Implement Interface / Virtual Methods、Add Member、Add Similar Member
- Add Include（UE 项目默认使用引号，2023.6）、Add Forward Declaration、Document Method
- VA Snippets（带保留变量的代码片段）、Surround With

#### 编码辅助

- 增强补全列表与 Suggestions（缩写、首字母匹配）、Smart Suggestions
- 自动纠正（`.` ↔ `->`、大小写修复）、括号补全、Smart Select（扩大/缩小选区）
- 增强语法着色（类型、宏、变量、方法分色）、当前符号引用高亮
- 未知符号下划线（不依赖 IntelliSense 的快速红线）、注释拼写检查
- Quick Info 增强、**悬停宏展开**（2025.3；2026.1 加入 Clang 展开对照；2026.3 优化 UE 场景性能）
- 多重剪贴板（Paste Multiple）

#### 代码检查

- 基于 clang-tidy 的 Code Inspection（Clang 12→16 逐步升级，预设：VA Default / C++ Safety / Custom，2025.4 新增 60+ 安全类检查）
- **2026.5：全项目范围检查**，400+ 检查项（约 94 个精选 + 300 余个 clang-tidy），支持单条或批量修复
- Include Cleaner（misc-include-cleaner）

#### 调试辅助

- VA Step Filter、VA Memory View、Address Resolver、PDB Explorer

#### UE 专项（官方发布记录中可查证的）

- 正确解析 UCLASS / UFUNCTION / UPROPERTY 等宏；`*.generated.h` 不再解析（2021.2）
- 默认解析项目启用或引用的 UE 插件（2021.2）；UE5 官方支持（2022.2）
- 重命名时写入 Core Redirects（2022.2）
- `Create***Subobject` 识别（2024.1）
- HLSL / USF / USH 着色器支持（2022.3 beta，2023.4 默认开启），`.hlsli`（2026.3）
- UE 项目中粘贴代码不做自动缩进（2023.1）

#### AI（VA Intelligence）

- 2025.4 引入，本地 GPU 运行（Gemma，经 Ollama，需 ≥12 GB 显存），默认关闭、两步开启
- Symbol Explanation / Explain with AI（流式输出）
- **2026.3：Change Code with AI**（Shift+Alt+Q）：选中代码，用自然语言描述修改，内置 diff 审阅后接受或拒绝。附图中“VA 2026.5 特别版：AI 辅助 C++ 重构”指的就是这条能力在 2026.5 被并入 VA Agent
- **2026.5：VA Agent** 聊天窗口，可接 Claude Code、Codex 或本地 Gemma 4；Ask / Edits 两种模式，每次编辑可撤销（官方标注为 alpha）

#### 性能宣传

- 2024.1 解析速度“最高快 15 倍”，2024.2 再提升 20–50%
- 2024.3 某查找引用从 38 分钟降到约 5 分钟
- 2026.3 / 2026.4 分别改进 UE 宏展开和首次解析，无具体数字
- 官方没有公布内存数据

### 3.2 Rider for Unreal：值得借鉴的交互

Rider 在 UE 上的优势主要不在“能做什么”，而在“怎么做”。以下按可迁移程度列出：

| 交互 | 价值 | 本项目对应 |
| --- | --- | --- |
| **Search Everywhere**（双击 Shift） | 一个入口找一切 | 已有，继续强化（加入设置项、Git、CVar 等标签页） |
| **Alt+Enter 上下文动作** | 所有修复、生成、重构都在一个菜单，按光标位置排序 | 新增 Action 中枢（§7.4），统一接入 VS 灯泡 |
| **Alt+Insert Generate** | 构造函数、Getter/Setter、UFUNCTION 样板、RPC 三件套 | 新增 |
| **Code Vision** | 类/函数上方显示“N 个派生蓝图”“蓝图用法”“被 N 处引用” | 新增，基于 VS CodeLens 或自绘装饰 |
| **反射说明符补全** | `UPROPERTY(` 内补全 `EditAnywhere`、`meta=(ClampMin=...)` 并带文档 | 新增 |
| **蓝图感知** | 查找 C++ 符号在蓝图中的用法、派生蓝图列表 | 新增（VA 没有） |
| **UE 命名规范检查** | `A/U/F/E/T/I/b` 前缀规则，一键修复 | 新增 |
| **UHT 规则实时检查** | 不编译就能发现 UHT 会报的错（如 BlueprintCallable 参数类型不支持） | 新增 |
| **RPC 生成** | 声明 `UFUNCTION(Server, Reliable)` 后自动生成 `_Implementation` / `_Validate` | 新增（VA 只有 Create Implementation） |
| **Ini / .uproject / .uplugin 支持** | 配置键补全与跳转 | 新增 |
| **Inlay Hints** | 参数名、`auto` 推导类型、宏展开提示 | 新增，可开关 |
| **Postfix / Live Templates** | `expr.if`、`ucls` 等 | 新增，模板放在可共享的配置里 |
| **UE 日志窗口、PIE 控制、测试运行器** | 不切换到编辑器就能看日志、跑自动化测试 | 部分新增（经 UEToVSLink） |
| **一致的弹窗交互** | 所有列表都能输入过滤、Tab 切换、Ctrl+Enter 打开到窗口 | 由组件系统统一（§8） |
| **Inline Rename** | 在编辑器里直接改名，不弹对话框，冲突就地提示 | 新增 |
| **Find Usages 结果分组** | 按读/写、模块、文件、调用者分组，带预览 | 新增 |

### 3.3 结论

1. VA 的“自研解析器 + Clang 补位”是在 VS 内做到快且不依赖 IntelliSense 的被验证路线，本项目采用同样的分层（ADR-01、ADR-02）。
2. VA 的 UE 支持集中在“解析不出错”。蓝图、反射语义、网络复制、配置、资产等更深的 UE 语义，VA 基本没覆盖，这是本项目的差异化空间。
3. Rider 的交互统一性（一个入口、一个动作菜单、一致的列表行为）是体验的核心，必须靠组件系统和 Action 中枢在架构层面保证，而不是逐个功能各写一套。

---

## 4. 功能规划：三档清单

图例：
- **P0 必须开发**：对标 VA 的核心能力，缺了就无法替代 VA。
- **P1 优秀可选**：VA 或 Rider 有、价值高，但可以后置。
- **P2 VA 没有、可新增**：差异化能力。
- 状态：✅ 原型已有（行为作为参考，将在新架构上重写） / 🔶 原型部分已有 / ⬜ 未开始。
- 依赖：L0 = 只需文件/声明索引；L1 = 需要容错解析 + 语义层；L2 = 需要 Clang 精确语义；Link = 需要 UE 编辑器插件。

### 4.1 P0 必须开发

#### 导航

| 功能 | 对标 | 依赖 | 状态 |
| --- | --- | --- | --- |
| 打开文件 / 查找符号 / 查找类型（Search Everywhere） | VA Open File / Find Symbol；Rider Search Everywhere | L0 | ✅ |
| 转到定义 / 声明 / 实现（含 UE `_Implementation`） | VA Alt+G | L1 | ⬜（当前靠 VS） |
| 查找引用（读/写区分、分组、预览、文件内模式） | VA Find References | L1 | ⬜ |
| 头文件 ↔ 源文件、相关项（`Foo`/`_Implementation`/`_Validate`/`.generated.h`） | VA Open Corresponding；Rider Related | L0 | ✅ |
| 文件结构 / 成员列表（Alt+M） | VA List Methods；Rider File Structure | L0 | ✅ |
| 导航历史、最近文件、最近位置、编辑位置 | VA Navigate Back；Rider | — | ✅ |
| 类型层次（基类/派生类，含 UInterface） | VA Goto Related / Hovering Class Browser | L1 | ⬜ |
| 导航栏（当前作用域 + 跳转） | VA Navigation Bar | L1 | ⬜ |
| 引用高亮（光标所在符号） | VA | L1 | ⬜ |
| 书签 | Rider / VA Hashtags | — | ✅ |

#### 编码辅助

| 功能 | 对标 | 依赖 | 状态 |
| --- | --- | --- | --- |
| UE 感知 IntelliSense：成员补全（`.` `->` `::` 后，UE 宏下准确，见 §7.6） | VA 增强补全 | L1 | ⬜ |
| 首字母 / 驼峰 / 省略 UE 前缀的补全匹配 | VA Suggestions | L0 | 🔶（匹配器已有） |
| 增强语法着色（VA 风格配色，类型/函数/变量/宏/枚举值分色，引擎符号斜体，UE 反射符号单独着色） | VA | L1 | ⬜ |
| Quick Info（签名、注释、反射说明符） | VA | L1 | ⬜ |
| 悬停宏展开（含 UE 宏） | VA 2025.3+ | L1，精确模式 L2 | ⬜ |
| 参数信息（Signature Help） | VA | L1 | ⬜ |
| `.` ↔ `->` 自动纠正 | VA | L1 | ⬜ |
| Add Include（UE 引号风格、Public/Private 路径） + 自动补 Build.cs 依赖 | VA Add Include；Rider | L0/L1 | 🔶（依赖修复已有） |
| 未知符号快速红线（高置信度才报） | VA | L1 | ⬜ |

#### 重构与代码生成

| 功能 | 对标 | 依赖 | 状态 |
| --- | --- | --- | --- |
| Rename（含文件、Core Redirects 写入 ini） | VA Rename | L1 | ⬜ |
| Create Declaration / Create Implementation（UFUNCTION、`_Implementation`/`_Validate`） | VA | L1 | ⬜ |
| Change Signature（同步 `_Implementation`/`_Validate`） | VA | L1 | ⬜ |
| Extract Method / Introduce Variable | VA | L1 | ⬜ |
| Encapsulate Field（UPROPERTY 下生成 BlueprintGetter/Setter 可选） | VA | L1 | ⬜ |
| Move Implementation to Source / Header | VA | L1 | ⬜ |
| Implement Virtual Methods / Override（含 `Super::` 调用） | VA | L1 | ⬜ |
| Add Forward Declaration | VA | L1 | ⬜ |
| 代码片段（Snippet / Surround With，带 UE 内置模板） | VA Snippets；Rider Live Templates | — | ⬜ |

#### UE 专项

| 功能 | 对标 | 依赖 | 状态 |
| --- | --- | --- | --- |
| UE 宏正确解析（UCLASS / USTRUCT / UENUM / UINTERFACE / UFUNCTION / UPROPERTY / GENERATED_BODY / DECLARE_*_DELEGATE*） | VA | L1 | 🔶（声明级已有） |
| 不依赖 `.generated.h`（内置 GENERATED_BODY 语义模型） | VA 2021.2 | L1 | 🔶 |
| 只解析启用的插件与引用模块 | VA 2021.2 | L0 | 🔶 |
| `CreateDefaultSubobject<T>` 等模板工厂的类型推导 | VA 2024.1 | L1 | ⬜ |
| 反射说明符补全与文档（UPROPERTY/UFUNCTION/UCLASS/USTRUCT/meta） | Rider | L0 + 知识库 | ⬜ |
| Build.cs 模块依赖检查与修复 | Rider | L0 | ✅ |
| HLSL / USF / USH 着色、导航、补全 | VA | 独立轻量解析 | ⬜ |
| 生成工程文件、Live Coding | — | Link | ✅ |

#### 基础设施（不面向用户，但 P0）

| 内容 | 说明 |
| --- | --- |
| UE 工程模型 | 从 `.uproject` / `.uplugin` / `*.Build.cs` / `*.Target.cs` / UBT 输出建立模块、include 路径、宏定义 |
| 增量索引 + 持久化 | 分段索引、引擎索引跨项目共享（§6.4） |
| 诊断面板 | 内存、索引进度、各请求延迟、日志一键导出 |
| 自动接管 VS 自带 C++ 感知 | 启用后自动关闭 VS C++ IntelliSense（含备份与一键恢复），由 UEToVS 提供全部感知；检测 VA 冲突（§7.5） |

### 4.2 P1 优秀可选

| 功能 | 来源 | 依赖 |
| --- | --- | --- |
| Alt+Enter 上下文动作中枢（所有修复/生成/重构的统一入口） | Rider | 架构能力 |
| Generate 菜单（构造函数、Getter/Setter、GetLifetimeReplicatedProps、RPC 三件套） | Rider | L1 |
| Inline Rename（就地改名、冲突就地提示） | Rider | L1 |
| Code Vision（引用数、派生类数、蓝图用法数） | Rider | L1 / Link |
| Inlay Hints（参数名、`auto` 类型、宏结果） | Rider | L1 |
| Postfix 模板（`expr.if`、`expr.valid` → `IsValid(expr)`） | Rider | L1 |
| 调用层次（Call Hierarchy） | Rider | L1 |
| Smart Select（扩大/缩小选区） | VA | L1 |
| Move Class / Move Method to Base / Promote Lambda / Modify Expression | VA | L1 |
| Rename Files（同步 include、Build.cs、redirect） | VA | L1 |
| 代码检查（clang-tidy，含全项目批量检查与修复） | VA 2026.5 | L2 |
| Include Cleaner | VA | L2 |
| 多重剪贴板 | VA | — |
| 注释拼写检查 | VA | L0 |
| VA Hashtags 式注释标签 | VA | L0 |
| UE 命名规范检查与修复 | Rider | L1 |
| Ini / .uproject / .uplugin 补全、跳转、校验 | Rider | 独立解析 |
| UE 日志窗口（分类过滤、点击跳源码） | Rider | Link |
| UE 自动化测试运行器 | Rider | Link |
| Natvis 增强（UE 容器、FName、FText、UObject 名称） | Rider | 调试器扩展 |
| 调试辅助：Step Filter（跳过 `TArray::operator[]` 等） | VA | 调试器扩展 |
| AI：解释符号、选区 AI 修改（带 diff 审阅） | VA Intelligence | AI 适配层 |

### 4.3 P2 VA 没有、可新增

这些是本项目的差异化点，全部围绕“UE 的语义不止 C++”。

| 功能 | 说明 | 依赖 |
| --- | --- | --- |
| **蓝图用法与派生蓝图** | 在 C++ 类/函数/属性上看到哪些蓝图继承、调用、读写了它；可跳到编辑器打开对应蓝图节点。离线时解析 `.uasset` 导入表与名称表；编辑器在线时走 AssetRegistry 查询，结果更准确 | L0 + Link |
| **UHT 规则实时检查** | 不编译就报出 UHT 会拒绝的写法：BlueprintCallable 使用不支持的参数类型、UPROPERTY 用在非反射类型、缺少 GENERATED_BODY、`BlueprintNativeEvent` 缺 `_Implementation` 等 | L1 + 知识库 |
| **网络复制检查** | `UPROPERTY(Replicated)` 没有对应 `DOREPLIFETIME`；`ReplicatedUsing=OnRep_X` 但 `OnRep_X` 不存在或不是 UFUNCTION；`Server` RPC 缺 `WithValidation` 时的 `_Validate` 不一致 | L1 |
| **委托绑定导航** | 从 `AddDynamic(this, &AFoo::OnHit)`、`BindUFunction(this, "OnHit")`、`FTimerDelegate` 跳到被绑定函数，并在函数上显示“被哪些委托绑定”；字符串形式的函数名也纳入 Rename | L1 |
| **字符串引用感知** | `FName("Socket")`、`TEXT("/Game/...")`、`ConstructorHelpers::FObjectFinder` 的资产路径补全、校验（路径不存在即提示）、跳转到资产 | L0 + 资产索引 |
| **GameplayTags 支持** | 从 `DefaultGameplayTags.ini` 和 Tag 表读取标签树，`FGameplayTag::RequestGameplayTag("A.B")` 等处补全、校验、查找用法 | 独立解析 |
| **配置联动** | `UCLASS(config=Game)` + `UPROPERTY(config)` 与 ini 键双向跳转；CVar（`TAutoConsoleVariable`）搜索与跳转 | L1 |
| **日志分类导航** | `DECLARE_LOG_CATEGORY_EXTERN` 与使用处互跳，UE 日志窗口中点日志行跳到 `UE_LOG` 源码 | L0 + Link |
| **模块依赖图** | 可视化 Build.cs 依赖、检测循环依赖、Public/Private 误用、未使用依赖 | L0 |
| **Include 成本分析** | 结合 Clang `-ftime-trace` 或 UBT 编译时间，标出最贵的头文件和传染路径，给出前向声明建议 | L2 |
| **Unity Build 隐患检查** | 找出只在 Unity Build 下能编过的文件（依赖同批 cpp 的 include），在非 Unity 模式提前预警 | L1 |
| **引擎升级助手** | 读取 `UE_DEPRECATED(5.x, "...")` 信息，列出项目里所有已弃用 API 用法，能机械替换的给一键修复 | L1 |
| **生成代码预览** | 在 `GENERATED_BODY()` 上悬停或跳转时直接看到 UHT 生成的内容（有生成文件时读取，没有时由知识库合成） | L0 |
| **Live Coding 错误映射** | Live Coding / UBT 输出的错误直接变成编辑器内诊断，而不是只在输出窗口 | Link |
| **MCP 服务：把语义索引开放给 AI 代理** | 以本地 MCP 服务暴露“查符号 / 查引用 / 查派生蓝图 / 查模块依赖 / 安全重命名”等工具，供 Claude Code、Codex 等代理调用。AI 修改代码时用的是 UE 感知的索引而不是纯文本搜索 | L1 |
| **AI 修改的确定性校验** | AI 产出的改动在应用前由本地解析器检查（符号是否存在、签名是否匹配、UHT 规则），不通过的标红，用户审阅 diff 后再应用 | L1 + AI |
| **扩展自身的性能面板**（提前到 M0） | 实时显示本扩展的内存、CPU、索引各阶段占用、每类请求延迟，超预算高亮，可一键生成诊断包（§9.8） | 基础设施 |

---

## 5. 总体架构

### 5.1 进程模型

```
┌──────────────────────── devenv.exe（VS 进程）─────────────────────────┐
│  UEToVS.Host（VSIX, net48，进程内，保持“薄”）                            │
│  ┌──────────────┐ ┌───────────────┐ ┌──────────────┐ ┌──────────────┐ │
│  │ Shell        │ │ Editor 适配器  │ │ 功能模块       │ │ UI 组件库      │ │
│  │ 包/命令/选项   │ │ MEF：着色/补全 │ │ 导航/搜索/重构  │ │ UEToVS.UI    │ │
│  │ 键位/诊断     │ │ QuickInfo/灯泡 │ │ UE/检查/AI     │ │              │ │
│  └──────┬───────┘ └──────┬────────┘ └──────┬───────┘ └──────────────┘ │
│         └────────────────┴────── EngineClient（契约 + 连接管理）────────┘
└───────────────────────────────────────┬──────────────────────────────┘
                     命名管道（当前用户 ACL），MessagePack-RPC，可取消、可流式
┌───────────────────────────────────────▼──────────────────────────────┐
│  UEToVS.Engine（.NET 8 独立进程，每个解决方案一个）                        │
│  Workspace │ VFS(磁盘+未保存缓冲区) │ 词法/预处理 │ 容错解析 │ 语义模型        │
│  分段索引(符号/引用/文件/资产) │ 查询服务 │ 重构引擎 │ 检查引擎 │ UE 知识库     │
└──────┬──────────────────────────┬─────────────────────────┬──────────┘
       │ 按需启动、可选              │ 只读 mmap                 │ localhost TCP
┌──────▼────────────┐   ┌─────────▼────────────────┐   ┌──────▼─────────────┐
│ Clang Worker       │   │ 索引（mmap）                 │   │ UE Editor          │
│ clangd/clang-tidy  │   │ <解决方案目录>\.vs\UEToVS\  │   │ + UEToVSLink 插件    │
│ 精确语义、检查、宏展开  │   │   Index\  Logs\            │   │ 资产/蓝图/日志/LC     │
└────────────────────┘   │ 引擎段只读、按 BuildId 校验     │   └────────────────────┘
                         └──────────────────────────┘
       可选：AI Provider 适配（本地模型 / Claude Code / Codex）、MCP 服务（对外暴露索引）
```

设计要点：

- **VS 进程只做 UI 与编辑器集成**。所有解析、索引、搜索、重构计算都在 Engine 进程，崩溃不影响 VS，内存不计入 `devenv.exe`。现有原型的 `UEToVS.Indexer` 也是进程外，但 Engine 按本设计重新实现。
- **每个解决方案一个 Engine 进程**，引擎部分索引通过只读 mmap 在进程间共享（同一引擎版本只存一份、只占一份物理内存）。
- **Clang Worker 是可选的**，默认只在需要精确语义的功能（代码检查、精确宏展开、复杂模板场景的二次确认）时按需启动，空闲超时自动退出。
- **UEToVSLink** 从“打开资产 + Live Coding”扩展为编辑器桥，提供蓝图、资产注册表、日志、测试等编辑器内才有的信息。

### 5.2 分层与依赖规则

```
L5  功能模块（Features.*）          只依赖 L4 契约与 L3 抽象，互不直接引用
L4  宿主服务（Host.Services）        命令、Action 中枢、导航服务、设置、诊断
L3  编辑器适配（Host.Editor）        MEF 组件，把 VS 编辑器事件转为领域请求
L2  UI 组件库（UEToVS.UI）           不依赖任何功能模块
L1  引擎客户端（Engine.Client）       连接、重连、请求优先级、取消
L0  契约与通用库（UEToVS.Core / Contracts）  netstandard2.0，无 VS、无 IO 依赖
```

强制规则（通过架构测试，例如 NetArchTest，在 CI 上校验）：

1. 依赖只能从上往下；同层功能模块之间禁止互相引用，协作通过 Action 中枢或事件总线。
2. `UEToVS.Core` / `Contracts` 不得引用 VS SDK、WPF、文件系统具体实现。
3. Engine 不得引用任何 VS 程序集（保证可在命令行、CI、MCP 服务中独立运行）。
4. UI 组件库不得引用功能模块；功能模块只能通过组件库搭界面，不得直接 `new` 原生 WPF 控件拼复杂界面（简单布局容器除外）。

### 5.3 解决方案与程序集划分

| 程序集 | 目标框架 | 职责 | 与现有代码的关系 |
| --- | --- | --- | --- |
| `UEToVS.Contracts` | netstandard2.0 | RPC 接口、DTO、错误码、版本号 | 新建，不沿用 `IndexProtocol` |
| `UEToVS.Core` | netstandard2.0 | 匹配、查询、导航历史、键位、Build.cs 模型、通用算法 | 新建；审查后迁入现有纯逻辑 |
| `UEToVS.Engine` | net8.0 | 引擎进程入口、RPC 服务、调度 | 重写（替代 `Indexer`） |
| `UEToVS.Engine.Syntax` | net8.0 | UTF-8 词法、预处理、容错解析器、语法树 | 重写，参考现有扫描器的 UE 关键字表 |
| `UEToVS.Engine.Semantics` | net8.0 | 作用域、名称查找、类型、继承、UE 反射模型 | 新增 |
| `UEToVS.Engine.Index` | net8.0 | 分段索引、mmap 存储、增量合并 | `Index/*` 重写 |
| `UEToVS.Engine.Unreal` | net8.0 | 工程模型、UE 知识库、资产/ini/GameplayTags 解析 | 重写，参考 `ProjectLayout` 的目录约定 |
| `UEToVS.Engine.Refactoring` | net8.0 | 重构与代码生成，产出 `WorkspaceEdit` | 新增 |
| `UEToVS.Engine.Inspections` | net8.0 | 自有规则 + clang-tidy 结果归一 | 新增 |
| `UEToVS.Engine.Clang` | net8.0 | Clang Worker 管理与协议适配 | 新增 |
| `UEToVS.UI` | net48（WPF） | 组件库、设计令牌、主题 | 新建，替代 `src/UEToVS/UI` |
| `UEToVS.Host` | net48 | VSIX：Shell、编辑器适配、宿主服务 | 重写（替代 `src/UEToVS`） |
| `UEToVS.Features.*` | net48 | 每个功能域一个程序集（或一个程序集内按命名空间严格隔离） | 重写，功能清单来自现有 `Search/Navigation/Unreal/...` |
| `UEToVS.Mcp` | net8.0 | MCP 服务，复用 Engine 查询 | 新增 |
| `ue/UEToVSLink` | C++ | UE 编辑器插件 | 按新协议重写 |

> 程序集数量与加载成本需要平衡。建议 VS 侧最终合并为 `UEToVS.Host` + `UEToVS.UI` 两个程序集，功能模块以命名空间 + 架构测试隔离；Engine 侧按上表拆分，因为它在独立进程，加载成本不敏感，而拆分有利于测试和复用。

---

## 6. 核心引擎设计（语义层）

### 6.1 工程模型（Workspace）

输入来源，按可靠性从高到低：

1. **UBT 导出的编译数据库**：`UnrealBuildTool -mode=GenerateClangDatabase`（或解析 UBT 生成的 `.vcxproj` / 响应文件），得到每个模块的 include 路径、宏定义、编译选项。最准确，但需要运行 UBT（数秒到数十秒），结果缓存，Build.cs 变化后后台刷新。
2. **自行解析 Build.cs / Target.cs**：现有 `BuildCsFile` 已能解析依赖列表。作为 UBT 不可用时的回退，按 UE 约定推导 `Public/` `Classes/` `Private/` include 路径和 `<MODULE>_API` 宏。
3. **约定推断**：只有文件树时，用目录结构估算模块边界（现有 `ProjectLayout` 的做法）。

模型实体：`Project`（.uproject）→ `Plugin`（.uplugin，启用状态）→ `Module`（Build.cs，类型、依赖、include 路径、宏）→ `SourceFile`（所属模块、Public/Private、是否参与编译）。

### 6.2 虚拟文件系统（VFS）

- 统一的文件读取入口：磁盘内容 + 编辑器未保存缓冲区覆盖（VS 侧对打开文档按 debounce 推送增量文本变化）。
- 文件 ID 为 32 位整数，路径只在字符串池中存一份。
- 文件变化来源：FileSystemWatcher（已有）+ VS 文档事件 + Git 分支切换检测（大量文件同时变化时进入批量模式，避免逐个触发重建）。

### 6.3 解析管线

```
UTF-8 字节 ──▶ 词法分析（零分配，Span）──▶ 轻量预处理 ──▶ 容错解析 ──▶ 文件摘要（FileSummary）
                                     │                          │
                             UE 宏知识库（宏 → 语义）             └─▶ 引用出现表（Occurrences）
```

**词法分析**：在 `ReadOnlySpan<byte>` 上运行，token 只记录（类型、起始偏移、长度），不创建字符串；标识符通过哈希查字符串池得到名称 ID。

**轻量预处理**：不做完整的 include 展开（这是 VA 快的关键）。处理：
- `#if / #ifdef` 分支：用工程模型中的宏定义求值；求不出的分支两边都解析并标记“条件分支”。
- 对象/函数宏：**只展开“影响声明结构”的宏**，由宏知识库决定，例如 `GENERATED_BODY()` 视为“插入一组已知成员”，`UCLASS(...)` 视为“附加反射元数据到下一个类声明”，`DECLARE_DYNAMIC_MULTICAST_DELEGATE_OneParam(FOnX, T, P)` 视为“声明类型 FOnX”，`<MODULE>_API` 视为空。
- 未知宏：项目内定义的宏，按定义体做一层展开尝试；失败时按“像函数调用”处理，不让解析崩溃。

**容错解析**：手写递归下降，目标是得到**声明结构 + 函数体内的名称引用**，而不是完整 AST。
- 产出声明：命名空间、类/结构体/联合体/枚举（基类列表、模板参数）、成员函数/字段（类型文本、参数、限定符、UE 反射元数据）、自由函数、typedef/using、宏定义。
- 函数体只做“浅解析”：识别标识符引用及其前缀上下文（`a.b`、`a->b`、`A::b`、调用、取地址、赋值左侧 = 写），供引用索引和补全上下文使用。
- 错误恢复：以 `;` `}` 和顶层关键字同步，单个文件解析错误不影响其他声明。

**UE 宏知识库**（数据驱动，放在可热更新的 JSON/YAML 里，按引擎版本分组）：
- 反射宏：类别、允许的说明符、每个说明符的参数类型和文档、UHT 规则（用于 P2 检查）。
- `GENERATED_BODY` / `GENERATED_UCLASS_BODY` 注入的成员（`StaticClass()`、`Super`、`ThisClass`、构造函数签名等）。
- 委托声明宏族（`DECLARE_DELEGATE_*`、`DECLARE_DYNAMIC_MULTICAST_*`、`DECLARE_EVENT_*`）的参数模式。
- 常见工厂模板的返回类型规则（`CreateDefaultSubobject<T>`、`NewObject<T>`、`Cast<T>`、`GetWorld()->SpawnActor<T>`）。
- 这部分是相对 VS IntelliSense 的核心优势：**不依赖 `.generated.h` 是否存在、是否最新**。

### 6.4 索引

#### 存放位置（用户决定）

索引和日志都放在 VS 解决方案目录下的 `.vs` 文件夹里的独立目录，跟随项目，不进版本库（`.vs` 本来就被 UE/VS 的 `.gitignore` 忽略）：

```
<解决方案目录>\.vs\UEToVS\
  Index\            分段索引（engine-<BuildId>.seg、project-*.seg、manifest.json）
  Logs\             日志（见 §10.5）
  Cache\            UBT 编译数据库、知识库缓存等
  settings.local.json   本机、本解决方案的临时状态（导航历史、弹窗尺寸等）
```

取舍：引擎段不再跨项目共享，同一引擎的多个项目各存一份（磁盘多占一份，首次索引各做一次）。好处是删除 `.vs` 即可彻底清理，问题排查时索引、日志都在项目里。后续如有需要，可加一个“从其他项目复制引擎段”的选项（按 BuildId 校验后直接拷贝，免去重新索引）。

#### 索引内容

| 索引 | 键 | 值 | 用途 |
| --- | --- | --- | --- |
| 文件表 | FileId | 路径 ID、模块 ID、来源、mtime、大小、内容哈希 | 文件搜索、增量判断 |
| 符号表 | SymbolId | 名称 ID、容器 SymbolId、种类、UE 标志、说明符 ID、FileId、位置、签名 ID | 搜索、跳转、补全 |
| 名称倒排 | NameId | SymbolId 列表 | 按名查找 |
| 搜索加速 | 名称首字母/驼峰缩写、trigram | NameId 列表 | 模糊搜索剪枝（替代当前的线性扫描） |
| 引用出现表 | NameId | (FileId, 偏移, 角色) 列表，delta + varint 压缩 | 查找引用、重命名候选 |
| 继承图 | SymbolId | 基类/派生类 SymbolId | 类型层次、Implement Virtual |
| include 图 | FileId | include 的 FileId 列表 | 依赖修复、include 成本分析、受影响文件计算 |
| 资产表 | AssetId | 对象路径、类名、父类（蓝图）、引用的原生类/函数名 | 资产搜索、蓝图用法 |

#### 分段存储（LSM 思路）

```
引擎基段（只读，按引擎 BuildId，所有项目/VS 实例共享，mmap）
  + 插件/项目基段（只读，mmap，后台合并生成）
  + 增量段（内存中，最近修改的文件，小）
  + 墓碑表（被修改/删除文件的 FileId，查询时过滤基段中的旧数据）
```

- 查询 = 各段结果合并（基段过滤墓碑）。增量段超过阈值（例如 2000 个文件或 32 MB）时，后台合并成新的项目基段，原子切换。
- 这替代现有“每次变化全量 `Rebuild()`”的做法，单文件修改的索引更新成本从 O(全部符号) 降到 O(该文件符号)。
- 文件格式：自定义二进制，头部含格式版本 + 引擎 BuildId + 知识库版本；任一版本不匹配则重建。所有数组按页对齐，可直接 mmap 成 `ReadOnlySpan<T>` 读取（.NET 8 `MemoryMappedViewAccessor` + `unsafe` 指针）。
- 引擎基段生成后不再修改，可以考虑**预构建分发**：同一 UE 版本的官方引擎索引由一台机器生成，团队成员直接复用（企业场景下很有价值，P1）。

### 6.5 语义模型

在索引之上提供按需计算的语义查询，**只为当前请求涉及的少量文件和符号做工作**：

- **作用域与名称查找**：基于声明结构建立作用域链（命名空间 → 类 → 函数 → 块），支持 `using namespace`、类继承中的成员查找、`Super::` / `ThisClass::`。
- **类型推导（轻量）**：变量声明类型、成员访问链、`auto` 的常见初始化形式（`= NewObject<T>()`、`= Cast<T>(x)`、`= GetComponentByClass<T>()`、`TObjectPtr<T>` / `TWeakObjectPtr<T>` / `TSubclassOf<T>` 的解包）、常见容器迭代元素类型（`TArray<T>`、`TMap<K,V>` 的 `for (auto& [K, V] : Map)`）。
- **模板**：识别模板参数和简单实例化，不做 SFINAE/重载决议的完整实现；遇到无法确定的情况，返回候选列表并标注“不确定”，由 UI 显示多个目标或交给 Clang Worker 二次确认。
- **查找引用**：两阶段。先用引用出现表按名称取候选（毫秒级），再对候选所在位置做语义过滤（同名不同符号排除），结果流式返回；用户可选择“快速模式”（只按名称+容器过滤）或“精确模式”（必要时调用 Clang 确认）。
- **置信度**：所有语义结果带置信度（确定 / 可能 / 仅文本匹配），重构在“可能”项上要求用户确认，红线只在“确定”时显示。

### 6.6 重构引擎

- 输入：重构种类 + 位置 + 参数；输出：`WorkspaceEdit`（多文件文本修改 + 文件重命名 + ini 修改），**引擎不直接写文件**。
- 宿主负责：展示预览（组件库的 Diff 预览）、应用到 VS 缓冲区（保证进入 VS 撤销栈，一次重构一次撤销）、对未打开文件直接写盘并通知引擎。
- UE 专项规则：重命名 UCLASS/USTRUCT/UPROPERTY/UFUNCTION 时可选写入 `DefaultEngine.ini` 的 `[CoreRedirects]`；改函数签名时同步 `_Implementation` / `_Validate`；重命名文件时同步 `#include` 和 `.generated.h` 的 include 名。
- 每个重构有“前置检查”（冲突、只读文件、引擎源码不可改、反射规则），检查结果在预览中显示。

### 6.7 检查引擎

- 自有规则（L1，毫秒级，随编辑运行）：UE 命名规范、UHT 规则、网络复制一致性、缺失 `_Implementation`、未在 Build.cs 声明的模块依赖（原型已有此功能，在此重写）。
- clang-tidy 规则（L2，后台/手动运行）：通过 Clang Worker 按文件执行，结果缓存（按文件内容哈希 + 编译参数哈希），全项目检查时并发度受控且可暂停。
- 统一诊断模型：规则 ID、严重度、位置、消息、可选修复（`WorkspaceEdit`），由宿主统一显示为波浪线、错误列表、Alt+Enter 修复项。
- 规则配置文件放在仓库（`.uetovs/inspections.json`），团队共享。

### 6.8 Clang Worker（可选精确层）

- 使用 clangd（LSP）或 libclang，以 UBT 导出的编译数据库驱动。
- 只用于：clang-tidy 检查、精确宏展开（与 VA 2026.1 类似的双栏对照）、重载/模板场景的定义确认、Include Cleaner、include 成本分析。
- 资源治理：按需启动、空闲 5 分钟退出；设置内存上限（进程 Job Object 限制）；禁止它建立全工程后台索引（我们的索引已经覆盖），只对请求涉及的文件构建 preamble。

### 6.9 UE 编辑器桥（UEToVSLink 扩展）

现有：打开资产、触发 Live Coding、端口写入 `Saved/UEToVSLink/port.txt`。

扩展的协议（仍为 localhost，JSON 行协议，带版本号与能力协商）：

| 能力 | 说明 |
| --- | --- |
| `assets.query` | 通过 AssetRegistry 查询蓝图父类、引用的原生类/函数/属性 |
| `blueprint.usages` | 给定 UFUNCTION / UPROPERTY，返回使用它的蓝图图表节点 |
| `blueprint.open` | 在编辑器中打开蓝图并聚焦到节点 |
| `log.subscribe` | 推送 UE 日志（分类、级别、源位置） |
| `livecoding.*` | 触发编译、回传错误到 VS 诊断 |
| `tests.*` | 列出并运行自动化测试 |
| `pie.*` | 开始/暂停/停止 PIE（P1） |

离线回退：编辑器未运行时，Engine 直接读取 `.uasset` 的 package summary、名称表和导入表，得到父类与引用的原生名称（精度较低，标注为“离线结果”）。

---

## 7. VS 宿主层设计

### 7.1 加载策略

- 使用基于规则的 UIContext：解决方案目录或其上级存在 `.uproject`，或 sln 由 UBT 生成，才激活 UEToVS。非 UE 解决方案下只注册命令，不启动 Engine，不占内存。（当前 `ProvideAutoLoad` 对 `NoSolution` 也会加载，需要改。）
- `AsyncPackage` 后台加载；所有 MEF 组件按需实例化，构造函数里不做 IO、不连 Engine。
- Engine 进程在 UE 解决方案打开后立即启动，先提供文件搜索，再逐步可用。

### 7.2 编辑器适配器（MEF）

| VS 扩展点 | 用途 |
| --- | --- |
| `IClassifierProvider` / `ITaggerProvider<IClassificationTag>` | 增强着色（只对可见区域请求，结果按版本缓存） |
| `IAsyncCompletionSourceProvider` | 补全（与 VS 内置补全合并或接管，见 §7.5） |
| `IAsyncQuickInfoSourceProvider` | Quick Info、宏展开、反射说明符文档 |
| `ISignatureHelpSourceProvider` | 参数信息 |
| `ISuggestedActionsSourceProvider` | 灯泡 / Alt+Enter |
| `IViewTaggerProvider<ITextMarkerTag>` | 引用高亮、波浪线 |
| `IWpfTextViewMarginProvider` | 导航栏、书签栏（已有书签字形） |
| CodeLens / 自绘 Adornment | Code Vision、Inlay Hints |
| `IVsRunningDocTableEvents` / `ITextBuffer.Changed` | 推送编辑到 Engine（debounce 150 ms，增量 diff） |

原则：所有适配器只做“取请求参数 → 调 Engine → 把结果交给 VS”，不含领域逻辑。

### 7.3 请求调度

- 请求分级：**交互级**（补全、Quick Info、跳转，用户在等）> **可见级**（着色、引用高亮、Code Vision）> **后台级**（检查、索引合并）。
- Engine 侧使用三个优先级队列；交互级请求可抢占后台级的 CPU（后台任务分块执行、每块检查让出）。
- 每个请求必带 `CancellationToken`；文档版本变化时自动取消旧请求。
- VS 侧使用 `JoinableTaskFactory`，严禁在 UI 线程上同步等待 Engine。

### 7.4 Action 中枢（统一的动作模型）

借鉴 Rider 的 Alt+Enter 与 Find Action，所有可执行能力都注册为 `IAction`：

```
IAction
  Id            "uetovs.refactor.rename"
  Title / Icon / Keywords
  Category      导航 / 重构 / 生成 / 修复 / UE / 设置
  IsAvailable(context)   // 根据光标、选区、文件类型判断，必须廉价
  ExecuteAsync(context)
  Priority(context)      // 上下文相关排序
```

- 一个能力注册一次，就同时出现在：Alt+Enter 菜单、VS 灯泡、Find Action（取代原型的 `ActionCatalog`）、右键菜单、命令（可绑定键位）。
- 功能模块之间不直接调用，而是通过 Action ID 调度，满足 §5.2 的隔离规则。

### 7.5 接管 VS 自带 C++ 感知

**决定：插件启用后自动关闭 VS 自带的 C++ IntelliSense，由 UEToVS 全面接管 C++ 感知**（补全、Quick Info、参数信息、语义着色、波浪线、跳转、查找引用）。这与 VA 推荐的用法一致，也是降低 `devenv.exe` 内存和 CPU 的最大来源。

#### 关闭哪些开关

通过 `DTE.Properties["TextEditor", "C/C++ Specific"]`（或对应的设置存储）修改，VS 2022 / 2026 名称一致，实际键名在实现时按版本核对：

| VS 设置（工具 > 选项 > 文本编辑器 > C/C++ > 高级） | 接管后 | 原因 |
| --- | --- | --- |
| 禁用 IntelliSense（Disable IntelliSense） | True | 关闭 EDG 前端：补全、Quick Info、波浪线、语义着色全部由 UEToVS 提供 |
| 禁用自动快速信息 / 成员列表 / 错误波浪线 / 语义着色 | True | 部分 VS 版本在“禁用 IntelliSense”外仍单独生效，一并关闭，避免双重弹窗 |
| 禁用数据库（Disable Database） | 可选，默认 True | 浏览数据库（.vc.db）在 UE 工程上占用大量磁盘与内存；UEToVS 自有索引覆盖其功能。保留一个选项给依赖 VS“类视图”的用户 |
| 禁用外部依赖项文件夹 | True | 避免 VS 枚举数万引擎头文件 |
| 自动 include 补全、#include 列表 | 由 UEToVS 提供 | |

#### 生命周期

1. **首次启用**：检测到 UE 解决方案时，在 InfoBar（组件库 `Banner`）中说明“UEToVS 将接管 C++ 感知，并关闭 VS 自带 IntelliSense”，默认执行，可选“暂不”。
2. **备份**：修改前把所有涉及的原始值写入 `%LocalAppData%\UEToVS\intellisense-backup.json`，与现有键位备份（`keymap-backup.json`）同一套机制。
3. **作用范围**：VS 的这些开关是全局的，而不是按解决方案。提供两种策略：
   - *全局接管*（默认）：插件启用期间始终关闭 VS C++ 感知。
   - *仅 UE 解决方案*：打开 UE 解决方案时关闭，关闭解决方案或打开非 UE 解决方案时自动恢复。
4. **恢复**：命令“恢复 Visual Studio 自带 C++ 感知”一键还原备份；在扩展管理器里禁用 UEToVS 前也会提示先执行恢复。由于 VSIX 卸载时无法运行代码，下次 VS 启动若检测到 UEToVS 已不在但备份仍存在，无法自动恢复，因此在设置页和 README 中提供手动恢复说明。
5. **兜底**：Engine 不可用（崩溃重启中、首次索引未完成）时，UEToVS 自身的感知会降级为“仅词法”（关键字着色、基于文件内符号的补全），并在状态栏提示；不自动重新打开 VS IntelliSense，避免反复切换引发 VS 重新解析。

#### 与 VA / Copilot 共存

- 检测到 VA 已安装且启用时，提示二选一（两套感知同时开会相互抢占补全与快捷键）；用户选择 UEToVS 时不改动 VA 设置，只提示用户在 VA 中关闭。
- GitHub Copilot 等 AI 补全使用独立的灰色内联建议通道，与我们的补全列表不冲突，保留。

### 7.6 UE 感知 IntelliSense

接管后，UEToVS 需要提供完整的 C++ 感知能力，并在 UE 场景下比 VS 自带的更准。全部能力依赖 L1（容错解析 + 语义层 + UE 宏知识库），不依赖 `.generated.h`。

| 能力 | 行为 | UE 专项 |
| --- | --- | --- |
| 成员补全（`.` `->` `::`） | 按语义类型列出成员；VA 风格匹配（首字母缩写、驼峰、子串），近期使用与上下文类型匹配优先 | `GENERATED_BODY` 注入成员（`StaticClass`、`Super`、`ThisClass`）；`TObjectPtr<T>`、`TWeakObjectPtr<T>`、`TSubclassOf<T>` 自动解包；`Cast<T>`、`CreateDefaultSubobject<T>`、`NewObject<T>` 推导结果类型 |
| 全局/作用域补全 | 输入标识符时列出可见符号 | 省略 UE 前缀匹配（输入 `Actor` 命中 `AActor`，沿用现有 `UnrealNaming`） |
| 反射说明符补全 | 在 `UPROPERTY(` `UFUNCTION(` `UCLASS(` `USTRUCT(` `UENUM(` `meta=(` 内补全说明符，带文档、互斥/依赖提示 | 来自 UE 宏知识库，按引擎版本 |
| `#include` 补全 | 按模块 Public/Classes 路径补全，引号风格 | 选中不在 Build.cs 依赖中的头文件时，同步提示添加依赖（原型已有依赖修复，在此重写） |
| 字符串上下文补全 | 资产路径、GameplayTag、`BindUFunction` 函数名、ini 键 | P2 能力在补全中的入口 |
| Quick Info | 签名、所在模块与文件、注释（含 `/** */` 文档）、反射说明符摘要 | UPROPERTY/UFUNCTION 显示“蓝图可见/可编辑/复制”等标签；宏上悬停显示展开 |
| 参数信息 | 重载列表、当前参数高亮、默认值 | 委托 `Broadcast`/`Execute` 的参数取自 `DECLARE_*_DELEGATE_*` 声明 |
| 语义着色 | 见 §7.7 | 反射符号可单独着色 |
| 波浪线 | 只报高置信度问题：未知类型/成员（确定无此符号时）、UHT 规则错误、缺失 include 的模块依赖 | 不因 `.generated.h` 缺失报错 |
| 自动纠正 | `.` ↔ `->`（根据是否指针/智能指针）、大小写修复 | `TObjectPtr` 视为指针 |
| 引用高亮 | 光标处符号的所有出现，读/写不同底色 | |
| 跳转 / 查找引用 | 见 §6.5 | `_Implementation` / `_Validate` 关联 |

补全实现要点：

- 触发：`.` `->` `::` `(` `<` 以及标识符输入；`Ctrl+Space` 强制。
- 请求在 Engine 端按“当前文件语义缓存 + 索引”计算，目标 p95 < 50 ms；超过 50 ms 时先返回作用域内已知结果，再增量补全。
- 补全列表使用 VS 异步补全 UI（`IAsyncCompletionSource`），图标使用 KnownMonikers，列表项右侧显示类型/模块（对应组件库 `ListRow` 的视觉规范）；UE 反射符号带小徽章。
- 排序参考 VA：精确前缀 > 驼峰首字母 > 子串；同分时按“本类成员 > 基类成员 > 全局”、再按最近使用。

### 7.7 配色方案（参考 Visual Assist）

接管后 VS 自带的 C++ 语义着色（“C++ 函数”“C++ 宏”等由 IntelliSense 提供的分类）会失效，所以 UEToVS 必须提供完整的语义着色，风格参考 VA：**类型、函数、变量、宏、枚举值各自有明显区分的颜色，代码一眼可分层**。

实现方式：注册自己的分类类型（`ClassificationTypeDefinition` + `ClassificationFormatDefinition`），出现在“工具 > 选项 > 环境 > 字体和颜色”中，名称前缀 `UEToVS`，用户可以逐项修改；深色/浅色主题各有一套默认值，跟随 VS 主题切换。

| 分类 | 对应 VA 分类 | 深色主题默认 | 浅色主题默认 | 说明 |
| --- | --- | --- | --- | --- |
| UEToVS 类/结构体/类型 | Classes, structs, types | `#4EC9B0` | `#2B91AF` | 含 typedef/using 别名 |
| UEToVS 函数/方法 | Methods | `#DCDCAA` | `#74531F` | 声明与调用一致 |
| UEToVS 局部变量与参数 | Variables | `#9CDCFE` | `#1F377F` | 参数可单独设置为斜体 |
| UEToVS 成员变量 | Variables（VA 不区分，可选区分） | `#DAD085` | `#6F008A` | 可选，默认与局部变量同色（保持 VA 观感） |
| UEToVS 宏 | Macros | `#BD63C5` | `#8A1BFF` | 函数宏与对象宏同色 |
| UEToVS 枚举值 | Enum members | `#B8D7A3` | `#2F4F4F` | |
| UEToVS 命名空间 | Namespaces | `#C8C8C8` | `#000000` | |
| UEToVS 引擎/外部符号 | VA“stable symbols in italic” | 原色 + 斜体 | 原色 + 斜体 | 对应 VA 把系统/库符号显示为斜体的做法：引擎与第三方符号斜体，项目符号正体，一眼区分“我的代码”和“引擎代码” |
| UEToVS UE 反射宏 | （UE 扩展） | `#C586C0` | `#AF00DB` | `UCLASS`/`UFUNCTION`/`UPROPERTY`/`GENERATED_BODY` 单独着色 |
| UEToVS 反射说明符 | （UE 扩展） | `#CE9178` 弱化 | `#A31515` 弱化 | 括号内 `BlueprintCallable` 等 |
| UEToVS 引用高亮（读/写） | Highlight references | 背景 `#264F78` / `#5A3D1E` | 背景 `#ADD6FF` / `#F6D6A8` | |
| UEToVS 匹配括号 | Matching braces | 边框高亮 | 边框高亮 | |

> 上表颜色是初版建议，取值风格参考 VA 与 VS 默认深/浅色主题的协调色，最终在 M3 用 VA 实际截图逐项校准，并提供“导入 VA 配色”命令：读取 VA 导出的颜色设置（`.vssettings` 中 VA 的分类项）映射到 UEToVS 分类，方便从 VA 迁移的用户保持习惯。

着色管线：
1. **词法着色**（同步、本地、零延迟）：关键字、字符串、注释、数字、预处理指令、已知 UE 宏名。打开文件第一帧就有颜色。
2. **语义着色**（异步、只请求可见区域 ± 50 行）：类型/函数/变量/宏/枚举值/引擎符号，Engine 返回 `(偏移, 长度, 分类)` 列表，按文本快照版本缓存；编辑时只重新请求受影响的行区间。
3. 着色结果在补全列表、Quick Info、Search Everywhere 预览、Find References 结果中复用同一套分类（VA 也在列表和提示中使用彩色符号），由组件库的 `CodePreview` / `HighlightedText` 统一渲染。

---

## 8. UI 组件系统

### 8.1 目标

1. **所有 UI 由组件拼装**：弹窗、工具窗口、对话框、编辑器内装饰都只用组件库里的组件组合，功能代码不直接处理颜色、字体、间距和主题切换。
2. **美观且像 VS 的一部分**：跟随 VS 深色/浅色/蓝色/高对比度主题，使用 VS 字体和 KnownMonikers 图标；在此基础上统一圆角、间距、层级，参考 Rider 的弹窗质感。
3. **快**：弹窗首帧 < 50 ms（预热复用），列表万级条目滚动无掉帧，输入每次按键重绘 < 16 ms。
4. **键盘优先**：所有组件可纯键盘操作，并有一致的按键约定。
5. **可测试**：每个组件可在独立的组件画廊中预览与做截图回归。

### 8.2 设计令牌（Design Tokens）

令牌分两层，功能代码只能使用**语义令牌**：

| 层 | 例子 | 来源 |
| --- | --- | --- |
| 基础令牌 | `Color.Blue.500`、`Space.2 = 4px`、`Radius.M = 4px`、`Font.Mono` | 组件库内定义 |
| 语义令牌 | `Surface.Popup`、`Surface.Raised`、`Text.Primary`、`Text.Secondary`、`Text.Match`（匹配高亮）、`Border.Subtle`、`Accent`、`Selection.Background`、`Badge.UClass`、`Badge.UFunction`、`Badge.UProperty`、`Severity.Error/Warning/Info` | 映射到 VS `EnvironmentColors` / `CommonControlsColors`，或在需要时由基础令牌按主题派生 |

- 颜色：现有 `Theme.cs` 已映射了一部分 VS 资源键，并有固定的 `BadgeBrush`。新设计中徽章颜色也做成按主题派生的令牌（深色主题提高亮度、浅色主题降低饱和度），避免在浅色主题下刺眼。
- 间距：4 px 网格，`Space.1..8`；圆角：`S=2 / M=4 / L=6`；阴影：弹窗一级阴影（自定义无边框窗口使用 DWM 阴影）。
- 字体：界面用 VS 环境字体，代码片段与预览用编辑器字体（读取 VS“文本编辑器”字体设置）。
- 所有 Brush 冻结（`Freeze()`），主题切换时只替换资源字典，不遍历视觉树。

### 8.2.1 设置页微调（用户决定）

所有令牌都可以在“UEToVS 设置 > 外观”（v0.3.0 起为独立设置窗口，见 8.9）中微调，修改即时生效（组件只引用令牌，令牌变化时统一刷新资源字典）：

| 设置项 | 说明 | 默认 |
| --- | --- | --- |
| 界面风格 | VS 2026（默认）/ 紧凑 / 宽松 | VS 2026 |
| 字号缩放 | 相对 VS 环境字体 | 100% |
| 行高 / 列表密度 | 列表每行高度 | 24 px |
| 圆角 | 弹窗、按钮、徽章圆角 | 8 / 4 / 4 px |
| 强调色 | 跟随 VS / 自定义 | 跟随 VS |
| 匹配高亮样式 | 加粗 / 底色 / 下划线 | 加粗 + 强调色 |
| 徽章颜色 | UCLASS、UFUNCTION、UPROPERTY 等徽章各自颜色 | 按主题派生 |
| 弹窗阴影 / 动画 | 开关、动画时长 | 开、100 ms |
| 预览面板 | 默认显示、位置（下方 / 右侧） | 下方 |
| 恢复默认 | 一键还原全部外观设置 | |

视觉风格 v0.3.0 起改为 VS 2026（Fluent），细节见 8.8；以下 Rider 元素仍保留：弹窗大圆角、柔和阴影、输入框顶部通栏、标签页文字式（选中项下划线）、列表行左侧图标 + 主名称 + 灰色位置信息 + 右侧徽章/快捷键、键帽样式的快捷键提示、底部状态栏显示提示和计数。

### 8.3 组件分层

```
基础原子（Atoms）
  Text · HighlightedText（匹配区间高亮）· Icon（ImageMoniker）· Badge · Kbd（快捷键键帽）
  Divider · Spinner · ProgressBar · Avatar/TypeGlyph（类/函数/属性种类图标）

基础控件（Controls）
  Button（Primary/Secondary/Ghost）· IconButton · Toggle · Chip/FilterChip · Checkbox · Radio
  TextInput · SearchInput（带前缀标签、清除、历史）· Dropdown · Tooltip · Link

复合组件（Molecules）
  TabStrip（带计数、Tab 循环）· ListRow（图标+主文本+次要文本+徽章+快捷键）
  VirtualList（虚拟化、分组头、键盘导航、多选）· TreeList（虚拟化树）
  CodePreview（只读编辑器视图，真实语法着色）· DiffView（基于 VS IDifferenceViewer）
  EmptyState · StatusFooter · Banner/InfoBar · Toast

容器（Shells）
  PopupShell（无边框、可锚定光标/居中/锚定元素、失焦关闭、可固定成工具窗口）
  ToolWindowShell（标题栏动作、工具条、内容、状态栏）
  DialogShell（标题、表单区、预览区、按钮区）
  InlineAdornment（编辑器内：Inlay Hint、Code Vision、Inline Rename 输入框）

模式（Patterns，组件的标准组合）
  CommandPalette = PopupShell + SearchInput + TabStrip + VirtualList + CodePreview + StatusFooter
  ChooserPopup   = PopupShell + SearchInput + VirtualList（用于“转到实现”多目标、Navigate To 等）
  RefactorDialog = DialogShell + 表单控件 + DiffView
  ResultsPanel   = ToolWindowShell + TreeList（分组）+ CodePreview
  ContextMenu    = PopupShell + VirtualList（带分组、快捷键、子菜单）——Alt+Enter 使用
```

原型界面在新组件系统中的重写对应：

| 原型 | 重写后 |
| --- | --- |
| `SearchWindow`（507 行，手工拼装） | 用 `CommandPalette` 模式重写，只需实现数据源适配器 |
| `ListPopup` / `NavigationPopups` | `ChooserPopup` 模式 |
| `ResultsToolWindow` + `ResultList` | `ResultsPanel` 模式 |
| `InfoBar` | `Banner` 组件 |
| `Theme.Text(...)` 等辅助函数 | `Text` / `HighlightedText` 原子 + 语义令牌 |

### 8.4 组件 API 约定

- 组件为 WPF `Control` + 默认样式（`Generic.xaml` 中的 ControlTemplate），数据通过依赖属性或轻量 ViewModel 传入；不在组件内部访问 Engine 或 VS 服务。
- 列表类组件只接收 `IReadOnlyList<TItem>` + `IItemPresenter<TItem>`（把数据映射为行的图标/文本/徽章），**由组件负责渲染与回收**，功能模块不写 DataTemplate。
- 所有交互组件暴露命令（`ICommand`）和路由事件，键盘约定统一：
  - `↑/↓` 移动、`PgUp/PgDn` 翻页、`Enter` 打开、`Shift+Enter` 在新标签打开、`Ctrl+Enter` 在结果窗口打开、`Tab` 切换标签页、`Esc` 关闭、`Ctrl+P` 切换预览。
- 尺寸与位置记忆（每个 Popup 的大小、预览分隔位置）由 `PopupShell` 统一持久化。

### 8.5 性能约束

- `VirtualList` 使用 `VirtualizingStackPanel` + 回收模式，或在条目复杂时使用自绘（`DrawingVisual` + 缓存的 `GlyphRun`）实现每行固定高度的极速列表；结果超过 1 万条时只物化可见行。
- `HighlightedText` 用单个 `TextBlock` + `Run` 片段，避免每个字符一个元素。
- 弹窗窗口实例预创建并隐藏复用（现有 `SearchWindow` 已经这样做），首次显示前在空闲时预热模板。
- `CodePreview` 共享一个只读 `IWpfTextView` 实例，切换条目时只替换缓冲区内容和可视范围。
- 禁止在组件里使用 `DropShadowEffect` 等软件渲染效果；动画仅限透明度与位移，时长 ≤ 120 ms，可在设置中关闭。

### 8.6 可访问性与国际化

- 每个组件实现 `AutomationPeer`，读屏可读出“类 AActor，模块 Engine，3 / 120”。
- 高对比度主题下只使用系统颜色令牌。
- **中英双语，跟随 VS 自身语言（用户决定）**：VS 界面语言为英语时显示英文，其他任何语言都显示中文。宿主启动时读取 VS UI 语言（`IUIHostLocale` / `DTE.LocaleID`）设置 `UEToVS.Core.Localization.Loc`；每个模块的界面文案集中在一个 `Strings` 类里（`Loc.T("中文", "English")`），组件监听 `Loc.LanguageChanged` 刷新文案；VSCT 命令名在运行时按语言设置。日志内容保持英文，不做本地化。
- 支持每显示器 DPI（Per-Monitor V2），图标使用 ImageMoniker 以获得矢量缩放。

### 8.7 组件画廊与 UI 测试

- 开发用工具窗口“UEToVS 组件画廊”：列出所有组件与模式，可切换主题、DPI、数据量（空 / 10 / 10 万条）。
- 截图回归：在 CI（Windows）中用 WPF 离屏渲染组件的典型状态到 PNG，与基线比较。
- 交互测试：基于 UI Automation 驱动组件的键盘行为。


### 8.8 VS 2026 视觉规范（v0.3.0）

**调研结论**：VS 2026（18.x）把 IDE 界面整体迁移到 Fluent 设计语言——新的主题系统（深色 / 浅色 / Cool Breeze 等多套主题，18.7 起可编辑 Fluent 颜色令牌）、更大的圆角、更轻的边框、细滚动条、仅键盘焦点时出现的焦点环，强调色默认是 Windows/Fluent 蓝 `#0078D4`。微软没有公开像素级的控件规格，所以下面的数值取自 Fluent 2（WinUI 3）控件度量，在 VS 的环境字体（9 pt Segoe UI）下核对过；颜色依然全部从 VS 主题派生（`ThemeManager`），只有在主题不提供时才用默认强调色。

| 令牌 / 部位 | VS 2026 预设 | 说明 |
| --- | --- | --- |
| `CornerRadiusPopup` | 8 px | 弹窗、浮动窗口、卡片（Fluent “overlay”） |
| `CornerRadiusControl` | 4 px | 按钮、输入框、分段控件、筛选芯片、下拉框 |
| `CornerRadiusBadge` | 4 px | 徽章、键帽 |
| `CornerRadiusInner` / `CornerRadiusFocus` | 控件圆角 − 1 / + 3 | 内层填充 / 焦点环 |
| `ControlHeight` | 26 px（按字号缩放） | 按钮、输入框最小高度 |
| `PadControlX` / `Gap` | 12 / 8 px | 控件水平内边距 / 控件间距（4 px 网格） |
| `FontSizeTitle` | 正文 + 3 pt | 窗口标题、设置分类标题 |
| 焦点环 `FocusStroke` + `ThicknessFocus` | 2 px，外扩 3 px | 只在键盘焦点（`IsKeyboardFocused`）时显示，鼠标点击不出现 |
| 输入框聚焦 `InputFocusUnderline` | 底部 2 px 强调色 | Fluent TextBox 样式；未聚焦时底边 `ControlStrokeBottom` 略深 |
| 选中指示 `SelectionIndicator` | 3 px 圆角竖条 | 列表行、设置分类导航左侧 |
| 分段控件 `SegmentTrack` | 轨道底色 + 2 px 内边距 | 选中段填充 `Accent`、文字 `TextOnAccent`，圆角 4 |
| 筛选芯片（Chip） | 圆角 4 的矩形 | 选中填充强调色（不再是胶囊形） |
| 开关（Toggle） | 36×18 轨道，10 px 圆点 | 开：强调色填充，圆点白色 |
| 复选框 | 16 px，圆角 4 | 选中强调色填充 |
| 标签页（TabStrip） | 3 px 下划线，选中半粗体 | 悬停有圆角浅底 |
| 滚动条（ScrollRail） | 3 px，悬停 6 px | 拇指半透明，按主题派生 |
| 阴影 | 模糊 24（深色主题）/ 20（浅色主题），偏移 4 | 用 DWM / 预渲染边距，不用 `DropShadowEffect` |
| `SurfaceNav` | 比窗口底色略深 / 略亮 | 设置窗口左侧分类栏、浮窗标题栏 |

预设：**VS 2026**（默认，行高 24、圆角 8/4/4、间距 ×1.0）、**紧凑**（行高 20、圆角 6/3/3、间距 ×0.75）、**宽松**（行高 28、圆角 8/5/4、间距 ×1.25）。旧版本保存的 “Rider 风格” 自动迁移为 VS 2026；旧默认圆角 6/3 与旧默认强调色 `#3574F0` 迁移为新默认值，用户改过的值保留（`AppearanceSettings.CurrentVersion = 2`）。

浮动窗口统一用 `UtilityWindow`（`UEToVS.UI.Shells`）：40 px 标题栏（品牌图标 + 标题 + 标题栏操作 + 关闭）、可拖动、可调整大小、Esc 关闭、由 VS 主窗口拥有；位置和大小按窗口 id 记在 VS 用户设置 `UEToVS\Windows`，恢复时夹回可见显示器；双击标题栏恢复默认大小并居中。设置窗口（`settings`）、性能面板（`performance`）、组件画廊（`componentGallery`）都用它。

### 8.9 设置窗口、快捷键与工具栏（v0.3.0）

**设置窗口**（`Host/Settings/SettingsWindow.cs`）取代 工具 > 选项 下的 UEToVS 页面：左侧分类（常规 / 外观 / 索引 / 快捷键 / 导航 / 日志与诊断 / 性能），顶部搜索（标题、说明、关键字、分组名都能搜，Ctrl+F 聚焦），底部 恢复默认（当前页）/ 取消 / 应用 / 确定。行由声明式描述生成：

- `UEToVS.UI.Logic.SettingsModel`（无 WPF 依赖，有单元测试）：`SettingDescriptor`（Key、分类、分组、类型 Bool/Int/Choice/Text/Path/Action/Info、默认值、范围、选项、“自动”标签、关键字）与 `Normalize / Matches / Diff / Defaults / Validate`。
- `UEToVS.Host.Logic.Settings.HostSettingsSchema`：所有描述（键如 `indexing.cppThreads`、`nav.ctrlClick`）和 `Codec`（`HostSettings` ⇄ 字符串值，`ToIndexing` 生成推给引擎的 `IndexingSettings`）。单元测试保证每个 `HostSettings` 属性都能在窗口里改到。
- 持久化：VS 用户设置 `UEToVS\Settings`（`SettingsStore`）。首次运行从旧的 选项页（General / Logging DialogPage）迁移一次并记日志 `settings.migrated`；解决方案级覆盖（`.vs/UEToVS/settings.json`）不变。“应用 / 确定”调用 `SettingsService.ApplyUserValues`，引发 `Changed`，各服务实时生效（日志级别、索引线程推给引擎、工具栏显隐、导航 / M1 设置等）。
- 工具 > 选项 > UEToVS 只剩一个说明页和“打开 UEToVS 设置”按钮。所有原来打开选项页的入口（菜单、Find Action、IntelliSense 提示条的“设置”链接）都打开新窗口。
- 自定义页：外观（沿用 `AppearanceSettingsPanel`，应用 / 取消与窗口一致）和快捷键（`KeymapPage`）。

索引页：C++ / 蓝图线程数（0 = 自动 = max(1, 逻辑核心数 / 4)）、编译期间（暂停 / 单线程 / 全速）、索引引擎源码、引擎索引位置、三个重建按钮（项目 / 引擎 / 蓝图，分别对应 `RebuildScope` 3 / 4 / 1）、实时索引状态。引擎索引默认放在本解决方案的 `.vs\UEToVS\Index`；打开“在多个工程间共享引擎索引”（`ShareEngineIndex`，默认关）后放到 `EngineIndexPath`，留空为 `%LocalAppData%\UEToVS\EngineIndex\<引擎版本>`。

**命令 ID 段**：0x01xx 宿主命令，0x0200–0x02FF M2 导航，0x0300–0x03FF M1 功能，0x0400–0x04FF 设置 / 窗口 / 工具栏。`Logic/Commands/CommandCatalog.cs` 是快捷键页和测试使用的命令目录（id、规范名、动作 id、三套预设键位、实现模块），单元测试检查它与 VSCT 的 id、规范名、默认键位一致且默认键位无重复。

**默认快捷键（Visual Assist 风格，VSCT KeyBindings）**：Alt+G 转到定义、Alt+Shift+G 转到实现、Shift+Alt+F 查找引用、Shift+Alt+H 类型层次、Alt+M 文件成员、Shift+Alt+M 类型成员、Alt+O 头/源切换、Shift+Alt+O 转到文件、Shift+Alt+S 转到符号、Alt+← / Alt+→ 后退 / 前进、Ctrl+Alt+F11 Live Coding、Ctrl+Shift+Alt+A Find Action；Search Everywhere 为双击 Shift（M1 的输入钩子，VSCT 无法表达）。

**快捷键页**：列出目录中的命令和 VS 当前绑定（DTE `Command.Bindings`，在 UI 线程分块读取），点击后录制新快捷键（Esc 取消、Backspace 清除）；一键套用 Visual Assist / Rider / VS 默认 预设；与 VS 全部命令做冲突检测，可一键移除其他命令上的冲突键；每次修改前把受影响命令的原绑定备份到 `%LocalAppData%\UEToVS\KeymapBackups\keymap-backup-*.json`，可逐次还原。M1 的“完整 Rider 键位方案”（Find Action 中）继续独立存在。

**菜单与工具栏**：扩展 > UEToVS 顶层只保留 搜索组（Find Action、Search Everywhere 等）、导航组（M1 / M2）、性能面板、设置…；重启引擎、接管 / 恢复 IntelliSense、打开日志文件夹、打开当前日志、生成诊断包、组件画廊移到设置窗口（仍是注册命令，Find Action 可搜、可绑快捷键）。UEToVS 工具栏（`tbUEToVS`）首次运行默认显示：品牌图标（打开设置）、Search Everywhere、后退、前进、转到定义、查找引用、性能面板，图标用 KnownMonikers；整个工具栏和每个按钮（品牌按钮除外）都可在 常规 > 工具栏 中隐藏。品牌图标源文件 `Host/Resources/UEToVS.svg`，WPF 版本 `UI/Atoms/BrandMark.cs`。

---

## 9. 性能与内存设计

### 9.1 延迟预算

| 场景 | 目标（p95） | 硬上限 | 说明 |
| --- | --- | --- | --- |
| 按键后 UI 线程占用 | 4 ms | 16 ms | 宿主只做 debounce 和投递 |
| 补全列表出现 | 50 ms | 150 ms | 超时先显示已有结果再增量补充 |
| 转到定义 | 30 ms | 200 ms | 不确定时显示 ChooserPopup |
| Quick Info | 50 ms | 300 ms | |
| Search Everywhere 每次按键出结果 | 30 ms | 100 ms | 现有实现需加入 trigram 剪枝 |
| 查找引用首批结果 | 200 ms | 1 s | 流式返回，后续结果持续追加 |
| 着色（可见区域） | 30 ms | 100 ms | 先显示词法着色，语义着色后补 |
| 单文件保存后索引更新 | 100 ms | 500 ms | 增量段 |
| 冷启动：文件搜索可用 | 1 s | 3 s | 现有实现已接近 |
| 冷启动：符号可用（有缓存） | 2 s | 5 s | mmap 后几乎无反序列化 |
| 首次引擎全量索引（UE 5.x，8 核） | 60 s | 180 s | 后台低优先级；现有声明级扫描约 6 s，加入引用索引后预计显著增加 |

### 9.2 内存预算（典型：UE 5.x 源码版引擎 + 中型项目 3000 个文件）

| 组件 | 预算 | 手段 |
| --- | --- | --- |
| VS 进程内增量（UEToVS.Host + UI） | ≤ 80 MB | 不缓存索引数据；结果分页；弹窗复用 |
| Engine 进程私有工作集 | ≤ 400 MB | 结构体数组、字符串池、mmap 基段、增量段上限 |
| 引擎基段（mmap，共享，可被系统换出） | 磁盘 ≤ 1.5 GB，常驻视访问而定 | 多 VS 实例共享同一份物理页 |
| Clang Worker（启用时） | ≤ 1.5 GB，可配置 | Job Object 限制；空闲退出；不建后台索引 |
| 多开：第二个同引擎项目 | 增量 ≤ 250 MB | 引擎段共享 |

超出预算时的降级顺序：关闭语义着色缓存 → 缩小增量段阈值、提前合并 → 停用 Clang Worker → 提示用户并在诊断面板给出原因。

### 9.3 数据布局

- 所有实体用 32 位 ID：FileId、SymbolId、NameId、ModuleId、AssetId。
- 符号记录改为结构体（约 32 字节），按列或按行紧凑存放：

```
struct SymbolRecord        // 32 bytes
  uint  NameId
  uint  ContainerId        // SymbolId 或 0
  uint  FileId
  uint  Offset             // 文件内字节偏移；行列号按需由行表换算
  uint  SignatureId        // 字符串池
  uint  SpecifiersId       // 字符串池
  ushort Kind
  ushort UeFlags
  uint  Flags              // IsDefinition、访问级别、virtual/static/const 等
```

  25 万符号约 8 MB，对比当前每个符号一个对象 + 多个字符串引用，内存可降一个数量级。
- 字符串池：所有名称、路径片段、签名去重存储，UTF-8 存储，比较时直接按字节比较；查询时才转为 .NET string。
- 行表：每文件一个行起始偏移数组（varint 压缩），用于偏移 ↔ 行列换算，只在需要显示时计算。
- 引用出现表：按 NameId 分桶，桶内 (FileId, Offset, Role) 按 FileId 排序后 delta + varint 编码，平均每条约 3–4 字节。UE 引擎预估数千万条出现，压缩后约百 MB 级，存放在 mmap 基段中，不进托管堆。

### 9.4 计算策略

- **零分配热路径**：词法、解析、搜索匹配都基于 `Span<T>` 和 `ArrayPool<T>`；每个解析线程一个可重置的 arena。
- **并行与让步**：索引并行度默认 `核数 - 1`，线程优先级 BelowNormal；检测到 VS 正在构建（`IVsUpdateSolutionEvents`）或 UBT 进程运行时，自动降到 1 个线程或暂停；笔记本电池模式下进一步降低。
- **增量**：文件内容哈希（xxHash64）判断是否需要重解析，避免只改 mtime 的情况（如切分支后切回）触发重解析。
- **惰性**：语义模型只对当前请求的文件构建，LRU 缓存最近 N 个文件的语义结果（N 可配，默认 64）。
- **搜索剪枝**：名称的 trigram 倒排 + 驼峰首字母索引先筛候选，再用 `FuzzyMatcher` 打分，替代当前对全部符号的线性扫描；保留 TopN 堆（现有 `Searcher.TopN`）。

### 9.5 GC 与运行时

- Engine：Workstation 并发 GC（现有配置）；设置 `GCHeapHardLimit`（例如 512 MB）作为保险，超限触发降级而不是让系统换页；考虑 `GCConserveMemory`。
- 启动性能：Engine 使用 ReadyToRun 编译，减少 JIT 时间；评估 NativeAOT（需要把 StreamJsonRpc 替换为源生成器友好的序列化，见 ADR-04）。
- VS 侧（.NET Framework 4.8）：避免 LINQ 和闭包出现在按键路径；大集合使用数组池；不持有对 `ITextSnapshot` 的长期引用（防止旧快照无法回收）。

### 9.6 进程间通信

- 传输：命名管道（已有），ACL 只允许当前用户。
- 序列化：交互路径使用 MessagePack（StreamJsonRpc 支持 MessagePack 格式化器，迁移成本低）；保留 JSON 仅用于调试。
- 流式结果：查找引用、全项目检查等使用 `IAsyncEnumerable<T>` 分批返回（每批 200 条或 50 ms）。
- 不传全量文本：编辑同步只传增量（版本号 + 修改区间 + 新文本），Engine 侧校验版本，不一致时请求全量。

### 9.7 性能工程流程

- 基准语料：固定版本的 UE 引擎源码（如 5.4、5.5、5.6 各一份）+ Lyra 示例工程 + 一个合成的超大项目（2 万文件）。
- 基准测试：BenchmarkDotNet 覆盖词法、解析、索引构建、搜索、查找引用；CI 上与上一次主干结果对比，退化超过 10% 则失败。
- 运行时观测：Engine 与 Host 都通过 `EventSource` 输出 ETW 事件（请求开始/结束、阶段耗时、内存快照），可用 PerfView / WPA 分析；诊断面板读取相同的计数器。
- 内存回归：每次基准运行记录 Engine 私有工作集与托管堆大小，纳入同一退化门槛。

### 9.8 性能面板（用户决定）

工具窗口“UEToVS 性能”（扩展 > UEToVS > 性能面板，也可在 Find Action 中搜索），由组件库的 `PerformancePanel` 模式拼装：

| 区域 | 内容 | 数据来源 |
| --- | --- | --- |
| 顶部指标卡 | Engine 进程内存、VS 内扩展内存（devenv 私有内存 + 托管堆）、Engine CPU、索引状态与进度 | `GetPerformanceSnapshotAsync` + 宿主自采样 |
| 趋势图 | 最近 10 分钟 CPU 与内存曲线 | Engine 每秒采样的环形缓冲 |
| 正在进行 | 当前后台任务（名称、所在队列、进度、耗时），队列长度，后台是否暂停 | 调度器 |
| 索引阶段 | 每个阶段（列文件、读缓存、解析、合并…）的耗时、CPU 时间、条目数、缓存命中等 | 索引编排 |
| 索引内存构成 | 字符串池、符号表、引用表、增量段等的托管内存 / 映射内存 / 磁盘占用 | 索引统计 |
| 操作延迟 | 各操作次数、错误数、p50 / p95 / max，超出 §9.1 预算的行高亮 | `Log.LatencySinceStart` |
| 操作按钮 | 打开日志、生成诊断包、暂停 / 恢复后台索引 | 宿主命令 |

只在面板可见时每秒轮询一次，关闭即停止，面板本身不增加常驻开销。面板中显示的数据与日志中的 `resource.sample`、`perf.summary` 同源，便于对照。

---

## 10. 工业级工程约束

### 10.1 模块化与契约

- 每个模块有：明确的职责说明、公开接口清单、负责人、测试项目、性能预算（若在热路径上）。
- Engine 的 RPC 契约放在 `UEToVS.Contracts`，带协议版本号；Host 启动时做版本握手，不匹配时提示重新安装而不是静默出错。
- 契约只增不改：新增字段可选，删除字段走“标记废弃 → 两个版本后删除”。

### 10.2 可靠性与故障隔离

- Engine 崩溃：Host 自动重启（指数退避，最多 3 次/10 分钟），期间功能降级为 VS 内置能力，并在状态栏显示。
- Engine 卡死：每个请求有超时；心跳超时则重启。
- 索引损坏：文件头校验 + 每段校验和；校验失败只重建对应段。
- 单文件解析异常不影响整体：异常被捕获、记录（文件路径 + 偏移），该文件降级为“仅文件索引”。
- 所有写文件操作（重构、ini 修改、Build.cs 修改）先预览、可撤销，写盘采用“写临时文件 + 原子替换”。

### 10.3 配置

三层覆盖：内置默认 < 用户设置（VS 选项页）< 解决方案设置（仓库内 `.uetovs/settings.json`，团队共享，如命名规范、检查规则、代码片段、额外宏定义）。

### 10.4 测试策略

| 层级 | 内容 |
| --- | --- |
| 单元测试 | Core、Syntax、Semantics、Refactoring 的纯逻辑（Linux/Windows 均可运行，延续现有做法） |
| 语料回归 | 在 UE 引擎与 Lyra 上运行“已知答案”查询：给定位置的定义、引用数、补全项、宏展开结果；与基线对比，统计准确率 |
| 重构快照测试 | 输入代码 + 重构参数 → 期望输出代码（文本快照） |
| 架构测试 | 依赖方向、禁止引用规则（§5.2） |
| 集成测试 | 启动 Engine 进程走真实 RPC（原型 `RpcTests` 的用例作为规格迁移） |
| UI 测试 | 组件截图回归 + 键盘交互（§8.7） |
| VS 端到端 | 在 Windows CI 上用 VS 实验实例（`/rootsuffix Exp`）跑冒烟测试 |
| 性能测试 | §9.7 |

### 10.5 可观测性

日志是必须的功能，目标是：**出了问题看日志就能定位；日志本身足够让 AI 分析插件是否有问题、哪里可以优化**。

- **位置**：`<解决方案目录>\.vs\UEToVS\Logs\`。Host（VS 进程内）与 Engine 各写各的文件：`host-<日期>-<pid>.log`、`engine-<日期>-<pid>.log`；单文件超过 10 MB 滚动，默认保留最近 7 天 / 200 MB。
- **格式**：JSON Lines，每行一个事件，字段固定：`ts`（UTC，毫秒）、`lvl`、`proc`（host/engine）、`mod`（模块名）、`evt`（事件名，稳定的英文标识）、`msg`（中文或英文说明）、`req`（请求 ID，可跨进程关联）、`ms`（耗时）、`data`（结构化附加字段）、`ex`（异常类型、消息、完整堆栈、内部异常）。机器可读、grep 友好、AI 可直接解析。
- **必须记录**：
  - 所有未处理异常与被捕获的异常（含 VS 侧 `AsyncPackage` 任务、MEF 组件、UI 事件处理器里的异常；Engine 侧每个 RPC 处理器、后台任务）；任何功能的失败都必须进日志，不允许静默吞掉。
  - 会话头：插件版本、协议版本、VS 版本与语言、OS、CPU 核数、内存总量、.NET 运行时、UE 引擎版本与路径、项目路径、启用的功能开关、IntelliSense 接管状态。
  - 生命周期：包加载、Engine 启动/重启/退出码、连接断开与重连、索引各阶段开始/结束（文件数、符号数、耗时、缓存命中率）。
  - 性能：每个 RPC 请求的耗时（超过预算的记 Warn 并带参数摘要）、UI 线程单次占用超过 16 ms 的操作、索引合并耗时。
  - 资源：每 60 秒一次资源快照（Engine 私有工作集、托管堆、GC 次数、线程数、索引段大小、增量段大小），超出 §9.2 预算记 Warn。
  - 周期性汇总：每 5 分钟输出各请求类型的次数、p50/p95/max 延迟、错误数，便于直接判断“哪里慢”。
- **隐私**：日志不写源码内容，只写路径、符号名、数量和耗时。
- **级别**：Error / Warn / Info / Debug / Trace，默认 Info；设置页可调，Debug 以上用于临时排查。“输出 > UEToVS”窗口同步显示 Info 以上的可读版本。
- **一键诊断包**：把 Logs、设置（脱敏）、索引 manifest 打包成 zip，便于发给 AI 或自己分析。
- 不做任何联网遥测；所有计数器只在本地。

### 10.6 安全与隐私

- 管道与 TCP 只监听本机，管道 ACL 限定当前用户；UEToVSLink 使用随机 token 握手。
- AI 功能默认关闭；启用外部模型时明确提示哪些内容会发送给哪个服务，使用用户自己的账号；支持纯本地模型。
- MCP 服务默认只读工具；写操作（重命名等）需要在 VS 中确认。

### 10.7 版本与发布

- 语义化版本；Engine、Host、UEToVSLink 同版本发布，协议版本独立管理。
- CI：Windows 上构建 VSIX、跑全部测试与基准（在现有 workflow 基础上调整）；主干合并自动出预发布包。
- 支持 VS 2022（17.x）与 VS 2026（18.x）；UE 5.3 及以上（知识库按版本分组，旧版本尽力支持）。

---

## 11. 关键架构决策（ADR 摘要）

### ADR-01：主语义层采用自研容错解析器，而不是完全依赖 Clang

- **背景**：需要在 UE 工程上做到毫秒级补全、跳转和查找引用，且不依赖 `.generated.h`。
- **选项**：A. 自研容错解析 + UE 宏知识库；B. 完全基于 clangd；C. 依赖 VS IntelliSense 数据。
- **决定**：A 为主，Clang 补位（ADR-02）。
- **理由**：clangd 需要完整 include 展开，UE 单个 cpp 的 preamble 动辄数百 MB、首次全量索引数十分钟，且 UHT 生成文件缺失时同样报错；VS IntelliSense 数据无法以足够低的延迟和足够的 UE 语义拿到。VA 的长期实践证明 A 路线在 VS 中可行。
- **代价**：模板、重载等复杂场景不精确；用置信度标注和 Clang 二次确认缓解。

### ADR-02：Clang 作为可选精确层，按需启动

- 用于代码检查、精确宏展开、复杂场景确认。默认不常驻，限制内存，空闲退出。

### ADR-03：采用进程外 Engine，索引 mmap 加载并存放在 .vs 内

- 解析与索引放在独立进程（现有原型也是这样做的，这一点得到验证）。索引按段 mmap 只读加载，不进托管堆；索引存放在 `<解决方案目录>\.vs\UEToVS\Index`（用户决定，见 §6.4），因此引擎段按项目各存一份。

### ADR-04：IPC 从 JSON 迁移到 MessagePack，支持流式

- StreamJsonRpc 原生支持 MessagePack 格式化器，迁移成本低；大结果集序列化开销和 GC 压力明显下降。若后续 Engine 改为 NativeAOT，再评估替换为源生成器方案。

### ADR-05：VS 侧继续使用进程内 VSSDK，暂不迁移到 VisualStudio.Extensibility

- 新的进程外扩展模型（VisualStudio.Extensibility）在编辑器深度集成（补全、着色、装饰、边距）方面能力仍不完整。宿主保持进程内但“薄”，计算全部在 Engine，未来迁移成本可控。

### ADR-06：UI 采用自研组件库，基于 WPF 与 VS 主题资源

- 不引入第三方 WPF 控件库（体积、主题适配、加载成本）。组件库以 VS 资源键为底，自定义语义令牌。

### ADR-07：功能间协作通过 Action 中枢，不直接引用

- 保证 Alt+Enter、Find Action、菜单、快捷键的一致性，并让功能模块可独立开关、独立测试。

### ADR-08：插件启用后自动关闭 VS 自带 C++ IntelliSense

- **背景**：两套 C++ 感知同时运行会出现双重补全/提示、互相抢快捷键，且 VS IntelliSense 在 UE 工程上占用大量内存、CPU 并在 UE 宏上误报。
- **决定**：UE 解决方案中由 UEToVS 全面接管（补全、着色、Quick Info、波浪线、导航），自动关闭 VS 自带 C++ IntelliSense，修改前备份、可一键恢复（§7.5）。
- **代价**：UEToVS 必须覆盖 VS 原有的全部日常感知能力（§7.6、§7.7），因此自动接管在 M3 达标后才默认启用。

---

## 12. 路线图与里程碑

每个里程碑结束时都要求：基准不退化、语料回归准确率不下降、内存在预算内。

| 里程碑 | 内容 | 退出标准 |
| --- | --- | --- |
| **M0 骨架重建** | 按 §5.3 新建解决方案结构；Contracts 与 RPC（MessagePack）；Engine 进程骨架与调度；UI 组件库雏形（令牌 + 原子 + PopupShell + VirtualList）；Action 中枢；规则化 UIContext；诊断面板 v1；基准与语料框架；架构测试。旧代码移到 `legacy/` 仅供对照，不参与构建 | 新骨架上 CI；架构测试通过 |
| **M1 新索引与解析 + 功能对齐** | UTF-8 零分配词法；容错解析器（声明 + 函数体引用）；UE 宏知识库 v1；分段 mmap 索引；字符串池；trigram 搜索；流式查询；在新架构上重新实现原型已有的全部功能（Search Everywhere、导航历史、书签、键位、相关项、模块依赖修复、资产搜索、Live Coding），完成后删除 `legacy/` | 原型功能全部对齐；引擎冷索引 < 60 s；Engine 私有内存 < 400 MB；搜索 p95 < 30 ms |
| **M2 核心导航** | 转到定义/实现、查找引用、类型层次、引用高亮、导航栏、成员列表增强 | 语料回归：定义准确率 ≥ 95%（UE 宏场景） |
| **M3 UE 感知 IntelliSense** | 成员补全、VA 风格语义着色、Quick Info、参数信息、宏展开悬停、`.`/`->` 纠正、反射说明符补全、自动关闭 VS 自带 C++ 感知（备份/恢复）、导入 VA 配色 | 补全 p95 < 50 ms；VS IntelliSense 关闭后所有感知功能正常；配色与 VA 截图逐项校准 |
| **M4 重构与生成** | Rename（含 Core Redirects）、Create Decl/Impl（UE 三件套）、Change Signature、Extract Method、Encapsulate Field、Implement Virtual、Add Include/Forward Decl、Snippets、Diff 预览 | 重构快照测试全绿；所有重构一次撤销 |
| **M5 UE 深度** | UHT 规则检查、网络复制检查、命名规范、委托导航、资产路径与 GameplayTags、ini 联动、蓝图用法（离线 + Link）、Code Vision、HLSL 支持 | 每项都有语料用例 |
| **M6 检查与 AI** | Clang Worker、clang-tidy 检查与全项目批量修复、Include Cleaner；AI 解释/修改（带 diff 与本地校验）；MCP 服务 | Clang 启用时内存在预算内；AI 默认关闭 |
| **M7 打磨** | Inline Rename、Inlay Hints、Postfix 模板、调用层次、多重剪贴板、日志窗口、测试运行器、Natvis、引擎升级助手、Include 成本分析 | — |

---

## 13. 风险与对策

| 风险 | 影响 | 对策 |
| --- | --- | --- |
| 自研解析器在复杂 C++ 上准确率不足 | 跳转错、重构漏改 | 置信度标注；重构必预览；Clang 二次确认；语料回归持续跟踪 |
| UE 版本差异（宏、说明符、目录结构变化） | 新版本支持滞后 | 知识库数据化、按版本分组；新 UE 版本发布后先跑语料回归 |
| 与 VA / Copilot 的快捷键和补全冲突 | 体验割裂 | 冲突检测与二选一提示；键位与 IntelliSense 设置都有备份恢复 |
| 关闭 VS IntelliSense 后我们的感知在 M3 前不完整 | 早期版本体验倒退 | 自动接管只在 M3 完成后默认开启；此前作为实验选项 |
| 卸载扩展后 VS IntelliSense 仍处于关闭状态 | 用户困惑 | 禁用前提示恢复；设置页与 README 写明手动恢复步骤 |
| 引用索引体积过大 | 磁盘与首次索引耗时 | 压缩编码；引擎段共享与可预构建分发；引擎符号引用可按需延迟索引（先只索引项目对引擎符号的引用） |
| VS 版本 API 变化（2026 及以后） | 适配成本 | 宿主保持薄；编辑器适配层隔离；CI 覆盖两个主版本 |
| 蓝图离线解析依赖 UE 资产格式细节 | 跨版本失效 | 优先编辑器在线查询；离线结果标注精度；格式解析做版本守卫 |
| 单人开发、功能面过大 | 进度失控 | 严格按里程碑推进，P0 优先；每个功能可独立开关，未完成功能不阻塞发布 |

---

## 14. 附录

### 14.1 术语

- **UHT**：Unreal Header Tool，解析反射宏并生成 `.generated.h` / `.gen.cpp`。
- **UBT**：Unreal Build Tool，负责模块构建与工程文件生成。
- **Core Redirects**：UE 中把旧类/属性名映射到新名的配置，保证重命名后资产仍能加载。
- **容错解析**：遇到语法错误或未知宏时不中断，尽可能恢复并继续产出结果的解析方式。
- **基段 / 增量段**：分段索引中不可变的大段与可变的小段，参考 LSM 树思想。

### 14.2 调研来源

- Visual Assist 新功能记录：<https://www.wholetomato.com/features/whats-new>
- Visual Assist 功能总览：<https://www.wholetomato.com/features>
- VA 2026.1 发布说明：<https://www.wholetomato.com/blog/visual-assist-2026-1-release-blog/>
- VA 2026.3 发布说明：<https://www.wholetomato.com/blog/visual-assist-2026-3-release-post/>
- VA Intelligence：<https://wholetomato.com/va-intelligence>
- VA 2026.5 全项目检查（ComponentSource）：<https://www.componentsource.com/news/2026/11/09/find-code-issues-across-multiple-projects>
- VA 版本历史（ComponentSource）：<https://www.componentsource.com/product/visual-assist/releases>
- Rider Unreal Engine 设置参考：<https://www.jetbrains.com/help/rider/2026.2/Reference__Options__Languages__CPP__Unreal_Engine.html>
- Rider 蓝图支持：<https://www.jetbrains.com/help/rider/2026.2/Unreal_Engine__Blueprints.html>

> 说明：VA 的功能列表以官方发布记录为准；部分 UE 细节（例如 VA 是否提供反射说明符补全）在官方页面中没有找到明确记录，本文没有把它们算作 VA 已有能力。Rider 交互部分基于其公开文档与产品行为整理。
