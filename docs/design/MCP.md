# UEToVS MCP 服务设计

> 状态：已实现（v0.3.0）。代码：`src/UEToVS.Mcp`（服务）、`src/UEToVS.Host/Mcp` + `src/UEToVS.Host/Logic/Mcp`（注册）、`tests/UEToVS.Mcp.Tests`。
> 目的：让 AI Agent（VS Copilot Agent 模式、Claude Code、Cursor、VS Code）直接查询 UEToVS 已有的符号索引、文件列表和蓝图资产，不再靠 grep 全文扫描。

## 1. 调研结论：Visual Studio 的 MCP 是怎么做的

资料来源（2026-11 查阅）：

- Visual Studio 文档《Use MCP servers》：<https://learn.microsoft.com/en-us/visualstudio/ide/mcp-servers>
- Visual Studio 2026 发行说明（18.7 起的 MCP 服务器信任校验、18.10 的 Git MCP）：<https://learn.microsoft.com/en-us/visualstudio/releases/2026/release-notes>
- VS Code MCP 配置参考：<https://code.visualstudio.com/docs/agents/reference/mcp-configuration>
- Claude Code MCP 文档：<https://code.claude.com/docs/en/mcp>
- MCP 规范与 C# 官方 SDK：<https://modelcontextprotocol.io/specification>、<https://github.com/modelcontextprotocol/csharp-sdk>

要点：

| 项目 | 结论 |
|---|---|
| 支持的版本 | VS 2022 17.14 起正式支持（Copilot Agent 模式），VS 2026（18.x）一样支持 |
| 配置文件位置（VS 按顺序全部读取） | `%USERPROFILE%\.mcp.json`（全局）、`<解决方案目录>\.vs\mcp.json`（当前用户 + 当前解决方案，不进版本库）、`<解决方案目录>\.mcp.json`（可提交共享）、`<解决方案目录>\.vscode\mcp.json`、`<解决方案目录>\.cursor\mcp.json` |
| 文件格式 | 根键 `servers`；每项 `type`（`stdio` / `http` / `sse`）、`command`、`args`、`env`、`url`、`headers` 等 |
| 传输方式 | stdio（本地进程）、Streamable HTTP、SSE |
| 工具默认状态 | 新加的服务器工具在 Copilot 工具列表中默认未勾选，需要用户启用 |
| 信任 | 18.7 起首次启动配置文件里的 MCP 服务器会弹出信任确认；文档写明“随扩展一起内置发布的服务器”不弹 |
| 扩展注册 MCP 的 API | **没有公开文档的 API**。VS SDK / VisualStudio.Extensibility 都没有“扩展贡献 MCP 服务器”的入口（VS Code 有 `contributes.mcpServerDefinitionProviders`，VS 没有）。现有第三方 VS 扩展（如 CodingWithCalvin 的 VS MCP Server、LadislavSopko 的 MCP for VS）都是在扩展里起一个 HTTP/SSE 服务，然后让用户手动把地址写进 `.mcp.json` |
| 其他客户端 | VS Code：`.vscode/mcp.json` 用 `servers`，支持 `${workspaceFolder}`；Cursor：`.cursor/mcp.json` 用 `mcpServers`；Claude Code：项目根 `.mcp.json` 用 `mcpServers`，支持 `${VAR}` / `${VAR:-默认值}`，也可 `claude mcp add` |

### 1.1 我们怎么按 VS 的标准来

既然没有注册 API，“按 VS 的标准”就是**写 VS 会读的配置文件**：

