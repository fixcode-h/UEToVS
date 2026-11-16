# VS 调试控制（v0.8.0）

让 Agent 通过 **VS 自带的调试器** 启停和调试 UE：启动（等于工具栏「本地 Windows 调试器」/ F5）、停止、重启、附加，断点（行、函数、条件、命中次数、日志断点、数据断点），暂停、继续、单步，停下后读调用栈、线程、局部变量、表达式，读调试输出，存小转储。

UEToVS 不做调试器本体（§1.4），这里只是把 VS 的自动化接口（EnvDTE `Debugger` / `SolutionBuild` / 输出窗口）开放给本机的 Agent。

## 1. 结构

```
面板 UE 插件（ue_vs_* 工具、UE 页调试卡片） ──┐
                                            ├─ 命名管道 \\.\pipe\UEToVS.Debug.<devenv PID>（JSON 行 + 令牌）── VS 里的 UEToVS（DebugControlServer → DteDebugBackend → EnvDTE）
UEToVS.Mcp（debug_session / debug_breakpoints / debug_inspect） ─┘
```

- 打开 UE 解决方案、设置「MCP > 允许 Agent 通过 VS 调试 UE」开着（默认开）时，VS 里的 UEToVS 开管道并写发现文件；关解决方案、关设置、VS 退出时关管道、删文件。
- 管道 ACL 只允许当前 Windows 用户；每个请求要带发现文件里的随机令牌（VS 每次启动都换）。
- 所有 DTE 调用都在 VS 的 UI 线程上做；`wait` 用调试器和编译事件唤醒，不占 UI 线程。运行类命令都用不等待的版本（`Go(false)` 等），不会把 VS 卡住。

## 2. 发现文件

`<解决方案目录>\.vs\UEToVS\Debug\endpoint.json`：

```json
{
  "protocol": 1,
  "pipe": "UEToVS.Debug.12345",
  "pid": 12345,
  "token": "64 位十六进制",
  "solution": "D:\\Proj\\MyGame.sln",
  "solutionDir": "D:\\Proj",
  "project": "D:\\Proj\\MyGame.uproject",
  "uetovs": "0.8.0",
  "vs": "17.0",
  "startedUtc": "2026-11-16T12:00:00Z"
}
```

- 同一解决方案开了两个 VS：后开的覆盖；关掉时只删自己写的那份。
- 文件在但连不上（VS 崩了没删）：当作 VS 没开。

## 3. 线路格式

UTF-8，一行一个 JSON 对象，`\n` 结尾。一个连接上可以连发多个请求（按顺序应答）；也可以一个请求一个连接。

请求：

```json
{"v":1,"id":"1","token":"…","op":"status","args":{}}
```

应答：

```json
{"id":"1","ok":true,"result":{…}}
{"id":"1","ok":false,"error":"没有在调试：先 start 启动或 attach 附加到 UE"}
```

- `error` 是中文，写清楚怎么办，原样给用户和 Agent 看。
- 一个结果最多 12000 字；各操作按预算（约 4000 字）分页、截断并标出 `truncated` / `nextOffset` / `more`。
- `subscribe`：`{"v":1,"id":"s","token":"…","op":"subscribe"}` 之后连接不再收请求，服务端推 `{"id":"s","event":"state","result":{…同 status…}}`：连上马上推一次，之后每次调试器 / 编译状态变化推一次（150 ms 内的连续变化合并），另外每 30 秒推一次。客户端断开即结束。

## 4. 操作

通用：行号、列号从 1 开始；帧号 `frame` 从 0（栈顶）开始；`thread` 是线程 ID（`threads` 里的 `id`）。

### 会话

| op | args | 说明 / 结果 |
| --- | --- | --- |
| `status` | `frames?`（0–20，默认 5） | `mode`（`design` 没在调试 / `run` 运行中 / `break` 停下）、`building`、`launchPending`、`lastReason`、`lastBuildFailed`、`startupProject`、`configuration`（如 `Development Editor\|Win64`）、`processes[{pid,name}]`、`breakpoints`（个数）；停下时还有 `breakCount`、`thread{id,name}`、`location{function,file,line,module}`、`breakpoint`（命中的断点 `A.cpp:12`）、`stack[]` |
| `start` | `configuration?`、`project?`、`noDebug?`、`map?`、`game?`、`args?` | 等于按 F5（`noDebug` 等于 Ctrl+F5）：VS 照常先编译（UBT）。`configuration` 切换解决方案配置（全名、`Development Editor`、唯一的片段都行）；`project` 切启动项目。**启动参数**：`map`（如 `/Game/Maps/Test`，插在 .uproject 后面）、`game: true`（加 `-game`，独立游戏进程）、`args`（原样追加，如 `-log -windowed ResX=1280 ResY=720`）只对这一次启动生效：UEToVS 临时改启动项目的「调试 > 命令参数」，进程启动后（或编译失败、取消）马上改回去。已经在调试时报错 |
| `restart` | — | 等于 Ctrl+Shift+F5；没在调试时等于 `start` |
| `stop` | `detach?` | 停止调试（启动的 UE 会被结束）；`detach: true` 只断开、UE 继续跑；正在编译时顺带取消编译 |
| `attach` | `pid?`、`name?`（默认 `UnrealEditor`） | 附加到本机进程（本机调试器 Native）。名字对上多个时报错并列出 PID |
| `build` | `configuration?`、`project?`、`waitMs?` | 编译启动项目（不启动）；`wait` 得到 `buildDone` / `buildFailed`，失败带前 10 条错误 `buildErrors[{file,line,code,message}]` |
| `pause` | `waitMs?`（默认 5000） | 全部中断；默认等到停下再返回位置 |
| `continue` | `waitMs?`（默认 0） | 继续运行；`waitMs` > 0 时等下一次停下 |
| `step` | `kind`（`into` / `over`（默认）/ `out` / `toLine`）、`file`+`line`（toLine）、`waitMs?`（默认 10000） | 单步；默认等到停下，返回新位置（结果同 `wait`，原动作放在 `action`） |
| `wait` | `timeoutMs?`（0–120000，默认 30000） | 等到停下 / 调试结束 / 编译结束。`event`：`break`、`exited`、`buildDone`、`buildFailed`、`timeout`、`idle`（没在调试也没在编译，马上返回）。已经停着时马上返回 `break`。结果带 `status` 的全部字段，停下时带 8 帧 `stack` |

