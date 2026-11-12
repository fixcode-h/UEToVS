# UEToVS

面向 Unreal Engine 5 C++ 工程的 Visual Studio 2022 / 2026 扩展：对标 Visual Assist 的 C++ / UE 感知能力，交互参考 Rider。

- 整体设计：[docs/design/UEToVS-架构设计.md](docs/design/UEToVS-架构设计.md)
- 当前状态：**M0 骨架重建**。新架构的各模块已就位并有单元测试；编辑器内的补全、跳转等 IntelliSense 功能从 M2/M3 开始实现。`legacy/` 是早期原型，只作对照，不参与构建。

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
legacy/                   旧原型，仅供参考
```

## 数据与日志位置

所有数据都在解决方案目录的 `.vs\UEToVS\` 下，删除 `.vs` 即彻底清理：

- `Index\` 索引
- `Logs\` 日志：`host-*.log`、`engine-*.log`，JSON Lines，含会话信息、异常堆栈、每 60 秒资源采样、每 5 分钟各操作延迟统计，可直接交给 AI 分析
- `Cache\` 其他缓存

## 构建

需要 .NET 8 SDK；VSIX 打包需要 Windows + Visual Studio（“Visual Studio 扩展开发”工作负载）。

```
dotnet test                       # 全部单元测试（Linux / Windows 均可）
msbuild src\UEToVS.Host\UEToVS.Host.csproj /restore /p:Configuration=Release
```

产物：`src\UEToVS.Host\bin\Release\net48\UEToVS.Host.vsix`。Engine 进程会被发布并打包进 VSIX 的 `Engine\` 目录。运行机器需要 .NET 8 运行时。

## 注意

- 接管 VS 自带 C++ IntelliSense（工具 > 选项 > UEToVS > 常规）开启后，打开 UE 解决方案时自动关闭 VS 自带的 C++ IntelliSense（修改前备份到 `%LocalAppData%\UEToVS\intellisense-backup.json`），可在 扩展 > UEToVS > 恢复 VS C++ IntelliSense 一键还原；卸载扩展前请先执行恢复。**在 UEToVS 自己的补全/跳转（M3）完成前该选项默认关闭**，否则会暂时失去 C++ 感知。
- 界面语言跟随 VS：英文 VS 显示英文，其他语言显示中文。
