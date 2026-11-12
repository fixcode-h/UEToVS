# 更新日志

格式说明（发布脚本依赖这个格式）：

- 每个版本一节，标题为 `## vX.Y.Z`，最新的在最上面。合并到 main 时，CI 读取最上面一节：如果这个版本还没有发布，就自动打包并发布 Release，发布说明由这一节生成。
- 节内用 `### 新功能`、`### 改进`、`### Bug 修复`、`### 注意` 等小标题，条目写成 `- **短标题** — 说明`。
- 改了 `src/` 的 PR 必须在最上面加一个新版本节（PR 检查会验证），这样每次合并都会出一个带更新日志的包。

## v0.4.6

### 改进
- **VS 字体设置分组** — 设置 > 外观 里“由 UEToVS 管理 VS 字体”下的 9 个类别分成“代码类窗口”（文本编辑器、输出、查找结果、命令 / 即时窗口、打印机）和“界面类窗口”（环境、语句完成、编辑器工具提示）两张卡片；每个类别的字体和字号放在同一行，行数减半。搜索“字号”时字号仍单独显示
- **字体修改实时生效** — 在设置窗口里改字体或字号时立即显示效果（UEToVS 界面马上变，VS 自己的字体停顿 0.25 秒后写入），不用先点“应用”；点“取消”或直接关闭窗口会恢复成已保存的字体。刚安装的内置字体仍要重启 VS 才在 VS 自己的窗口里生效
- **VS 字体“当前状态”** — “由 UEToVS 管理 VS 字体”下新增一行状态：刚为当前用户安装、要重启 VS 才能显示的字体（在这之前编辑器、菜单等会悄悄换成别的字体），以及 VS 找不到的字体都会列出来
- **UEToVS 窗口里的文字更清晰** — 设置窗口等 UEToVS 自己的窗口和弹窗改用和 VS 一样的按像素排字（Display 模式），12–13 号的中文不再发虚
- **内置中文字体加入半粗字重** — 思源黑体 Noto Sans SC 新增 SemiBold（同样是常用字子集）：标题、分组名、“确定”等主按钮要的就是半粗，之前没有这一档，汉字被系统合成加粗、笔画糊在一起
- **关闭管理时 VS 字体行置灰** — 关掉“由 UEToVS 管理 VS 字体”后各类别的字体和字号不会写进 VS，现在这些行变灰不可编辑，避免改了以为没生效

## v0.4.5

### 新功能
- **检查更新** — 设置 > 常规 > 更新 新增“检查更新”按钮：查询 GitHub 上的最新版本，有新版本时下载安装包并启动 VS 的扩展安装程序（VSIXInstaller），关闭 VS 后完成安装；已是最新时直接提示（日志 `update.*`）

- **Agent / API：输入模型名时筛选接口返回的模型** — 在“输入模型名筛选，回车添加”里打字，下面的列表只显示匹配的模型（不分大小写，多个词都要匹配，忽略 - . _ 等分隔符，例如 v41 能找到 deepseek-v4.1-flash）；“添加全部”变成“添加匹配的 N 个”；回车时只有一个匹配就直接添加它，否则按输入的名字添加

- **Quick Info 宏展开更好读** — 分步显示“1 展开一层 → 2 完全展开 → 3 计算常量后”，每步是带底色的等宽代码块；去掉不改变含义的多余括号（`((((o))->tt_)` → `o->tt_`），整数常量直接算出结果（`((2) | ((0) << 4))` → `2`），原式保留在上一步；很长的展开（GENERATED_BODY 等）按 `;` 和大括号分行缩进；右上角“复制”复制最终结果。可能是类型转换的括号一律保留

- **字体设置（设置 > 外观 > 字体）** — 插件自带三款开源字体（OFL 许可）：IBM Plex Sans（英文）、思源黑体 Noto Sans SC（中文，常用字子集）、Maple Mono NL（代码，无连字，自带中文，汉字正好两个字符宽，代码里的中文注释和字符串能对齐；只带常规字重，粗体由系统合成）。英文字体、中文字体、代码字体可分别选内置字体、“跟随 VS”或系统已安装的字体，默认就是这三款；UEToVS 界面和代码预览立即生效
- **代替 VS 自己的字体设置** — 新增“由 UEToVS 管理 VS 字体”（默认开），把 VS“字体和颜色”里的 9 个类别放进插件设置：文本编辑器默认 Maple Mono NL 10pt；输出、查找结果、命令 / 即时窗口用代码字体；环境、语句完成、编辑器工具提示用英文字体；打印机默认不改。每个类别可单独选字体和字号。内置字体自动为当前用户安装（不需要管理员）；第一次修改前把原字体备份到 %LocalAppData%\UEToVS\vs-fonts-backup.json，关掉这个开关或设为“不修改”就恢复原样（日志 `font.*`）。刚装的字体有时要重启 VS 才能在 VS 自己的窗口里生效

- **路径列表：Lua 脚本路径可以添加多个文件夹** — “Lua 脚本路径”改成列表：右上角“添加文件夹…”弹出系统的文件夹选择框；每个文件夹一行，显示“相对 .uproject / 绝对路径”和“文件夹不存在”提示，⋯ 换一个文件夹、✕ 删除，双击（或 F2）直接编辑。项目里的文件夹自动存成相对 .uproject 的路径（如 Content/Script），换电脑也能用；旧配置里用 ; 分隔的路径自动拆成多行。这是通用组件，以后其他多路径设置也用它。引擎程序路径、Unreal Engine 根目录、共享引擎索引位置等单个路径的设置加了“浏览…”按钮