1. **自动注册（默认开）**：打开 UE 解决方案时，扩展在 `<解决方案目录>\.vs\mcp.json` 的 `servers` 里维护一个 `uetovs` 条目，指向 VSIX 里自带的 `Engine\Mcp\UEToVS.Mcp.exe`。这个文件只属于当前用户、不进版本库，正好对应“这台机器装了扩展才有”。扩展升级后安装目录会变，所以每次打开解决方案都会刷新路径。设置 **UEToVS 设置 > 常规 > MCP > 启用 MCP 服务（给 AI Agent 用）**（`HostSettings.EnableMcpServer`，默认开）关掉时删掉这个条目。
2. **命令写共享配置**：**扩展 > UEToVS > 写入 .mcp.json 到项目根目录**（命令 ID `0x0500`，动作 `uetovs.mcp.writeConfig`）写 Claude Code 的项目级 `.mcp.json`，只写 `mcpServers`。位置：从解决方案目录往上找版本库根目录（`.git` 目录或文件、`.svn`（取最外层）、`.hg`、`.plastic`、P4CONFIG 文件——环境变量 `P4CONFIG` 指定的名字或 `.p4config` / `p4config.txt`；都没有时运行 `p4 -ztag -F %clientRoot% info`，客户端根目录包含解决方案时采用），找到就写在版本库根目录，否则写在解决方案目录（UE 项目根目录）。Claude Code 通常在版本库根目录启动。不写 `servers`：VS 也会读 `<解决方案目录>\.mcp.json` 的 `servers`，而 `.vs\mcp.json` 已经注册了 uetovs，两处都写会让 VS 看到两份同名服务器；Cursor 读 `.cursor\mcp.json`、VS Code 读 `.vscode\mcp.json`，都不读根目录的 `.mcp.json`，写 `servers` 对它们没用。旧版本写进 `.mcp.json` 的 `servers.uetovs` 会被删掉；写到版本库根目录后，旧版本留在 `<解决方案目录>\.mcp.json` 的 uetovs 条目也会删掉（文件删空则删除文件）。
3. **合并而不是覆盖**：两种写入都只动 `uetovs` 这一项里的 `type`、`command` 和 `--solution-dir` 的值。别的服务器、未知字段、用户自己加的 `args` / `env` 原样保留。文件不是合法 JSON 时不写，提示用户。写入用临时文件 + `File.Replace`，是原子的。
4. **选 stdio 而不是 HTTP**：VS、Claude Code、Cursor、VS Code 都支持 stdio。stdio 不占端口，没有鉴权和跨用户访问问题，生命周期由 Agent 管理，VS 不开着也能用（直接读磁盘索引）。

未验证的风险：没有版本库时 `.mcp.json` 写在解决方案目录，VS 也会读到它；如果某个 VS 版本把 `mcpServers` 也当作服务器列表，会和 `.vs\mcp.json` 重复出现一次 uetovs。按 VS 文档它只读 `servers`。

## 2. 架构

```
AI Agent（VS Copilot / Claude Code / Cursor / VS Code）
   │  stdio，MCP JSON-RPC
   ▼
UEToVS.Mcp.exe（每个 Agent 会话一个进程，.NET 8，ModelContextProtocol.Core 2.4.0）
   ├─ 首选：正在运行的 UEToVS.Engine
   │     通过 <解决方案目录>\.vs\UEToVS\engine.json 找到命名管道（pid 校验存活），
   │     StreamJsonRpc + MessagePack，与 VS 宿主同一套契约；握手时不传工程文件，Engine 不会因此重新建索引
   └─ 回退：磁盘上的持久化索引（只读 mmap）
         <解决方案目录>\.vs\UEToVS\Symbols\ 的段文件（ReadOnlyIndex：不校验知识库版本、不写任何文件），
         manifest.json 变化后自动重新打开；资产列表用 WorkspaceDiscovery + AssetLister 枚举 Content\
```

