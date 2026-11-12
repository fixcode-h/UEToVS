# Agent / API：把自定义 API 的模型接入 VS 自带聊天

UEToVS 设置 > Agent / API 配置 OpenAI 兼容（Chat Completions）的 API 和密钥，从接口获取模型，勾选的模型自动出现在 Visual Studio 自带的 Copilot 聊天里。

## 1. VS 这边能做什么（调研结论，Copilot 18.6）

Visual Studio 没有给扩展注册聊天模型的公开接口（VS Code 的 `LanguageModelChatProvider` 在 VS 里没有对应物）。聊天的“添加模型提供程序”对话框做的事是：

- 模型列表写在 `%LocalAppData%\Microsoft\VisualStudio\Copilot\BringYourOwnModel\ConfiguredBringYourOwnModel_v1.json`（Newtonsoft，属性名 PascalCase）：
  提供商数组 `{Name, IsApiKeyAvailable, Models[], Endpoint}`，模型 `{Id, DisplayName, IsToolCallingEnabled, IsVisionEnabled, MaxInputTokens, MaxOutputTokens, ProviderName, IsCustom, IsSelected, CustomURL}`。
  `Endpoint` 是枚举：OpenAI=2、Anthropic=3、Google=4、xAI=5、AzureFoundry=7、Ollama=10。VS 读取时按 `Endpoint` 合并同类提供商。
- 密钥不在这个文件里，存在 VS 的安全凭据服务（`ISecureCredentialStore`），键名是 `<提供商>@<GitHub 用户名>`；某个提供商取不到密钥时，它的模型整组不显示。
- OpenAI、Anthropic 两类固定走官方地址，**不认自定义地址**；只有 Ollama（和 Azure）用模型的 `CustomURL`，Ollama 会用 OpenAI Chat Completions 客户端请求 `scheme://host:port/v1`（路径固定是 `/v1`），密钥为空时发 `ollama`。
- VS 在打开聊天（会话开始）时读取这个文件，改完要重新打开聊天窗口或重启 VS。
- 微软文档说 VS 18.10 起 BYOK 改版，这个格式以后可能会变。

## 2. UEToVS 的做法

```
VS Copilot 聊天 ──(OpenAI Chat, model=VS 模型 ID)──► UEToVS 本地转发 127.0.0.1:47811/v1 ──(换成真实模型名 + 密钥)──► 你的 API
```

- **本地转发**（`Logic/Agent/AgentGateway.cs`）：VS 进程里的一个只监听 127.0.0.1 的小 HTTP 服务（原始 socket，不用 HttpListener，免管理员权限）。
  `POST /v1/chat/completions` 按模型 ID 找到 API，换上真实模型名和密钥，原样流式转发；上游的错误原样返回给聊天。另外实现 Ollama 的 `/api/tags`、`/api/show`、`/api/version` 和 `/v1/models`，所以在 VS 里手动“添加 Ollama、地址填转发服务”也能列出这些模型。
  带 `Origin` 头（浏览器）或 Host 不是本机的请求一律拒绝，网页不能借它用你的密钥。每个 API 可设并发上限，超过的排队。
- **写入 VS**（`Logic/Agent/VsByokConfig.cs`）：把勾选的模型写进上面那个文件的 `ollama` 提供商，`CustomURL` 指向转发服务。只增删 `CustomURL` 是转发地址的模型，别的提供商和你自己的 Ollama 模型不动；第一次写之前备份原文件为 `.uetovs-backup`；文件不是数组（VS 也读不了）时不碰它并报错。
  模型 ID 在所有 API 里唯一时直接用原名，重名时加 `API 名称/` 前缀。
- **密钥**：`%LocalAppData%\UEToVS\agent-keys.dat`，Windows DPAPI 按当前用户加密；API 列表在 `%LocalAppData%\UEToVS\agent-apis.json`，不含密钥。VS 的文件和日志里都没有密钥。
- **地址**：填根地址即可。`https://api.deepseek.com` → `https://api.deepseek.com/v1`；已有版本段（`/v1`、`/api/paas/v4`）原样用；贴了完整的 `/chat/completions` 会自动去掉。

## 3. 限制

- 只支持 OpenAI Chat Completions 兼容的 API（大部分中转、DeepSeek、通义、智谱、Moonshot、OpenRouter 等）。只支持 Anthropic Messages 协议的 API 需要协议转换，暂不支持。
- 转发服务跟着 UEToVS 包加载：打开 C++ 解决方案或执行任意 UEToVS 命令后才运行。
- VS 需要为 Ollama 提供商存过一次凭据，模型才会显示。如果应用后模型选择器里还是没有，在聊天的模型选择器 > 管理模型 > 添加模型提供程序里选 Ollama、地址填转发地址添加一次即可。
- 依赖 VS 内部格式，VS 升级后（尤其 18.10+）可能需要跟着调整。