- **Quick Info 注释翻译** — 悬停提示里的英文注释自动翻译，默认用谷歌翻译、双语对照（每段原文下面跟译文，译文前有一条细的强调色竖线）。翻译在后台进行：先显示原文，译文到了再补上，不阻塞悬停；硬换行的段落先合并再翻译，@param、列表项和代码行分开处理（代码行不翻译）。译文缓存在 %LocalAppData%\UEToVS\Translation\cache.jsonl（内存最多 5000 条，所有解决方案共用），看过的注释下次直接显示；相同内容同时只发一次请求，最多 4 个请求并发，单次超时 8 秒，失败后 30 秒内不再请求，悬停时直接显示原文和失败原因。未接管 VS IntelliSense 时（VS 自己显示注释）只补充译文（日志 `translate.*`）
- **右键“翻译选中内容”** — 编辑器右键菜单新增“翻译选中内容”（Ctrl+Shift+Alt+U, T），在选中位置弹出和 Quick Info 一样的提示窗显示译文，同样遵循双语 / 只显示译文的设置；没有选中时翻译光标所在的整段注释。所有文本编辑器都可用，移动光标、编辑或切走焦点时关闭
- **翻译设置（设置 > 代码感知 > 注释翻译）** — 开关、显示方式（双语对照 / 只显示译文）、翻译服务（谷歌翻译 / 微软翻译 / MyMemory，都是免费接口，不需要密钥）、翻译成（自动跟随界面语言，或简体中文、繁體中文、English、日本語、한국어）、谷歌翻译服务地址（可换成镜像）、清空翻译缓存。微软翻译使用 Edge 网页翻译的接口，访问不了谷歌时可以切换过去

### 改进
- **设置窗口换成 Claude Code 风格** — 暖灰配色（深色 / 浅色都有），陶土色强调色 #D97757；左侧导航带图标，选中项有强调色竖条，搜索框显示 Ctrl F；每页顶部是衬线字体的大标题和一句说明；同一组的设置放进一张圆角卡片，行之间用细分隔线，说明文字改成更淡的小字；弹窗圆角 12、控件圆角 8；底部栏显示“● N 项未应用”和“恢复本页默认”。外观页按设计图重排：“风格与密度”（标准 / 紧凑 / 宽松、字号和间距带 % 单位、三个圆角合成一行）、“颜色”（强调色色块输入框、匹配高亮改成可切换的小标签、徽章颜色三列网格），右侧预览只有一条分隔线。Agent / API 页：每个 API 一组设置行，模型一行一个（启用、模型名、“工具 / 图片”标签、“上下文 1M”、✕），下面是筛选框和接口返回的模型。新配色是默认值，设置 > 外观 可以切回跟随 VS 的旧配色；高对比度主题不受影响
- **“关闭 VS 后台代码分析”默认开启** — 打开 UE 解决方案时默认关闭 VS 的 C++ 后台代码分析（修改前备份，可在设置里关掉这个开关恢复）
- **Agent / API 转发服务默认端口改为 11434** — 和 VS“添加 Ollama 提供程序”里预填的 http://localhost:11434 一致，手动添加时不用改地址；“应用”时仍会自动把模型写进 VS。11434 已被本机真实的 Ollama 占用时自动改用 47811，并在页面上提示。旧配置里的默认端口 47811 会自动迁移，自己改过的端口保持不变
- **Agent / API 模型的上下文默认改为 1M（一百万 token）** — 新加的模型默认 1M；旧配置里还是原默认值 128K 的模型自动改为 1M，自己改过的保持不变。上下文框改成可直接输入：100 万以下用 K、100 万及以上用 M 显示（128K、1M、1.5M），可以写 1M、1000K、1.5M，只写数字按 K 算
- **MCP 设置移到“Agent / API”页** — “启用 MCP 服务”和“写入 .mcp.json”从“常规”移到“Agent / API”页顶部
- **UEToVS 工具栏去掉“后退 / 前进”按钮** — VS 自带这两个按钮；Alt+← / Alt+→ 和导航历史不变
- **接管 IntelliSense 时同时关掉浏览数据库的更新** — 除“Disable Database”外，再关闭“Disable Database Updates”“Disable Automatic Updates”（同样先备份，撤销接管时恢复），避免 VS 继续“正在更新 C/C++ IntelliSense 和浏览信息 / 正在分析包含文件”
- **“仅 UE 解决方案”模式关闭 UE 解决方案时不再立即恢复 VS IntelliSense** — 改为打开非 UE 解决方案时恢复。这样下次打开 UE 解决方案时，VS 的 C++ 解析和浏览数据库扫描一开始就是关的，不会先扫一阵再被关掉
- **输入框文字不再离左边一大截** — 输入框组件把内边距算了两次（WPF 文本框自己会加一次），光标和文字离左边约 20 像素，窄输入框里的数字还会被截掉（如上下文 K 的 128 只显示 12）。现在内边距只算一次，光标、文字和占位文字都从同一位置开始；输入框里的滚动容器也不再受 VS 全局样式影响。设置页、Agent / API、重命名、搜索框、颜色值等所有用这个组件的地方一起修好
- **数字框的 −/+ 按钮不再被裁掉** — 数字框最小宽度 = 输入框 + 两个按钮，外面给的宽度不够时也完整显示；数字改为靠左，和其他输入框一致
- **密钥输入框和其他输入框外观统一** — 同样的圆角、高度、内边距和聚焦下划线
- **路径设置的占位文字改为“默认”** — 原来的全角括号“（默认）”左边自带半个字的空白，看起来没对齐
- **UEToVS 窗口的按键保护再补一处** — 窗口激活但 WPF 焦点暂时为空时，编辑器命令同样留在窗口里，不会落到代码编辑器
- **打开文件时的 Build.cs 包含检查不再反复搜索** — 原来每检查一个文件都要为当前模块能看到的 175～214 个模块各发一次文件名搜索来找 Build.cs（一次 28 分钟的会话里约 4200 次，刚建完索引时单次最慢 1.5 秒，引擎 CPU 一分钟多用 25 秒）。现在模块的 Build.cs 路径和包含文件的查找结果在检查之间共用缓存：Build.cs 被删除或移动时重新查找，查不到的模块和包含文件 60 秒后重查，重启引擎时清空
- **首次建索引更快：索引段文件并行写入** — 原来解析线程大部分时间在等单线程写段文件（一次 8 万文件的建索引中 54 个段、每段约 220 ms，合计 16 秒，基本等于整个耗时）。现在多个线程并行编码和写文件，按原顺序发布，结果和原来完全一致；写入线程数为 C++ 索引线程数的一半（1～4 个），在途批次有上限（最多多 3 批），出错或取消时丢弃未发布的段
- **建完索引后立即回收内存** — 一次解析超过 5000 个文件后做一次压缩 GC。原来引擎建完索引后可能在 877 MB 停留十几分钟，下一次完整 GC 后才降到 470 MB（日志 `index.trim`）
- **设置窗口打开更快** — 每次打开时设置页不再构建两遍
- **诊断包对所有文件脱敏** — 日志和 `Index/manifest.json` 里的用户目录和用户名也替换为 `%USERPROFILE%` / `<user>`，原来只处理了 settings.json
- **VS 自身的后台任务异常不再记为错误** — 堆栈里没有 UEToVS 代码的“未观察到的任务异常”（VS 的 WithTimeout、遥测、管道等）改为警告 `process.unobserved_task.foreign`，每次运行最多记 20 条；UEToVS 自己的仍记为错误