- **数据源选择**：`--source auto`（默认，先 Engine，失败回退索引）、`engine`（只用 Engine）、`index`（只用索引）。每个结果都带 `source` 字段：`engine` / `index` / `index-heuristic`（按名字推断的结果，见 §5）。
- **解决方案目录**：`--solution-dir`；不传则读环境变量 `UETOVS_SOLUTION_DIR`；还没有就从工作目录往上找 `.vs\UEToVS`、`.sln`、`.uproject`。所以 Claude Code / Cursor 在工程根目录启动时可以不带参数。
- **发现文件**：Engine 监听管道后写 `engine.json`（`pipe`、`pid`、`protocolVersion`、`engineVersion`、`solutionDir`、`startedUtc`），退出时只删除自己写的那份。MCP 进程连接失败后 3 秒内不重试，除非 `engine.json` 变了。
- **资源限制**：托管堆上限 512 MB；索引数据是内存映射，不进托管堆；源码行缓存 LRU 64 个文件。
- **日志**：每次调用记一行到 `<解决方案目录>\.vs\UEToVS\Logs\mcp-*.log`：`mcp.tool.<工具名>`（Info，含参数摘要、结果数、数据源、耗时），超过 1 s 记 `.slow`（Warn），参数错误 `.rejected`，异常 `.failed`，取消 `.cancelled`。stdout 只走协议，不打印任何日志。
- **打包**：VSIX 内 `Engine\Mcp\`（单独目录，避免 SDK 依赖的 System.IO.Pipelines / Microsoft.Extensions.* 版本覆盖 Engine 的）。`/p:SkipMcpPublish=true` 跳过。

## 3. 工具、资源和提示

所有工具都是只读的（`readOnlyHint: true`），参数严格校验（未知参数直接报错，告诉 Agent 正确的名字）。行列号从 1 开始。列表类结果统一分页：

```json
{ "source": "index", "indexReady": true, "count": 30, "offset": 0, "hasMore": true, "nextOffset": 30,
  "nonProjectHidden": 12, "note": "...", "items": [ ... ] }
```

默认值字段省略，减少 token。`limit` 默认 30，最大 200。

| 工具 | 用途 | 主要参数 |
|---|---|---|
| `search_symbols` | 按名字找类/函数/变量/宏等，支持 UE 说明符过滤 | `query`、`match`（fuzzy：前缀、CamelHumps 缩写；exact）、`kinds`、`ue`（UCLASS/UFUNCTION/UPROPERTY/…）、`container`、`scope`（project/all） |
| `search_files` | 按文件名找源码文件 | `query`、`extensions`、`scope` |
| `search_assets` | 找蓝图等 `.uasset` / `.umap` 资产 | `query`、`class`（Blueprint/WidgetBlueprint/Material/…，按命名前缀推断）、`scope` |
| `get_file_outline` | 文件大纲（类、成员、带 UE 宏标记） | `path`、`kinds` |
| `go_to_definition` | 跳到定义 | `symbol`，或 `path` + `line` + `column`；`kind` |
| `find_references` | 查引用/调用者，按文件分组并带行文本 | `symbol` 或位置、`include_declarations`、`scope` |
| `get_type_hierarchy` | 基类链 / 派生类 | `type_name`、`direction`（bases/derived/both）、`max_depth`、`max_nodes`、`scope` |
| `get_members` | 类型成员 | `type_name`、`include_inherited`、`kinds`、`ue` |
| `index_status` | 索引是否就绪、数据源、文件数、Engine 状态、Lua 索引状态（`lua`） | 无 |
| `search_lua` | 找 Lua 函数 / 方法 / 类 / 模块表 / 字段 / 文件级变量 | `query`（`M:Fire`、`UI.MainPanel.Show` 限定所属表）、`match`、`kinds`（function/method/class/table/field/variable）、`container`、`include_locals` |
| `search_lua_files` | 按文件名或 require 路径找 Lua 脚本 | `query`（`MainPanel`、`UI.MainPanel`、`UI/MainPanel`） |
| `get_lua_outline` | Lua 文件大纲 | `path`（绝对路径、相对 Lua 目录/项目的路径，或模块名） |

错误返回 `isError: true` 和 `{"error": "<code>", "message": "..."}`，`code` 有 `invalid_argument`、`unknown_tool`、`file_not_found`、`no_index`、`engine_unavailable`。

资源：`uetovs://index/status`（同 `index_status`）、`uetovs://guide`（给 Agent 的使用说明：什么时候用哪个工具）。
提示：`explain_ue_class`（讲解一个 UE 类：层次、成员、反射标记）、`find_blueprint_usage`（找一个 C++ 类被哪些蓝图使用）。

### 3.1 Lua 脚本索引

UnLua / sluaunreal 等项目的 Lua 不在 C++ 索引里，单独建一份：

