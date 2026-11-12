# UEToVS.Host（VS 扩展宿主）

`UEToVS.Host` 是运行在 `devenv.exe` 进程内的 VSIX（net48），对应设计文档 §5–§7 的 L1/L4 层：包加载、命令、Action 中枢、设置、诊断与日志、IntelliSense 接管、引擎进程管理、性能面板。所有解析/索引/搜索都在独立的 `UEToVS.Engine` 进程中完成，宿主保持“薄”。

## 加载策略（§7.1）

- 包 `HostPackage`（`AsyncPackage`，后台加载）只通过**基于规则的 UIContext** 自动加载：解决方案已完全加载 **且** 含有 VisualC 项目（`SolutionExistsAndFullyLoaded & SolutionHasProjectCapability:VisualC`）。没有解决方案或非 C++ 解决方案时不会加载。
- 加载后用 `UnrealSolutionDetector` 判断是否为 UE 解决方案：解决方案目录或其上级（最多 3 级）有 `.uproject`，或 `.sln` 由 UBT 生成（`Intermediate\ProjectFiles\` + `UE4/UE5` 工程），或是引擎源码目录（`Engine/Source` + `Engine/Build/Build.version`）。
- **只有 UE 解决方案**才会：把日志切到 `<解决方案>\.vs\UEToVS\Logs`、写会话头、启动引擎、接管 IntelliSense、激活 UIContext `UEToVS.UnrealSolution`（`67abac9a-…`，供编辑器适配器等使用）。非 UE 解决方案什么都不启动。
- 界面语言跟随 VS：英文 VS 显示英文，其他语言显示中文（`IUIHostLocale.GetUILocale`，回退 `DTE.LocaleID`）。所有界面文字集中在 `Logic/Localization/Strings.cs`；日志内容保持英文。

## 命令（扩展 > UEToVS）

| 命令 | Action ID | 说明 |
| --- | --- | --- |
| 查找操作… | `uetovs.findAction` | 命令面板（UI 组件库的 `CommandPalette`），列出当前可用的全部操作；可在 工具 > 选项 > 环境 > 键盘 中为 `UEToVS.FindAction` 绑定快捷键 |
| 重启引擎 | `uetovs.engine.restart` | 手动重启，同时重置自动重启次数 |
| 接管 VS C++ IntelliSense / 恢复 VS 自带 C++ IntelliSense | `uetovs.intellisense.takeOver` / `uetovs.intellisense.restore` | 菜单里只有一项，按当前状态切换：还没接管（没有备份）时显示“接管”，立即执行接管（会先备份）；已接管（存在备份）时显示“恢复”，按备份逐项还原。Find Action 里两个操作都在 |
| 打开日志文件夹 / 打开当前日志 | `uetovs.diagnostics.openLogFolder` / `openCurrentLog` | |
| 生成诊断包 | `uetovs.diagnostics.createPackage` | zip：日志、脱敏设置、索引 manifest、环境/引擎/IntelliSense/性能快照 |
| 性能面板 | `uetovs.diagnostics.performancePanel` | 工具窗口“UEToVS 性能”，仅可见时每秒采样 |
| 组件画廊 | `uetovs.ui.componentGallery` | UI 组件库的 `ComponentGallery` 窗口（开发用），父窗口为 VS 主窗口 |
| 设置… | `uetovs.settings.open` | 打开 工具 > 选项 > UEToVS |

### 导航（M2，扩展 > UEToVS 与编辑器右键 > UEToVS）

| 命令 | 默认快捷键 | Action ID | 说明 |
| --- | --- | --- | --- |
| 转到定义 / 声明 | Alt+G | `uetovs.navigation.goToDefinition` | Toggle：在声明处去定义，在定义处回声明，其他位置去定义。一个目标直接跳，多个弹选择器，没有结果时状态栏说明原因 |
| 转到实现 | Alt+Shift+G | `uetovs.navigation.goToImplementation` | 重写、派生类型、UE 的 `_Implementation` / `_Validate` |
| 查找引用 | Shift+Alt+F | `uetovs.navigation.findReferences` | 浮动弹窗：按文件分组、行号 + 行文本高亮、读/写等徽章、不确定的结果灰显；工具条：重新搜索、只看写入、包含引擎代码、显示不确定的结果 |
| 类型层次 | Alt+Shift+H | `uetovs.navigation.typeHierarchy` | 浮动弹窗：基类 / 派生类两个标签，未在索引中的类型灰显 |
| 文件成员列表 | Alt+M | `uetovs.navigation.listMembersInFile` | 当前文件的声明，输入即模糊过滤 |
| 类型成员（含继承） | Alt+Shift+M | `uetovs.navigation.listTypeMembers` | 光标所在类型的成员，继承来的灰显并标注“继承自 X” |

- **Ctrl+鼠标左键点击 = 转到定义**（不是搜索）。按住 Ctrl 悬停在标识符上会出现下划线和手形光标；Ctrl+Alt / Ctrl+Shift 点击仍是 VS 的多光标 / 扩展选择。设置 `CtrlClickNavigation`。
- 已接管 VS C++ IntelliSense 时，F12 在 UE 解决方案的 C++ 编辑器里也走 UEToVS 的转到定义。
- **引用高亮**：光标停留约 150 ms 后高亮当前符号在本文件的其他出现处，读 / 写两种颜色（字体和颜色里的 “UEToVS Reference Highlight (Read/Write)”，设置 `ReferenceHighlightReadColor` / `WriteColor` 覆盖，留空按深浅主题取默认）。设置 `ReferenceHighlighting`。
- **导航栏**：C++ 编辑器顶部，左边当前类型、右边当前函数，点击弹出可模糊过滤的列表。设置 `NavigationBar`。
- 所有跳转都经过 `Navigation/NavigationService.JumpToAsync(location, reason)`，统一写入导航历史（Alt+← / Alt+→）并记录日志 `nav.jump`。

## IntelliSense（M3，§7.5–§7.7）

都是 `IntelliSense/` 下的 MEF 组件，只对 `C/C++` 内容类型、UE 解决方案生效；状态与开关集中在 `IntelliSenseContext`（是否已接管、补全范围、会话级“最近使用”）。RPC 封装在 `Engine/EngineClient.IntelliSense.cs`（超时、取消、状态，日志 `is.*` + `rpc.*`）。

| 功能 | 实现 | 说明 |
| --- | --- | --- |
| 补全 | `CompletionSource.cs`（`IAsyncCompletionSource`）、`CompletionManagers.cs`（条目管理器 + 提交管理器） | 触发判定 `Logic/IntelliSense/CompletionTriggers.cs`；本地重排 `CompletionRanking.cs`；提交时 `.`↔`->`（`AccessorFixes`）；`IsIncomplete` 时 VS 随输入重查。未接管时 `CompletionScope=Auto` 只提供 UE 增强项 |
| Quick Info | `QuickInfoSource.cs`（`IAsyncQuickInfoSource`） | VS 原生 `ContainerElement` / `ClassifiedTextElement`，声明着色用语义分类（`DeclarationTokenizer`），宏展开可折叠；未接管时只追加 UE 部分 |
| 参数信息 | `SignatureHelp.cs`（`ISignatureHelpSource` + 每视图控制器） | `(` `,` 触发，Ctrl+Shift+Space 手动；异步取数后再打开会话，当前参数随光标本地计算 |
| 语义着色 | `SemanticClassifier.cs`、`SemanticClassificationFormats.cs`、`SemanticColors.cs` | 21 个 “UEToVS …” 分类；可见区 ± `SemanticColoringMarginLines` 行，`VisibleRangePlanner` / `SemanticLineStore`；主题切换只改仍是默认值的颜色 |
| 键位接管 | `IntelliSenseCommandFilter.cs` | 已接管时 Ctrl+Space / Ctrl+J / Ctrl+Shift+Space / Ctrl+K,I 与悬停直接由 UEToVS 处理（F12 见 `Editor/GoToDefinitionCommandFilter.cs`） |
| 命令 | `IntelliSenseFeature.cs`（0x0600–0x06FF，不进菜单） | 导入 VA 配色（`.vssettings` → 字体和颜色存储）、切换语义着色（会话级）、恢复默认语义配色 |

新增设置（`HostSettings` 末尾）：`CompletionEnabled`、`CompletionScope`（Auto / UeOnly / All）、`CompletionOnIdentifier`、`CompletionAccessorFix`、`CompletionMaxResults`、`QuickInfoEnabled`、`SignatureHelpEnabled`、`SemanticColoring`、`SemanticColorOverrides`（如 `Field=#DAD085; Functions=#DCDCAA`）、`SemanticColoringMarginLines`。

所有命令都注册为 `IAction`（`Logic/Actions`），菜单只是转发；Find Action / Alt+Enter 将列出同一批动作。菜单文字在 VSCT 中是英文默认值，运行时按语言替换（`TextChanges`）。

## 设置（工具 > 选项 > UEToVS）

- **General（常规）**：日志级别（应用后立即对宿主和引擎生效）、引擎路径覆盖、UE 根目录、请求超时、是否接管 C++ IntelliSense、接管范围（Global / UnrealSolutionOnly）、是否同时禁用浏览数据库、是否关闭 VS C++ 后台代码分析（默认关）。
- **Appearance（外观）**：UI 组件库的 `AppearanceSettingsPanel`，修改即实时预览；确定/应用时提交并以 `AppearanceSettings.ToStorageString()` 文本保存在 VS 用户设置存储（`UEToVS\Appearance`），取消时撤销预览。包加载时读取并应用到 `ThemeManager`。
- **Logging（日志）**：保留天数（默认 7）、总大小上限（默认 200 MB）、打开日志文件夹。

三层覆盖（§10.3）：内置默认 < 用户设置（VS 设置存储）< 解决方案设置 `<解决方案>\.uetovs\settings.json`（只读，团队共享）。示例：

```json
{
  "log": { "level": "Debug", "retentionDays": 3 },
  "intelliSense": { "takeover": true, "mode": "UnrealSolutionOnly", "disableDatabase": false },
  "engine": { "unrealEngineRoot": "D:/UE_5.4", "requestTimeoutSeconds": 30 }
}
```

出于安全考虑，`engine.path`（会让 VS 启动另一个可执行文件）**不能**在解决方案设置中指定。未知键与非法值会记 Warn 日志。

## 日志在哪里

- `<解决方案目录>\.vs\UEToVS\Logs\host-<时间>-<pid>.log`（VS 进程）与 `engine-*.log`（引擎进程），JSON Lines，字段见设计文档 §10.5。
- 打开 UE 解决方案之前（或非 UE 解决方案）写到 `%LocalAppData%\UEToVS\NoSolution\Logs\`。
- “输出 > UEToVS”窗格同步显示 Info 以上的可读版本。
- 会话头 `session.start` 包含扩展版本、协议版本、VS 版本/版本号/版次/语言、OS、解决方案、UE 工程、引擎路径、设置、功能开关、IntelliSense 接管状态。
- 每 60 秒 `resource.sample`（devenv 私有内存、自加载以来的增量、托管堆、GC、线程）；每 5 分钟 `perf.summary`。
- 宿主的所有命令、事件、后台任务都经过 `Safe.Execute / Safe.RunAsync` 包装，异常一律写日志；UI 线程入口以 `ui.*` 计时，超过 16 ms 记 `ui.*.slow`。

## IntelliSense 接管与恢复（§7.5）

**接管**（打开 UE 解决方案且设置开启时自动执行，也可用命令）：

1. 通过 `DTE.Properties["TextEditor", "C/C++ Specific"]` 读取当前值。属性名随 VS 版本不同，代码中每项设置有一组候选名（见 `Logic/IntelliSense/CppIntelliSenseSettings.cs`），日志 `intellisense.properties` 会列出本机实际存在的全部属性名，`intellisense.property.*` 记录每项“已修改 / 已是目标值 / 不存在 / 失败”。
2. **先**把要改的原始值写入 `%LocalAppData%\UEToVS\intellisense-backup.json`（VS 的这些设置是按用户全局的，所以备份也是按用户的），备份写失败则一项都不改。同一属性只记录第一次的原值，重复接管不会覆盖。
3. 再把以下设置改为关闭：Disable IntelliSense、自动快速信息、成员列表、错误报告、错误波浪线、语义着色、#include 自动完成、外部依赖项文件夹，以及（可选，默认开）Disable Database。用户自己早已关闭的项不会记入备份，恢复时也不会被打开。
4. 第一次接管时在 InfoBar 提示“UEToVS 已接管 C++ 感知，已关闭 VS 自带 IntelliSense”，带“恢复”链接。
5. 检测到 Visual Assist 时记日志并提示二选一（UEToVS 不修改 VA 的设置）。

**范围**：Global（默认，插件启用期间始终关闭）；UnrealSolutionOnly（关闭 UE 解决方案时自动恢复）。关闭“接管”设置会立即恢复。

**恢复**：命令“恢复 VS 自带 C++ IntelliSense”按备份逐项写回原值；全部成功后删除备份，失败项保留以便重试。

**卸载前请先执行恢复。** VSIX 卸载时无法运行代码；如果已卸载，请手动恢复：工具 > 选项 > 文本编辑器 > C/C++ > 高级，把 “禁用 IntelliSense”、“禁用自动快速信息”、“禁用错误报告”、“禁用波浪线”、“禁用语义着色”、“禁用外部依赖项文件夹”、“禁用数据库” 等改回 False（备份文件 `%LocalAppData%\UEToVS\intellisense-backup.json` 中记录了原值）。

## 关闭 VS C++ 后台代码分析

VS 每次打开或保存 .cpp 都会在后台用编译器（/analyze）完整分析整个编译单元，也就是任务中心里的“C++ 后台 Code Analysis”。UE 源文件会展开数千个头文件，单个文件常要几十秒。设置“关闭 VS C++ 后台代码分析”（默认关，独立于 IntelliSense 接管；解决方案设置键 `intelliSense.disableBackgroundCodeAnalysis`）开启后：

1. 打开 UE 解决方案时，把 工具 > 选项 > 文本编辑器 > C/C++ > 高级 > Code Analysis 下的 “Disable Background Code Analysis” 设为 True（DTE 属性候选名见 `CppIntelliSenseSettings.BackgroundCodeAnalysis`）。
2. 原值先备份到 `%LocalAppData%\UEToVS\code-analysis-backup.json`，日志事件 `codeAnalysis.*`。
3. 关闭此设置即写回原值并删除备份。手动“分析 > 运行代码分析”不受影响。

## 引擎进程

- 启动 `<扩展目录>\Engine\UEToVS.Engine.exe --pipe <name> --solution-dir <dir> [--project <uproject>] [--engine <root>] --parent <devenv pid> --log-level <level>`（可在设置中覆盖路径），需要 .NET 8 运行时。
- 命名管道 `ProtocolInfo.PipeName(devenv pid, 解决方案目录)`，StreamJsonRpc + MessagePack（`ContractlessStandardResolver`，`UntrustedData`），长度前缀分帧，首个请求为握手（协议版本不一致则提示重新安装）。
- 崩溃自动重启（指数退避，10 分钟内最多 3 次），心跳 2 次无响应则重启；超过次数后提示并降级。所有请求有超时与取消，全部在后台线程执行。
- 引擎状态推送显示在状态栏（索引进度）。VS 构建期间通知引擎暂停后台索引；性能面板可手动暂停/恢复。

## 目录

| 目录 | 内容 |
| --- | --- |
| `Logic/` | 纯逻辑（无 VS 类型），由 `tests/UEToVS.Host.Logic.Tests` 以链接文件方式编译和测试 |
| `Diagnostics/` | 日志生命周期、输出窗格、`Safe` 包装与 UI 线程看门狗 |
| `Engine/` | 引擎客户端（`EngineClient.Navigation.cs`：导航 RPC 的超时、取消、降级与日志） |
| `Editor/` | MEF 编辑器适配器：Ctrl+点击、引用高亮标签器、导航栏 margin、F12 拦截、活动视图 / CaretRequest / 弹窗锚点 |
| `Navigation/` | 导航功能：转到定义、查找引用、类型层次、成员列表、`NavigationService`（统一跳转 + 历史）、动作与命令注册 |
| `IntelliSense/` | DTE 设置读写、接管服务、VA 检测；M3 补全 / Quick Info / 参数信息 / 语义着色 / VA 配色导入 |
| `Options/` | 选项页与设置合并 |
| `Performance/` | 性能采样与工具窗口 |
| `Shell/` | GUID/命令 ID、命令路由、InfoBar、状态栏、构建监视 |
| `Integration/` | UI 组件库接入（`HostPackage.InitializeIntegrations`）：外观页、Find Action 命令面板、组件画廊、性能面板、弹窗尺寸存储（VS 用户设置存储 `UEToVS\Popups`）；VS 级通知仍用 InfoBar。性能数据到面板的映射是纯逻辑 `Logic/Performance/PerformanceViewMapper.cs`（有单元测试） |

## 构建

- Windows：`msbuild src\UEToVS.Host\UEToVS.Host.csproj /restore`，生成 `.vsix`；若存在 `src\UEToVS.Engine`，会自动发布到 VSIX 内的 `Engine\`（`/p:SkipEnginePublish=true` 可跳过）。
- 其他系统：`dotnet build src/UEToVS.Host` 只编译 C#（跳过 VSCT 与 VSIX 打包）；`dotnet test tests/UEToVS.Host.Logic.Tests` 运行纯逻辑测试。