### Bug 修复
- **Alt+→ 在代码编辑器里不再触发“完成单词”** — VS 18 会保留“文本编辑器”作用域里 Edit.CompleteWord 的 Alt+→（UEToVS 写入删除后读回仍在），这个作用域比全局优先，所以在编辑器里按 Alt+→ 不会前进。现在在 C++ 编辑器里按 Alt+←/→ 触发这个命令时，会改为执行导航历史的后退/前进（日志 `nav.altArrow.redirect`）
- **默认快捷键不再每次启动都重新写入** — VS 拒绝删除冲突键时，UEToVS 自己的快捷键其实已经绑定好，但以前仍算失败，导致每次启动都重试（约 1.2 秒，还会多写一个备份文件）。现在这种情况记为已处理（日志 `boundButVsKeptKey`）
- **快捷键冲突不再误报** — VS 18 把退格键读回为拼错的 `Backsapce`，现在能识别，不会再每次报“写入与读回不一致”；资源/对话框编辑器等设计器作用域里的同名快捷键（如 VC 对话框编辑器的 Alt+←）不再算作冲突，也不会被删除
- **“关闭 VS 后台代码分析”在 VS 18 上生效** — 补上 VS 18.6 实际使用的属性名 `DisableCodeAnalysisInBackground`，同时补上 `DisableSemanticColoring`、`DisableDatabaseAutoUpdates`、`EnableQuickInfoToolTips`。`intellisense.properties` 日志现在记录完整的属性列表（原来 278 个只记了前 100 个）

## v0.4.4

### Bug 修复
- **在 Search Everywhere 里打字，字符跑进了代码** — 搜索窗口里的代码预览是一个 VS 编辑器视图，键盘焦点落到它上面时，VS 会把按键变成编辑器命令（输入字符、退格、方向键等）发给最后一个代码编辑器。现在预览不再接收键盘焦点；并且 UEToVS 窗口激活时，所有文本编辑器命令和 UEToVS 的编辑器快捷键（Alt+Enter、Ctrl+R, Ctrl+R 等）都留在窗口里：输入类按键转给窗口的输入框，其余不执行，不会再进入代码编辑器（日志 `shell.editRouter.keystroke`）

## v0.4.3

### Bug 修复
- **Search Everywhere 等弹窗打开时抽搐一下** — 弹窗关掉后是隐藏起来复用的，再打开时会先闪出上一次的画面（旧结果、旧预览），接着变透明、再往上滑入淡出，看起来像抽了一下；如果输入框带了选中的文字，旧结果还会在新结果出来时整体换掉。现在弹窗先在不可见状态下布局好再显示：Search Everywhere 和查找操作等新查询结果出来（最多等 0.1 秒）后才显示，只淡入、不再滑动；打开时同一个查询也不再连跑两次（预览不会再重新定位一次）。查找引用、类型层次等其它弹窗也不会再闪出上一次的画面

## v0.4.2