- **配置**：UEToVS 设置 > 索引 > Lua 脚本 > Lua 脚本路径（`HostSettings.LuaScriptPaths`，`;` 分隔；相对路径先按 .uproject 所在目录、再按解决方案目录解析）。宿主通过 `IndexingSettings.LuaScriptPaths` 推给 Engine，改了立即重新扫描；留空则删除 Lua 索引。命令行：`UEToVS.Engine --lua-dir <dir>`（可重复）。
- **解析**（`src/UEToVS.Engine.Index/Lua/LuaParser.cs`）：容错的词法扫描 + 嵌套跟踪，不是完整语法分析。识别 `function a.b:c()`、`local function f()`（任意深度）；文件级的 `[local] a.b = function/{...}/class(...)/值`；赋给具名表的表构造器字段（含嵌套表）；`---@class Name`。`self.x = …` 这类函数体内的赋值和块内 `local` 不收录，避免噪声。`class(...)`、`UnLua.Class()`、`Base:extend()` 等记为 class。
- **增量**（`LuaIndexer` + `Engine/Indexing/LuaIndexService.cs`，与 C++ 索引同样的思路）：首次扫描复用已保存索引里大小和时间戳都没变的文件；时间戳变了但 xxHash64 相同只更新戳不重新解析；之后 Lua 目录上的 FileSystemWatcher（`.lua` 文件和文件夹事件，300 ms 防抖）和宿主的保存通知只更新涉及的文件，文件夹删除/重命名整体移除，监视器溢出时增量重扫。“索引 Lua 脚本”按钮（`RebuildScope.Lua`）丢弃缓存全部重新解析。所有工作在单独的后台通道，不占 RPC 线程。
- **存储**：`<解决方案目录>\.vs\UEToVS\Lua\lua-index.json`（临时文件 + 替换，原子写入）。MCP 不经过 Engine，直接读这个文件（每秒最多检查一次时间戳，变了就重新加载），所以 VS 没开也能查；结果的 `source` 是 `lua-index`。状态在 `EngineStatus.Lua*` 和 `index_status.lua`。
- **日志**：`lua.index.scan.start/done`（文件数、解析/复用/未变/删除数、耗时）、`lua.index.update`、`lua.index.rebuild.requested`、`lua.index.disabled`、`lua.fs.watching`、失败时 `lua.index.*.failed`；MCP 端 `mcp.lua.loaded`。

## 4. 各客户端配置示例