### 断点

| op | args | 说明 / 结果 |
| --- | --- | --- |
| `bp.list` | `offset?`、`limit?`（默认 50） | `items[{i,file,line,function,enabled,condition,hitCount,hits,message,byAgent,bound}]`，`i` 是编号（从 1 起，给 remove / enable 用）。包括用户自己下的断点 |
| `bp.add` | `file`+`line` 或 `function`（`AMyCharacter::Jump`）；`condition?`（`Health < 0`）、`hitCount?`（第 N 次命中才停）、`message?`（日志断点：不停下，把 `Health={Health}` 打到调试输出） | 同一位置已有断点时替换。调试中断点没绑定（符号没加载、这行没代码）时给 `hint`。相对路径按解决方案目录、.uproject 目录找 |
| `bp.add`（数据断点） | `data`（`this->Health` 或地址表达式）、`size?`（1/2/4/8，默认 4）、`frame?`、`thread?` | 要在停下时设（地址要能算出来）；值一变就停。x64 最多 4 个，调试结束失效 |
| `bp.remove` | `index` / `indexes[]` / `file`+`line?` / `function` | 删断点 |
| `bp.enable` | 同上 + `enabled`（默认 true） | 启用 / 禁用 |
| `bp.clear` | `scope?`（`agent`（默认）/ `all`） | 默认只删 Agent 下的（带 `uetovs-agent` 标记），用户自己的断点不动 |

### 检视（要停下）

| op | args | 说明 / 结果 |
| --- | --- | --- |
| `stack` | `thread?`、`offset?`、`limit?`（默认 20） | `frames[{i,function,file,line,module}]`、`total`、`current`、`breakCount` |
| `threads` | `filter?`（名字或位置）、`limit?`（默认 60） | `threads[{id,name,location,current,frozen}]`、`total` |
| `locals` | `frame?`、`thread?`、`depth?`（0–3，默认 1）、`filter?` | `function`、`args[]`、`locals[]`，值节点 `{n,t,v,c?,more?,err?}`（名字、类型、值、子成员、没列出的子成员数、取值出错） |
| `eval` | `expr` 或 `exprs[]`（最多 10）、`frame?`、`thread?`、`depth?`、`timeoutMs?` | `results[]`（值节点）。值按监视窗口的格式（natvis 生效：FString、TArray、FName、UObject*）。**可以调用函数，有副作用**（`GetName()` 没事，`Destroy()` 真会销毁） |
| `output` | `cursor?`、`filter?`、`maxChars?`（默认 3000） | 调试输出窗口（挂调试器时 UE_LOG 也打到这里、日志断点也在这里）。不带 `cursor` 取最后 200 行；带上次的 `next` 只取新增；`restarted` = 输出被清空过 |
| `dump` | `full?` | 存小转储到 `.vs\UEToVS\Debug\Dumps\`（`full` 带堆，编辑器会有几 GB），返回 `file`、`bytes` |

## 5. 典型用法

- **断点调试 GameTest**：`bp.add` → 面板 `ue_gametest run` → `wait` → 命中后 `stack` / `locals` / `eval` → `step` / `continue` → `wait` 等测试跑完（`exited` 或下一次 `break`）。
- **崩溃 / check / ensure**：挂着调试器时会停在 VS 里，`wait` 返回 `break`，`lastReason` 是 `exception` / `unhandledException` / `breakpoint`（`__debugbreak`）；`stack` 看调用栈，`dump` 留证据。
- **谁改了这个值**：停在能访问它的地方 → `bp.add data=this->Health` → `continue` → `wait` → 停在改它的代码后面。
- **改代码 → 复测**：`stop` → `build`（`wait` 看错误）→ `start` → 测试；或者不停调试器用 UE 的 Live Coding。

## 6. 限制

- UE 停在断点时整个进程冻住：面板的 `ue_*` 工具（走 UELink）都调不通，面板会提示先 `continue`。
- 断点、单步、`eval` 只对 C++ 有效；Lua 不行。
- `start` 的启动参数要求启动项目是 VC 工程（UE 生成的 .vcxproj 都是）；VS 在改回参数前崩掉的话，参数会留在启动项目的调试设置里，手动删掉即可。
- 数据断点是 CPU 调试寄存器，x64 一次最多 4 个。
- Release 构建（Development）里很多局部变量被优化掉，显示「已优化」；要看全用 `DebugGame Editor`。