### 新功能
- **Alt+Enter 上下文操作** — 接管 VS C++ IntelliSense 后，Alt+Enter 按光标位置列出可用的 UEToVS 操作（生成实现 / 声明、重写虚函数、添加 #include、重命名），同样的操作也出现在 VS 灯泡菜单和“查找操作”里；未接管或非 C++ 文件时 Alt+Enter 仍执行 VS 自带的快速操作。操作由可注册的提供者生成，统一经 Action 中心执行并记录日志（`refactor.*`）
- **生成实现 / 生成声明** — 在头文件的函数声明上生成成对 .cpp 里的函数体，放在同一个类相邻成员的实现之后；UFUNCTION 的 Server / Client / NetMulticast / BlueprintNativeEvent 生成 `_Implementation`，Server + WithValidation 还生成返回 true 的 `_Validate`；在 .cpp 的成员定义上反向生成类里的声明。签名取自引擎解析，默认参数只留在声明里，virtual / override / static 只出现在声明里；一次撤销还原
- **重写虚函数** — 列出基类（含引擎类）里还没有重写的虚函数，按声明它的类分组，可多选；在类的 public 区生成 `override` 声明，在成对 .cpp 里生成调用 `Super::` 的实现（纯虚函数和接口方法不调用 Super）。默认快捷键 `Ctrl+Shift+Alt+U, V`（Rider 方案 `Ctrl+O`）
- **添加 #include** — 光标处类型所在头文件没有被当前文件直接或间接包含时，提供“添加 #include "Xxx.h"”，路径按 UE 习惯相对模块的 Public / Classes 目录，插入到 `.generated.h` 之前的 include 组里；随后检查 Build.cs 模块依赖，缺少时弹出添加依赖的横幅。间接包含只按索引里已有的 include 列表查找，最多 4 层、600 个文件，结果有缓存
- **重命名** — 重命名项目中的类型、函数、变量、UPROPERTY / UFUNCTION：用“查找引用”的结果生成预览，按文件列出每一处，可取消勾选；引擎和第三方文件中的引用只列出不修改，仅名称匹配的位置默认不勾选；所有文件的修改一次撤销还原。UCLASS / USTRUCT / UENUM / UPROPERTY / UFUNCTION 可选写入项目 Config/DefaultEngine.ini 的 `[CoreRedirects]`；类名与文件名一致时可同时重命名 .h / .cpp 并更新 #include。快捷键 `Ctrl+R, Ctrl+R`（Rider 方案 `Shift+F6`）
- **MCP：`get_reflection_info`** — 类/结构体的全部 UPROPERTY / UFUNCTION，说明符拆成标志、键值和 meta，标出复制（Replicated / ReplicatedUsing=OnRep_X）和 RPC（Server/Client/NetMulticast、Reliable、WithValidation），可按说明符（如 `BlueprintReadWrite` 或 meta 键）、属性/函数、只看复制过滤；UFUNCTION 附带同名 Lua 函数（UnLua 覆盖）
- **MCP：`get_module_info` / `suggest_include`** — 文件所属模块、模块类型（来自 .uproject/.uplugin，否则按目录推断）、插件、Build.cs Public/Private 依赖；给一个头文件（`GameFramework/Actor.h` 或路径）回答“它属于哪个模块、当前模块是否已能包含、应加到 Public 还是 Private 依赖”。`suggest_include` 给出类型应写的 UE 风格 `#include` 路径和需要的模块依赖
- **MCP：`search_config`** — 查 ini 键值（项目 Config 和项目插件，scope=all 加引擎 Config，带行号）、`UPROPERTY(config)` 与 `Default<Config>.ini [/Script/模块.类] 键` 的对应及当前值（ini 键也反查到对应 UPROPERTY）、项目代码里的控制台变量（`TAutoConsoleVariable` / `FAutoConsoleVariableRef` / `FAutoConsoleCommand`：名字、变量、帮助文本、位置）和 GameplayTag（ini 与 `UE_DEFINE_GAMEPLAY_TAG`）。ini 解析复用 Engine 的 ConfigCatalog；C++ 只读索引里出现这些标识符的文件，按文件时间戳缓存
- **MCP：`find_delegate_bindings`** — 函数被哪些 `AddDynamic` / `AddUObject` / `BindUObject` / `BindRaw` / `BindSP` / `BindUFunction`（含字符串名和 `GET_FUNCTION_NAME_CHECKED`）、`BindAction` / `BindAxis`、`SetTimer` 绑定，给出委托表达式、绑定方式和所在行；`Class::Func` 只保留指明该类的绑定。不改索引格式：成员指针绑定来自函数名的引用位置，字符串名绑定只扫描调用了 BindUFunction 等的文件
- **MCP：Lua ↔ C++ 关联** — `find_references` 查 UFUNCTION 时附带同名 Lua 函数定义（UnLua 同名覆盖），说明 Lua 调用点不在索引里
- **UE 规则波浪线（UHT / 网络复制 / 字符串）** — 在 v0.4.1 波浪线的基础上新增 7 条规则，仍然只报确定的问题：UE0004 `Server` / `Client` / `NetMulticast` / `BlueprintNativeEvent` / `BlueprintImplementableEvent` 的 UFUNCTION 被手写了本体（应写 `_Implementation`；重载、CustomThunk、条件编译、未保存的头文件不报）；UE0005 `ReplicatedUsing=OnRep_X` 但本类和基类都没有 `OnRep_X` 或它不是 UFUNCTION（类和所有基类完整解析时才报）；UE0006 本类的 Replicated 属性没有在当前文件的 `GetLifetimeReplicatedProps` 里 `DOREPLIFETIME*`（函数体里有循环、`#if`、未知宏、把 `OutLifetimeProps` 传给别的函数时不报）；UE0007 `RequestGameplayTag("A.B.X")` 的标签不存在（警告；只在父标签 `A.B` 已知时报，项目里有 GameplayTag 表、重定向或原生标签注册时整条规则跳过）；UE0008 `/Game/...` 资产路径不存在（警告；索引完成后才报，报之前再查一次磁盘，拼接、格式化字符串和文件夹不报）；UE0009 UPROPERTY 用了 UHT 不支持的类型（`TSharedPtr` 系列、`std::`、结构体/内置类型指针）；UE0010 UFUNCTION 参数用了这些类型，或 BlueprintCallable / BlueprintPure 参数用了 `int8/int16/uint16/uint32/uint64`。日志 `intellisense.diagnostics` 新增 `ueRules`（各规则命中数），跳过原因带 `ue000N:` 前缀
- **UE 代码模板（Live Templates）** — 行首输入缩写后按 Tab 展开，或在补全列表里选择：`uclass`、`ustruct`、`uenum`、`uinterface`、`uprop`、`upropr`（ReplicatedUsing + OnRep）、`ufunc`、`ufuncn`（BlueprintNativeEvent + _Implementation）、`userver`、`uelog` / `uelogf`、`logcat` / `logdef`、`uecheck` / `checkf` / `ensuremsg`、`dmdel` / `ddel` / `mdel` / `del` 委托、`cds`（CreateDefaultSubobject）、`adddyn`、`gbody`、`superbp`。展开后 Tab / Shift+Tab 在占位符间跳转，同名占位符联动修改，Enter 跳到结束位置，Esc 退出；类名默认取自文件名。设置“代码感知 > 代码模板”可关闭；日志 `snippets.expand`
- **Postfix 模板** — 在表达式后输入 `.if`、`.notnull`、`.null`、`.ifvalid`、`.for`、`.return`、`.var`、`.check`、`.ensure`（表达式在行首时）以及 `.cast`（→ `Cast<T>(expr)`）、`.isvalid` / `.valid`（→ `IsValid(expr)`）、`.not`、`.par`、`.text`，从补全列表选择后把表达式包进对应代码；只做行内文本分析，不请求 Engine
- **Generate：构造函数 / GetLifetimeReplicatedProps / RPC** — 三个新命令（编辑器右键 UEToVS 菜单、Find Action，默认键 Ctrl+Shift+Alt+U 再按 K / Y / W），在头文件的类里执行：生成构造函数声明和 .cpp 定义；按类里的 Replicated / ReplicatedUsing 属性生成 `GetLifetimeReplicatedProps`（已有定义时只补缺少的 `DOREPLIFETIME`，并补 `#include "Net/UnrealNetwork.h"` 和头文件里的 override 声明）；光标在 Server / Client / NetMulticast 声明上时补 `_Implementation` / `_Validate`，在普通函数上时生成一个转发到它的 `Server…` RPC 三件套。头文件和 .cpp 各一次撤销；日志 `generate.run`
- **Smart Select（扩大 / 缩小选区）** — Shift+Alt+W 按语法逐级扩大选区：单词 → 字符串内容 → 字符串 → 参数 → 括号内容 → 括号 → 调用（含 `Obj->Func`）→ 语句 → 块内容 → 块 → 带 if / for / 函数签名的整块；Ctrl+Shift+Alt+W 按原路缩回。只在按键时计算（超大文件只看选区附近 200 KB）；日志 `edit.smartSelect`
- **Natvis：补充 UE 常见类型** — VSIX 自带调试可视化（NativeVisualizer）：`FGameplayTag`、`FGameplayTagContainer`、`FTopLevelAssetPath`、`FSoftObjectPath`、`TSoftObjectPtr` / `TSoftClassPtr`、`FPrimaryAssetId` / `FPrimaryAssetType`、`FTimerHandle`、`FDataTableRowHandle`、`FGameplayAttributeData`、GAS 句柄。引擎 Unreal.natvis 已有的类型（TArray、FString、FName、TMap、UObject、TObjectPtr …）不重复定义
- **着色器文件（.usf / .ush）着色与 #include 跳转** — HLSL 关键字、向量 / 矩阵 / 资源类型、内建函数、`SV_` 语义、注释、字符串、数字和预处理指令按 VS 标准颜色显示（只着色可见行，逐行词法分析）；在 `#include "/Engine/…"`、`"/Project/…"`、`"/Plugin/<名>/…"` 或相对路径上按 F12 打开对应文件（`/Plugin/` 只在按键时查找一次同名 .uplugin）。日志 `shader.include.goto`
- **.uproject / .uplugin / .ini 补全与跳转** — 描述文件里补全模块 `Type`、`LoadingPhase`、平台 / 目标 / 配置的允许和禁止列表；在 `Modules` 的 `Name`、`AdditionalDependencies` 上按 F12 打开对应 `.Build.cs`，在 `Plugins` 的 `Name` 上打开 `.uplugin`。ini 里 `[/Script/模块.类]` 下的行首补全该类（含基类）的 `Config` / `GlobalConfig` 属性名，F12 在节标题上跳到类、在键名上跳到属性声明。全部只在按键时计算，结果按节缓存 30 秒；日志 `projectFiles.goto`