假设扩展装在 `C:\Users\me\AppData\Local\Microsoft\VisualStudio\17.0_xxx\Extensions\abc\`，解决方案在 `D:\MyGame`。一般不用手写：VS 自动注册，其他客户端用命令生成的 `.mcp.json`。

**Visual Studio**（`D:\MyGame\.vs\mcp.json`，扩展自动维护）：

```json
{
  "servers": {
    "uetovs": {
      "type": "stdio",
      "command": "C:\\Users\\me\\...\\Extensions\\abc\\Engine\\Mcp\\UEToVS.Mcp.exe",
      "args": ["--solution-dir", "D:\\MyGame"]
    }
  }
}
```

然后在 Copilot Chat 切到 Agent 模式，工具列表里勾选 `uetovs` 的工具。

**Claude Code**（版本库根目录或 `D:\MyGame` 下的 `.mcp.json`，命令生成，只含 `mcpServers`）：

```json
{
  "mcpServers": {
    "uetovs": {
      "type": "stdio",
      "command": "C:\\Users\\me\\...\\Engine\\Mcp\\UEToVS.Mcp.exe",
      "args": ["--solution-dir", "D:\\MyGame"]
    }
  }
}
```

或者命令行：`claude mcp add uetovs --scope project -- "C:\Users\me\...\Engine\Mcp\UEToVS.Mcp.exe" --solution-dir D:\MyGame`

**Cursor**（`D:\MyGame\.cursor\mcp.json`，键名 `mcpServers`，内容同上）。

**VS Code**（`D:\MyGame\.vscode\mcp.json`）：

```json
{
  "servers": {
    "uetovs": {
      "type": "stdio",
      "command": "C:\\Users\\me\\...\\Engine\\Mcp\\UEToVS.Mcp.exe",
      "args": ["--solution-dir", "${workspaceFolder}"]
    }
  }
}
```

命令行参数：`--solution-dir <目录>`、`--source auto|engine|index`、`--log-level Error|Warn|Info|Debug|Trace`、`--help`。

## 5. 比 grep 快多少

测试：`tests/UEToVS.Mcp.Tests/BenchmarkTests.cs`，默认跳过，设置 `UETOVS_BENCH_FILES=<Actor 数>` 运行，`UETOVS_BENCH_OUT=<文件>` 输出 Markdown。MCP 一列是 Agent 实际付出的代价：通过真实 `UEToVS.Mcp` 进程的 stdio JSON-RPC 往返。

合成工程：4,000 个 Actor（8,000 个源文件，7.0 MB）、2,000 个蓝图资产；4 核 Linux，ripgrep 14.1.0；每项 7 次取中位数，文件已在页缓存中（对 grep 最有利）。

| 问题 | Agent 惯用做法 | 耗时 | MCP 工具 | 耗时（stdio 往返） | 加速 |
|---|---|---:|---|---:|---:|
| 类定义在哪（精确名） | `rg -n 'class\s+\w+\s+AGenActor3007\b'` | 18.1 ms | `go_to_definition` | 0.7 ms | 25× |
| 模糊找类 | `rg -n 'class\s+\w+\s+\w*GenActor300\w*'` | 19.3 ms | `search_symbols` | 2.8 ms | 7× |
| UFUNCTION 声明 | `rg -n -B1 '\bDoWork3007\('` | 19.4 ms | `search_symbols` | 0.8 ms | 24× |
| 谁调用了某函数 | `rg -n -w DoWork1344` | 19.3 ms | `find_references` | 1.1 ms | 18× |
| 类的成员列表 | `cat GenActor3007.h` | 1.7 ms | `get_members` | 1.5 ms | 1× |
| 按文件名找文件 | `rg --files -g '*GenActor3007*'` | 7.9 ms | `search_files` | 2.3 ms | 4× |
| 找蓝图资产 | `rg --files -g '*BP_Gen1344*' Content` | 5.2 ms | `search_assets` | 3.1 ms | 2× |
| 子类有哪些 | `rg -n ':\s*public\s+AGenActor134\b'` | 20.2 ms | `get_type_hierarchy` | 1.5 ms | 13× |

MCP 进程首次启动 + initialize + 打开索引共约 0.5 s，每个 Agent 会话只付一次。建索引（VS 打开解决方案时后台进行）7.9 s。

怎么理解这些数字：

- **耗时随工程规模分开**：grep 是 O(源码总量)，上面 7 MB 已经 18–20 ms；真实 UE 工程算上引擎源码有几 GB，冷缓存下 rg 一次要几秒到几十秒，而索引查询基本不随规模变化（500 个 Actor 时 MCP 也是亚毫秒）。
- **更大的收益在准确度和 token**：grep 的结果是文本行，`DoWork` 会匹配到注释、字符串、同名函数，`: public X` 抓不到多行继承或间接派生，Agent 要多轮读文件确认。MCP 直接给出结构化结果（种类、UE 标记、所在类、定义位置、派生链），一次调用就能回答，省掉后续的读文件轮次。
- **grep 仍然适合**的场景：搜字符串字面量、注释、配置文件内容。这些内容不进索引，MCP 服务的说明（initialize 返回的 instructions）也是这么告诉 Agent 的。

## 6. 已知限制

- Engine 的 M2 导航 RPC（定义、引用、类型层次、成员）目前是空实现，MCP 在这些工具上会回退到索引里的按名字查找（`source: "index-heuristic"`）：同名重载/不同命名空间的同名类型可能多出结果；引用来自函数体内的标识符出现位置，不含基类子句。
- 资产类型是按命名前缀猜的（`BP_`、`WBP_`、`M_`、`T_`…），没有读 `.uasset` 头；不符合命名规范的资产只能按名字搜。
- 离线索引是磁盘上的最近一次持久化结果，不含 Engine 内存里尚未落盘的增量（编辑中的文件）。
- 只提供 stdio 传输，没有 HTTP；每个 Agent 会话一个进程。
- Linux 上无法验证：VS 是否实际读取并显示 `.vs\mcp.json` 里的条目、18.7 信任弹窗行为、VSIX 打包路径、Windows 命名管道连接（测试在 Linux 上用 Unix 域套接字跑通同一套代码）。
