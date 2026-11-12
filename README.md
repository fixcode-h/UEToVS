# UEToVS

把 Rider 里好用的导航和搜索带到 Visual Studio 2022 / 2026，专门针对 Unreal Engine 5 C++ 工程。个人自用，通过本地 VSIX 安装。

设计文档：<https://claude.ai/artifact/HcUc6HctbS8dY84XXWVkJC>（早期导航功能）；整体架构设计见 [docs/design/UEToVS-架构设计.md](docs/design/UEToVS-架构设计.md)

## 功能

| 功能 | 默认快捷键（Rider 默认键位） | 说明 |
| --- | --- | --- |
| Search Everywhere | 双击 Shift | 全部 / 类型 / 文件 / 符号 / 操作 / 文本 / 资产 七个标签页；Tab 切换；再双击 Shift 切换“包含非项目项” |
| Go to Everything / Type / File / Symbol | Ctrl+T / Ctrl+N / Ctrl+Shift+N / Ctrl+Shift+Alt+N | 驼峰匹配（`PCU` → `PlayerCameraUpdate`）、UE 前缀可省略（`Actor` 找到 `AActor`）、`File.cpp:120` 跳行、`AActor::Tick` 限定容器、`/types` 前缀 |
| Find Action | Ctrl+Shift+A | 所有 VS 命令 + UEToVS 开关 |
| Find in Files | Ctrl+Shift+F | ripgrep 文本搜索，实时出结果 |
| 后退 / 前进 | Ctrl+Alt+← / Ctrl+Alt+→、鼠标侧键 | Rider 式导航历史：跨文件跳转、同文件内远距离跳转都会记录；按解决方案持久化 |
| 最近文件 | Ctrl+E（再按一次只看编辑过的） | |
| 最近位置 | Ctrl+Shift+E | 带代码片段，可按代码内容过滤 |
| 上一个 / 下一个编辑位置 | Ctrl+Shift+Backspace | |
| 文件结构 | Ctrl+F12 | 来自索引，带 UCLASS / UFUNCTION / UPROPERTY 标记 |
| Navigate To | Ctrl+Shift+G | 声明、实现、用法、基类、相关项、Build.cs… |
| 相关项 | Ctrl+Alt+Home | 头文件 ↔ 源文件（识别 Public/Private）、`Foo` ↔ `Foo_Implementation` ↔ `Foo_Validate`、`.generated.h` |
| 书签 | F11 切换、Ctrl+Shift+数字 设置、Ctrl+数字 跳转、Shift+F11 列表 | 行号栏显示，编辑时跟随行移动 |
| 模块依赖修复 | 保存时自动检查；编辑器右键“添加模块依赖” | `#include` 的头文件所属模块没写进 Build.cs 时提示，一键加到 Public/Private |
| Live Coding | Ctrl+Alt+F11 | 需要 UEToVSLink 编辑器插件 |
| 资产搜索 | Search Everywhere 的“资产”页 | 选中后在编辑器里打开（需要插件），否则复制资产路径并在资源管理器里定位 |
| 生成工程文件 | 菜单 扩展 > UEToVS | 调用 UnrealBuildTool -projectfiles |

所有命令都在 **扩展 > UEToVS** 菜单里，也都能在 Find Action 里搜到。

### 键位方案

第一次启动会在顶部提示是否应用 Rider 键位。可选：

- **Rider 默认键位**（默认）
- **Rider（Visual Studio 键位）**

应用前会把所有会被改动的绑定备份到 `%LocalAppData%\UEToVS\keymap-backup.json`，“恢复 Visual Studio 原来的键位”可以完整撤销。也可以在 工具 > 选项 > UEToVS 里换方案后执行“应用键位方案”。

## 架构

```
Visual Studio 进程                         独立进程（.NET 8）
┌──────────────────────────┐  命名管道   ┌─────────────────────────────┐
│ UEToVS.vsix (net48)      │ JSON-RPC   │ UEToVS.Indexer.exe          │
│  键盘钩子 / 弹窗 / 导航   │ ─────────▶ │  UE 感知的 C++ 声明扫描       │
│  键位 / 书签 / Build.cs   │            │  文件 + 资产索引、文件监视     │
└──────────────────────────┘            │  ripgrep 文本搜索            │
            │ localhost TCP              └─────────────────────────────┘
            ▼
  Unreal Editor + UEToVSLink 插件（打开资产、Live Coding）
```

- `src/UEToVS.Core`：与 VS 无关的逻辑（匹配、查询解析、导航历史、双击 Shift 检测、键位预设、Build.cs 解析），有单元测试。
- `src/UEToVS.Indexer`：索引进程。缓存放在 `%LocalAppData%\UEToVS\Index`，引擎索引在同一引擎的多个项目间共用。UE 5.4 引擎源码约 6500 个文件、25 万符号，首次索引约 6 秒，之后读缓存。
- `src/UEToVS`：VSIX。
- `ue/UEToVSLink`：UE 编辑器插件（仅 Win64 编辑器）。

## 构建

需要 Windows + Visual Studio 2022（17.x）或 2026（18.x），装好“Visual Studio 扩展开发”工作负载，以及 .NET 8 SDK。

```
git clone https://github.com/fixcode-h/UEToVS
cd UEToVS
dotnet test
msbuild src\UEToVS\UEToVS.csproj /p:Configuration=Release /restore
```

产物：`src\UEToVS\bin\Release\net48\UEToVS.vsix`，双击安装。构建时会自动发布索引进程并下载 ripgrep 一起打包。

运行机器需要安装 **.NET 8 运行时**（索引进程是 framework-dependent 发布）。缺失时“输出 > UEToVS”里会有提示。

## 安装编辑器插件（可选）

在 Find Action 里执行“UEToVS: 安装 UE 编辑器插件 UEToVSLink 到当前项目”，或手动把 `ue/UEToVSLink` 复制到 `<项目>/Plugins/UEToVSLink`，重新生成工程文件并编译编辑器。插件只监听 127.0.0.1，端口写在 `<项目>/Saved/UEToVSLink/port.txt`。

## 设置

工具 > 选项 > UEToVS：双击 Shift 开关和间隔、只认左 Shift、每类最多结果数、预览、鼠标侧键、导航历史阈值、键位方案、引擎目录覆盖（找不到引擎时用）、保存时检查模块依赖、编辑器插件端口。

## 已知限制

- 代码开发和单元测试在 Linux 上完成，VSIX 的 VSCT 编译、打包和在 VS 里的实际运行需要在 Windows 上验证；第一次使用请留意“输出 > UEToVS”的日志。
- 符号索引是启发式扫描，不是完整的 C++ 解析：宏展开、模板特化等复杂写法可能漏掉；精确的“查找用法 / 转到定义”仍交给 VS 自带的 IntelliSense。
- 搜索预览没有语法高亮。
- 键位里的部分按键名（如 `Bkspce`）以 VS 英文名为准，个别键在非英文 VS 上如果没绑定成功，会在输出窗口列出。