### 改进
- **MCP 提示与说明** — 去掉没有数据支撑的 `find_blueprint_usage` 提示词（`explain_ue_class` 改用 `get_reflection_info`）；`uetovs://guide` 增加“索引未就绪时如何退回 grep”；服务说明列出新工具
- **共享的 .mcp.json 不再含本机路径** — “写入 .mcp.json 到项目根目录”改为写 `cmd /c %LOCALAPPDATA%\UEToVS\Mcp\uetovs-mcp.cmd --solution-dir <相对 .mcp.json 的路径>`：启动器脚本由每台装了 UEToVS 的机器在打开 UE 解决方案时自动更新（指向当前 VSIX 里的 UEToVS.Mcp.exe，找不到时在 `%LOCALAPPDATA%\Microsoft\VisualStudio\*\Extensions` 下查找），环境变量由 cmd 自己展开，不依赖客户端是否支持；提交到版本库后同事机器上也能直接用。旧版本写的绝对路径条目在打开解决方案时自动迁移，用户自己加的参数和 `env` 保留；`.vs\mcp.json`（VS 自己读，每人一份）仍写本机路径。MCP 服务的相对 `--solution-dir` 找不到解决方案时回退为从工作目录向上查找
- **关闭解决方案时保存未落盘的索引改动** — Engine 正常退出时先把内存里尚未压实的增量（最近编辑过的文件）写成一个小的附加段（只写增量，不重写整个工程段，上限 5 秒），离线 MCP 立即能查到最新结果；宿主停止 Engine 时最多等 7 秒（后台进行，不阻塞 VS 关闭）。日志 `index.shutdown.flush`（文件数、耗时）/ `index.shutdown.flush.timeout`、`index.delta.persisted`

### Bug 修复
- **每次启动都“正在从 X.uproject 中扫描 Blueprint”** — 这是 VS 自带的 Unreal Engine 集成（Blueprint Support），不是 UEToVS。设置 > 常规新增“启用 VS 自带的蓝图扫描”，默认关闭：打开 UE 解决方案时 UEToVS 找到 VS 的这个开关并关掉（修改前备份，打开设置即恢复），下次启动 VS 生效。找到哪些开关记在日志 `vsBlueprint.discovered`；没找到时设置页会提示手动在 项目 > Configure Tools for Unreal Engine 里关闭
- **UE 反射宏转到定义报“找不到定义（ue-macro）”** — `GENERATED_BODY`、`UENUM`、`UCLASS` 等现在跳到宏的 `#define`（如 ObjectMacros.h）；`GENERATED_BODY` 这类宏还会列出 UHT 在 `.generated.h` 里为这一行生成的展开。
- **在 UEToVS 窗口里粘贴，内容却进了代码** — 设置窗口、Search Everywhere 等 UEToVS 弹窗是独立的浮动窗口，VS 会先把 Ctrl+V / Ctrl+C / Ctrl+X / Ctrl+Z / Ctrl+Y / Ctrl+A / Delete 当成自己的编辑命令，发给最后一个代码编辑器（例如在“Agent / API”页粘贴密钥，结果粘到了代码里）。现在 UEToVS 窗口处于激活状态时，这些命令只作用于窗口里当前的输入框，不会再进入代码编辑器（日志 `shell.editRouter.exec`）
- **在解决方案资源管理器中定位后文件不居中** — VS 只把定位到的文件滚到刚好可见（常在列表最底部）。现在展开并选中后，会把它滚到视图中间；本来就在中间三分之一以内时不跳动（日志 `solutionExplorer.center`）
- **压实多段工程索引时丢文件** — 工程段不止一个且同一文件的旧副本在较早的段里已被覆盖时，压实会把较新段里的这个文件当成“压实期间被删除”而丢掉；现在只把压实期间新出现的删除算作删除

## v0.4.1

### 新功能
- **GameplayTag 补全** — 在 `RequestGameplayTag(FName("…"))` / `TEXT("…")`、`UE_DEFINE_GAMEPLAY_TAG(…, "…")`、`meta=(Categories="…")` 的字符串里按 `.` 分层补全（输入 `Ability.` 列出下一层），右侧显示 DevComment；标签来自项目和项目插件的 Config/DefaultGameplayTags.ini、Config/Tags/*.ini 以及代码里的 `UE_DEFINE_GAMEPLAY_TAG*`（含当前未保存的文件）
- **资产路径补全** — `ConstructorHelpers::FObjectFinder<T>` / `FClassFinder<T>`、`FSoftObjectPath`、`LoadObject<T>(nullptr, …)` 等的字符串以及任何以 `/Game/` 等挂载点开头的字符串里，按 `/` 逐级补全文件夹和资产，插入 `Name.Name`（类查找插入 `Name.Name_C`）；`UWorld` 只列地图，其它类型按 UE 命名约定（SM_、T_、M_、BP_ …）把更可能的资产排在前面
- **UFUNCTION 名补全** — `BindUFunction` / `AddUFunction` / `CreateUFunction` 的函数名字符串补全目标对象类型（`this` 时为所在类）的 UFUNCTION，`Obj->FindFunction(TEXT("…"))` 补全 `Obj` 类型的 UFUNCTION；非 UFUNCTION 的成员不列出
- **ini 键补全** — `GConfig->GetString/GetBool/GetInt…(TEXT("Section"), TEXT("Key"), …, GGameIni)` 的 section 和 key 补全，来源为项目 Config/*.ini，按 `GGameIni` / `GEngineIni` 等过滤对应文件；普通字符串（日志文本等）不会弹出列表
- **#include 补全后立即检查模块依赖** — 选中的头文件属于当前模块 Build.cs 未依赖的模块时，提交后马上弹出“添加到 Public/PrivateDependencyModuleNames”横幅，不必等保存
- **高置信度错误波浪线** — 接管 VS C++ IntelliSense 后，UEToVS 提供唯一的错误提示，只报确定的问题（宁可漏报，不误报）：UE0001 未知成员（`expr.Name` / `expr->Name`，且类型和所有基类都已完整解析；模板、宏、auto、未索引或模板基类、重载返回类型不一致等一律不报）、UE0002 `#include` 的头文件所在模块不在 Build.cs 依赖中（警告，消息里给出模块名）、UE0003 UCLASS / USTRUCT / UINTERFACE 缺少 GENERATED_BODY()。不因 `.generated.h` 缺失报错，引擎代码不检查，正在输入的单词和未保存头文件里声明的类型不报。只检查可见区域 ±50 行，编辑后约 500 ms 刷新；设置“代码感知 > 波浪线”可关闭。日志 `intellisense.diagnostics` 记录各规则命中数和“不确定而跳过”的数量及原因

## v0.4.0

### 新功能
- **Agent / API：自定义 API 的模型接入 VS 自带聊天** — UEToVS 设置新增“Agent / API”页（布局参考 AgentPanel）：每个 API 填名称、地址、密钥、密钥的发送方式（Authorization: Bearer / x-api-key）、并发上限，可“测试连接”“从接口获取”模型，在模型列表里勾选要用的模型，并设置是否支持工具调用、图片和上下文大小，也可以手动输入模型名添加。勾选的模型会自动写入 VS Copilot 聊天的自定义模型，重新打开聊天窗口后就能在模型选择器里选到
- **本地转发服务** — UEToVS 在本机 127.0.0.1（默认端口 47811，可改）起一个转发服务，VS 聊天把它当 Ollama 提供商来用；请求按模型转到你配置的 API，换上真实模型名和密钥后流式转发，API 返回的错误原样显示在聊天里。只接受本机非浏览器的请求，可按 API 限制并发

### 改进
- **每个命令都有默认快捷键** — 之前很多命令（最近位置、上/下一个编辑位置、文件结构、Search Everywhere、转到类型/文件等）没有默认键。现在全部补齐：Search Everywhere Ctrl+T，转到类型 Shift+Alt+T，最近文件/位置 Shift+Alt+E / Shift+Alt+L，上/下一个编辑位置 Ctrl+Shift+Backspace / Ctrl+Shift+Alt+Backspace，文件结构 Ctrl+Shift+Alt+M，导航到 Shift+Alt+N，转到资产 Shift+Alt+B，相关项 Shift+Alt+R，书签 Ctrl+F2 / Ctrl+Shift+F2；设置、日志、诊断、生成工程文件等工具命令统一为 Ctrl+Shift+Alt+U 再按一个字母（如 U, S 打开设置）。Rider / VS 预设里没有对应键的命令沿用这套默认
- **快捷键冲突时 UEToVS 优先** — 默认键、套用预设和在快捷键页修改时，如果 VS 自带命令占用了同一按键（含和弦前缀、编辑器范围遮挡全局范围的情况），会把该按键从 VS 命令上移除，让 UEToVS 生效；改动前自动备份，“还原上次备份”可一键恢复。用户自己给 UEToVS 命令设置的键不会被覆盖
- **老用户重新应用默认键** — v0.3.1 / v0.3.2 因按键被 VS 占用而跳过的默认键，升级后启动时会重新应用一次（日志 `keymap.apply` 记录移除了哪些 VS 绑定）

### 注意
- **密钥只存在本机** — 密钥用 Windows DPAPI 按当前用户加密保存在 `%LocalAppData%\UEToVS\agent-keys.dat`，不写进 VS 的配置、日志或解决方案；VS 只知道本地转发地址
- **VS 侧依赖内部格式** — VS 没有给扩展注册聊天模型的公开接口，UEToVS 写的是 VS 自己的自定义模型文件（第一次写入前备份为 `.uetovs-backup`）。目前只支持 OpenAI Chat Completions 兼容的 API。如果应用后模型选择器里没有这些模型，在 管理模型 > 添加模型提供程序 里选 Ollama、地址填转发地址添加一次。详见 docs/design/Agent-API.md

## v0.3.6

### 改进
- **Agent 优先用 UEToVS 搜代码和资源** — 重写 MCP 服务器的 instructions：明确要求在 Grep/Glob/读整个文件之前先用 UEToVS，按“要找什么 → 用哪个工具”列出全部工具（包括 `search_lua` / `search_lua_files` / `get_lua_outline`），并写清楚哪些情况仍该用 grep（字符串、注释、日志文本、.ini/.uplugin/.Build.cs 内容、Lua 函数的调用处）以及结果为空时先看 `index_status`。各工具描述开头加上“什么时候用它代替 grep/Glob”，方便 Claude Code 等按描述挑工具。instructions 控制在 2 KB 内，避免被客户端截断

## v0.3.5

### 新功能
- **索引 Lua 脚本，MCP 可以直接查找 Lua** — 设置 > 索引 > Lua 脚本 新增“Lua 脚本路径”（如 `Content/Script`，多个用 `;` 分隔，相对路径相对于 .uproject 所在目录）、Lua 索引状态和“索引 Lua 脚本”按钮。识别 `function M:Foo()`、`local function`、`M.Foo = function`、模块表及其字段、`class(...)` / `UnLua.Class()` / `---@class` 类和文件级变量。MCP 新增 `search_lua`（模糊/精确，`M:Fire` 限定所属表，按种类过滤）、`search_lua_files`（文件名或 require 路径 `UI.MainPanel`）、`get_lua_outline`，`index_status` 里有 Lua 状态
- **Lua 索引增量更新** — 和 C++ 项目索引一样：打开解决方案时复用已保存的索引，只重新读取大小或时间戳变了的文件、只重新解析内容哈希变了的文件；之后 .lua 文件保存、新增、删除、重命名（含整个文件夹）由文件监视自动增量更新。“索引 Lua 脚本”按钮会丢弃缓存全部重新解析。索引保存在 `.vs\UEToVS\Lua\lua-index.json`，VS 没开时 MCP 也能查

## v0.3.4

### 新功能
- **在解决方案资源管理器中定位 C++ 文件** — 类似 Rider 的 Autoscroll from Source：打开或切换到 C++ 文件（.h/.cpp/.inl 等）时，在解决方案资源管理器中展开并选中它，焦点留在编辑器。默认开启，可在 设置 > 导航 > 编辑器 中关闭；只在 UE 解决方案中生效。为大型 UE 解决方案保持轻量：只监听活动文档切换（不监听每次选择变化），快速切换标签时只定位最后一个文件，直接用窗口自带的项目节点（不遍历解决方案），解决方案资源管理器未打开时不做任何事

## v0.3.3

### 改进
- **写入 .mcp.json 到项目根目录** — 原“写入 .mcp.json 到解决方案目录”改名。先从解决方案目录往上找版本库根目录（Git、SVN、Perforce、Mercurial、Plastic；Perforce 认 P4CONFIG 文件，没有时问 `p4` 的客户端根目录），找到就写在版本库根目录，否则写在 UE 项目根目录；Claude Code 一般在版本库根目录启动，这样直接能识别。之前写在解决方案目录里的 uetovs 条目会自动清掉

### Bug 修复
- **.mcp.json 里 uetovs 出现两份** — 之前同时写了 `servers` 和 `mcpServers`。`servers` 只有 VS 读，而 VS 已经通过 `.vs\mcp.json` 自动注册了 uetovs，两处都有会重复；Cursor、VS Code 也不读根目录的 `.mcp.json`。现在只写 Claude Code 用的 `mcpServers`，并删掉旧版本写入的 `servers.uetovs`（其他服务器不动）

## v0.3.2

### 改进
- **IntelliSense 接管范围默认“仅 UE 解决方案”** — 打开 UE 解决方案时才关闭 VS 自带的 C++ IntelliSense，关闭解决方案或打开普通解决方案时自动恢复；从“全局”切到“仅 UE 解决方案”且当前不是 UE 解决方案时立即恢复
- **设置只保存改过的值** — v0.3.0 迁移时把所有默认值也写进了设置，导致之后改默认值对老用户不生效；升级时会清掉与默认值相同的项（v0.3.0 自动写入的“全局”接管范围也一并改回新默认）

### Bug 修复
- **Ctrl 悬停出现两条下划线** — VS 自带的“鼠标单击执行转到定义”和 UEToVS 的 Ctrl+点击同时生效、各画一条线；现在 UEToVS 接管 Ctrl+点击时只对当前编辑器视图关闭 VS 的这一功能（不改全局设置），关闭 UEToVS 的 Ctrl+点击后自动恢复

## v0.3.1

### 改进
- **大工程内存占用更低** — 资产表改为紧凑存储（同一文件夹的资产共用一份包路径，对象路径只为返回的结果生成），去掉列文件阶段多余的资产副本；84 万资产的工程约省 200 MB
- **构建零警告** — 修复测试项目里的编译警告，CI 中测试和 VSIX 构建都把警告视为错误
- **开关重做为 VS 2026 / Fluent 样式** — 设置里的开关不再是被压扁的紫色小药丸：轨道固定 40x20 全圆角，关闭时为描边轨道 + 同色小圆点，打开时填充强调色 + 白色圆点；悬停圆点放大、按下向移动方向拉伸，切换时滑块约 120 ms 缓出动画（系统或 UEToVS 关闭动画时立即切换）；禁用状态使用禁用色；焦点环只在键盘操作时出现，空格切换
- **强调色跟随系统 / 主题** — 优先使用 Windows 强调色，其次 VS 主题高亮色，都取不到时用 Fluent 默认蓝 #0078D4（深色/浅色主题各取合适色阶）；悬停/按下按 Fluent 规则在强调色上叠加；每次解析写 `ui.theme.accent` 日志（来源与候选颜色），外观设置里仍可自定义
- **设置窗口更贴近 VS 2026** — 每个设置项为 8px 圆角卡片，留白更舒展，右侧控件垂直居中且不被拉伸；下拉框展开时不再整体染成强调色，弹出列表 8px 圆角；复选框悬停不再出现强调色描边；文本框、搜索框与按钮同高；左侧分类列表支持键盘焦点环
- **焦点环只对键盘显示** — 用鼠标点击按钮、开关、复选框后不再残留焦点框，Tab / 方向键切换时才显示
- **代码预览带语法高亮** — Search Everywhere、搜索结果窗口和查找引用的预览改用只读的 VS 编辑器视图：VS 自己的 C++ 着色、UEToVS 语义着色（类型/函数/UE 宏等，颜色与 工具 > 选项 > 字体和颜色 一致）叠加内置的词法着色兜底；目标行背景高亮并居中。视图在选择之间复用，文件在后台读取（4 MB 以内，最近文件有缓存），过期加载自动取消，耗时写入 `ui.search.preview` 日志；出错时回退到原来的纯文本预览
- **结果行布局** — 名称优先占满宽度，只有整行确实放不下时才省略；路径（灰色次要文字）用剩余空间并在中间省略，保留开头和最后几级目录（如 `Plugins/SPProject/…/Private/HomeDecoration`）；右侧徽标/模块列按内容宽度右对齐。所有页签和列表弹窗通用，窗口缩放时实时重新计算
- **插件图标换成游戏手柄** — 靛蓝→蓝渐变圆角方块上的白色手柄（两侧握把、左十字键、右四个功能键），16×16 按像素对齐，工具栏按钮、扩展管理器图标与预览图、设置窗口等标题栏统一更换；`scripts/render_brand_icons.py` 生成 SVG 和全部 PNG
- **设置窗口层级与排版** — 页面标题更大（正文 +8，半粗），分组标题（如 索引 / 引擎索引 / 重建）半粗、上方留白多于下方；同组卡片间距 4px、组与组之间 24px；页脚有分隔线和独立底色，“恢复默认”为左侧次要按钮，取消 / 应用 / 确定右对齐且同宽；分段控件与按钮、文本框严格同高；操作按钮用短动词（重建、打开、重启…），完整说明留在卡片左侧
- **索引状态改为状态卡片** — 失败时显示错误样式卡片：错误图标、“索引失败”、引擎给出的原因（如 `OutOfMemoryException: …`）和“查看日志 / 重建”按钮；索引进行中显示进度条和百分比，就绪显示成功样式；引擎不可用时说明原因
- **线程数改为“自动”复选框 + 数值框** — 不再出现“自动（8）”旁边一个写着 0 的数值框：勾选“自动”时数值框禁用并显示本机自动值，取消勾选后可输入 1 到核心数；存储语义不变（0 = 自动），UEToVSLink 端口同样处理
- **设置窗口搜索框** — 内层文本框不再自带边框和焦点下划线（之前盖住光标和文字的下半部分），文字和占位符在控件高度内垂直居中，左右留白按 Fluent 规范；聚焦时由外框显示强调色下划线
- **外观页、快捷键页与其他页面一致** — 外观页的每个选项也是设置卡片，分组标题样式统一，背景透明不再出现另一块底色，页内重复的“恢复默认”按钮在设置窗口中隐藏；快捷键页的预设与每个命令都是卡片

### Bug 修复
- **大工程索引失败（内存不足）** — Engine 的托管堆上限从固定 1 GB 改为物理内存的 25%；写索引段时内存不足会回收后拆成两半重试，单个文件仍写不进去也只跳过该文件，不再让整次索引失败，也不再让剩下的几千个文件逐个报“解析失败”
- **文件夹“删除”事件误删索引** — 收到删除事件但文件夹仍然存在时（重命名回来、原子替换等），不再把整个文件夹的文件移出索引；真正删除时记录 `index.update.dir_removed` 日志
- **点“外观”后右侧仍停在原来的页面** — 外观 / 快捷键是缓存的自定义页，第二次显示时把同一个控件加入新的容器，而它还挂在上一次（已移出窗口的）容器里，WPF 抛出“Specified element is already the logical child of another element”；异常被吞掉，左侧已经高亮“外观”，右侧却还是上一页。现在显示前先把控件从旧容器摘下，并且先生成页面再刷新左侧导航；页面生成失败时显示行内错误卡片（重试 / 查看日志），写 `settings.page.show` 日志（分类、耗时、异常），不再停留在旧页面
- **快捷键页所有命令都显示“（无）”** — 设置窗口的快捷键页按 VSCT CanonicalName（如 `UEToVS.GoToDefinition`）向 DTE 查命令，而 VS 给扩展命令的 DTE 名称会带上菜单路径前缀，查找全部失败，于是每个命令都显示为未绑定，套用预设 / 修改 / 冲突检测也都落空。现在按命令集 GUID + ID 查找；读写时把本地化作用域（中文 VS 的“全局”“文本编辑器”）与英文互转，支持多个绑定、和弦（Ctrl+K, Ctrl+I）和本地化键名；冲突检测不再把 UEToVS 自己的命令当成“其他命令”
- **安装后默认快捷键可能没有生效** — VS 只在合并菜单资源时应用 VSCT 里的默认快捷键，升级后不一定会重新合并。菜单资源版本升到 2；启动后约 5 秒检查一次：仍然没有任何快捷键的 UEToVS 命令自动绑定默认键（Alt+G、Shift+Alt+G、Shift+Alt+F、Alt+←/→、Alt+O、Shift+Alt+O、Shift+Alt+S、Ctrl+Shift+Alt+A、Alt+M 等），已有绑定的命令和已被其他命令占用的按键不动；修改前写备份（快捷键页“还原上次备份”可撤销），每个默认键只决定一次，之后删掉也不会被加回来。日志 `keymap.read.ours`（每个命令的当前绑定、DTE 名称）、`keymap.apply`（应用 / 已绑定 / 被占用 / 未注册）
- **类名在空间充足时也被省略** — 文字宽度按布局取整后可能比实际少零点几像素，触发 WPF 的末尾省略号；现在按真实字体测量并留出 1 px 余量

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
