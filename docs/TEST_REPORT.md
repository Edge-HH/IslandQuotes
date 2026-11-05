# Motivation · AI 每日一言 测试报告

- 记录时间：2026-11-05 18:18–18:42（+08:00）
- 执行角色：QA / documentation engineer
- 被验仓库：`C:\Users\user\Documents\Code\Motivation`
- 结论：Release 编译、CIPX 打包、静态验收、核心服务 headless 测试和无密钥 Pollinations 连通性测试通过；ClassIsland 宿主内的实际导入、界面点击和动画未在本环境执行，不能视为已验证。

## 1. 环境和来源

| 项目 | 实际值 |
| --- | --- |
| OS | Windows 10.0.19045 x64 |
| .NET SDK | `10.0.403`（运行时 `10.0.14`） |
| SDK 路径 | `C:\Users\user\Documents\Code\Motivation\.tools\dotnet\dotnet.exe` |
| PowerShell | Windows PowerShell `5.1.19041.4291`；本机未安装 `pwsh` |
| 目标框架 | `net10.0-windows` |
| ClassIsland SDK | `ClassIsland.PluginSdk` `2.1.1.1` |
| 插件清单 | ID `ink.motivation.quoteai`，入口 `Motivation.dll`，版本 `1.0.0.0`，API `2.0.0.0` |

初始任务说明提到从 ExtraIsland 的「名句一言」组件提取并重构。需求记录的旧源码路径是 `C:\Users\user\Documents\Codes\extraisland`，实际核对到的源码是 `C:\Users\user\Documents\Code\ExtraIsland\ExtraIsland\Components\Rhesis\`，来源实现位于 `...\Shared\RhesisProviders.cs`。该路径差异已写入 README。

初始化时 `docs/ARCHITECTURE.md` 尚未落盘；后来由 lead 补建。本报告和 README 的最终校对依据为实际源码、manifest、Models/Contracts 和实际测试结果。

## 2. 结果摘要

| 验证项 | 结果 | 证据 |
| --- | --- | --- |
| NuGet restore | 通过 | exit 0 |
| Debug compile | 通过 | exit 0，0 个警告，0 个错误 |
| Clean Release rebuild | 通过 | exit 0，0 个警告，0 个错误，5.98 s |
| 任务要求的 raw Release + CIPX 命令 | 通过 | exit 0，0 个警告，0 个错误，2.83 s |
| `scripts/verify.ps1 -TestPublicAI` | 通过 | exit 0，128 通过，0 失败，1 警告 |
| Manifest / 入口程序集 / 包内容 | 通过 | 静态检查和 CIPX ZIP 检查 |
| 低/中/高风险策略静态检查 | 通过 | `auto_pass` / `manual_required` / `auto_block` 映射 |
| 日志字段和密钥脱敏静态检查 | 通过 | 路径、stage、request ID、upstream ID、redaction |
| 无密钥 Pollinations 连通性 | 通过 | HTTP 200，OpenAI-compatible body |
| Pollinations 失败路径退出码检查 | 通过 | 不可达端点返回 exit 1 |
| headless 核心服务测试 | 通过 | 本地无网络 36/36；四个 live quote providers 56/56；四来源+live AI 综合 60/60 |
| ClassIsland GUI 导入/点击/动画 | 未执行 | 当前环境没有宿主 UI 自动化；见第 9 节 |
| 四个真实名句 API 基础实测 | 通过 | headless harness：4 providers × 5 assertions = 20/20；见第 7.2 节 |

## 3. 构建过程与已知错误的处置

### 3.1 早期部分代码构建错误（已解决）

在插件代码仍在落盘时，`dotnet build ... -c Debug --no-restore` 曾以 4 个错误退出：

```text
Services/RhesisProviders.cs(40,20): error CS0246: HttpClient not found
Services/RhesisProviders.cs(38,30): error CS0246: HttpClient not found
Services/ReviewQueueService.cs(18,22): error CS0246: AiReviewService not found
Services/ReviewQueueService.cs(35,9): error CS0246: AiReviewService not found
```

处置：队列实现补上 `using System.Net.Http;`；AI 后端补齐 `Services/AiReviewService.cs`。最终 Debug 和 Release 构建均通过。

### 3.2 CIPX 打包的 pwsh 依赖（已解决）

首次执行 Release + CIPX 时，`ClassIsland.PluginSdk` 的打包目标调用 `pwsh` 生成 MD5：

```text
C:\Users\user\.nuget\packages\classisland.pluginsdk\2.1.1.1\build\net10.0\ClassIsland.PluginSdk.targets(33,3):
error MSB3073: 命令「pwsh ... generate-md5.ps1」已退出，代码为 9009。
'pwsh' 不是内部或外部命令
```

本机只有 Windows PowerShell 5.1。处置：

- lead 增加仓库级 `Directory.Build.targets`，在 Windows 且未指定 PowerShell 时回退为 `powershell.exe`；
- `scripts/build.ps1 -CreateCipx` 也检测 `pwsh`，缺失时传 `-p:PowershellBinaryName=powershell.exe`；
- README/USER_GUIDE 记录该诊断方法。

最终任务要求的 raw 命令已在本机实际通过：

```powershell
.\.tools\dotnet\dotnet.exe build .\Motivation.csproj -c Release -p:CreateCipx=true
```

### 3.3 最终构建命令

```powershell
# SDK/版本
.\.tools\dotnet\dotnet.exe --version
# 10.0.403

# 还原
.\.tools\dotnet\dotnet.exe restore .\Motivation.csproj
# exit 0

# 最终 Debug
.\.tools\dotnet\dotnet.exe build .\Motivation.csproj -c Debug
# exit 0, 0 warnings, 0 errors, 2.25 s

# Clean Release rebuild + CIPX
.\.tools\dotnet\dotnet.exe build .\Motivation.csproj -c Release -t:Rebuild -p:CreateCipx=true -p:PowershellBinaryName=powershell.exe
# exit 0, 0 warnings, 0 errors, 5.98 s

# 任务要求的 raw 命令（未加额外属性）
.\.tools\dotnet\dotnet.exe build .\Motivation.csproj -c Release -p:CreateCipx=true
# exit 0, 0 warnings, 0 errors, 2.83 s
```

当前最终构建没有编译警告。早期 Avalonia 的 `AVLN3001` 警告在后续 UI 源码/构建状态变更后未再出现；本项目不是以运行时 XAML 加载器构造组件，而是由 ClassIsland DI 注册组件。没有在本环境导入宿主做进一步确认，见风险部分。
## 4. 产物与实际校验值

最终 Release 构建产出：

| 产物 | 大小 | SHA-256 |
| --- | ---: | --- |
| `bin\Release\net10.0-windows\Motivation.dll` | 282,112 bytes | `C3859CCFE543217AEB53786BD03209080335A9DD0972418FBC75AD0D9669D195` |
| `bin\Release\net10.0-windows\manifest.yml` | 458 bytes | 见源文件 |
| `bin\Release\net10.0-windows\icon.png` | 28,066 bytes | `790DB60E491E1C3D3744592D2C0D30D2A98F4DBF4B9FEF8B4557EAB60E882666` |
| `cipx\Motivation.cipx` | 198,704 bytes | `9E935018865EA4DAA5F09EEFE0F476B0E11A39EE82DD62185730E10A650D9E60` |
| `cipx\checksums.md` | 174 bytes | 内容见下 |

CIPX 包内 ClassIsland 生成的 MD5：

```text
<!-- CLASSISLAND_PKG_MD5 {"Motivation.cipx":"5A667D655D6F22F6DBC41F460650E962"} -->
```

使用 `System.IO.Compression.ZipFile` 实际打开 `cipx\Motivation.cipx`，确认包含：

- `Motivation.dll`
- `manifest.yml`
- `icon.png`
- `LICENSE.txt`
- `Motivation.deps.json`
- `Motivation.runtimeconfig.json`
- `Motivation.pdb`
- `README.md`

包内 `manifest.yml` 的 `entranceAssembly` 再次验证为 `Motivation.dll`。

### 4.1 Manifest 静态检查

以下字段存在且非空：`id`、`name`、`description`、`entranceAssembly`、`url`、`version`、`apiVersion`、`author`、`repoOwner`、`repoName`、`assetsRoot`。

- `id` = `ink.motivation.quoteai`
- `name` 包含 `Motivation`
- `entranceAssembly` = `Motivation.dll`
- `version` = 四段数字 `1.0.0.0`
- `apiVersion` = `2.0.0.0`
- URL 格式为 http(s)

发布元数据仍是本地占位值：`manifest.yml:5` 的 `url: https://github.com/local/Motivation`、`:8` 的 `author: Motivation`、`:9` 的 `repoOwner: local`。这不影响本地编译/导入测试，但正式发布前应替换为真实的作者与仓库地址。

### 4.2 图标

`icon.png` 使用 bundled Python + Pillow 12.3.0 生成，实际为 256×256 RGBA PNG，文件签名 `89 50 4E 47 0D 0A 1A 0A`，`verify.ps1` 检查了 PNG signature 与 IHDR 宽高。图形为原创盾牌、引号与星光，不复用 ExtraIsland 素材。

## 5. 静态验收（verify.ps1）

最终命令：

```powershell
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify.ps1 -TestPublicAI
```

结果：

```text
Verification summary: 128 passed, 0 failed, 1 warnings
VERIFY OK
exit 0
```

唯一的 warning 是：仓库没有独立的 `*.json` 设置默认文件可做 JSON parse；默认值由 C# 对象初始化器提供，已通过源码 token 检查。该 warning 不是失败，也不把未执行的 JSON 解析冒充成已执行。

`verify.ps1` 实际检查并通过的内容包括：

- 必需文件存在：manifest、项目、Plugin.cs、README、用户指南、隐私文档、图标、核心服务源码。
- manifest 必需字段、ID、入口程序集、版本、API 版本、http(s) URL。
- `.csproj` 为合法 XML、目标 `net10.0-windows`、程序集名 `Motivation`、引用 `ClassIsland.PluginSdk`、复制 manifest/icon。
- `Plugin.cs` 有 `[PluginEntrance]`、继承 `PluginBase`、调用 `PluginRuntime.Initialize`、注册组件和设置页。
- 四个名句来源类及 upstream ID/`content-sha256` 机制。
- 全部 14 个 AI 预设 ID、五类分类、三种协议、Pollinations 匿名/免密钥/公共免费声明。
- 队列的待复核/播放集合、单条/批量批准拒绝、批次刷新、并发上限、最大缓冲、`review-queue.json` 持久化。
- `Low` → `auto_pass`、`Medium` → `manual_required`、`High` → `auto_block`、AI 失败 → `ai_failure`（fail-closed）的源码映射。
- FIFO 从索引 0 取出、移除，且批准只追加、不头插。
- `Reviewing`/`Pending` 恢复为人工复核。
- 审计日志目录、`audit-*.jsonl`、UTF-8 no BOM、correlation/upstream/request ID、`quote_request`/`ai_request`/`ai_review`/`decision`/`manual_review`/`playback`/`queue_restored` 来源。
- 敏感字段及 Bearer 文本脱敏。
- 内置预设的 `ApiKey` 字面量为空。
- 文档包含功能、构建、风险、人工队列、日志、Ollama、隐私和路径差异说明。
- 源码扫描未发现 `sk-`、`AIza`、长 Bearer、GitHub/Slack token 形态的硬编码密钥。
- 调用 `scripts/build.ps1 -Configuration Release -CreateCipx`，然后检查程序集/manifest/icon/CIPX/ZIP 内必需项。
- 可选 `-TestPublicAI` 实测 Pollinations。

## 6. 无密钥 Pollinations 端点实测

直接运行：

```powershell
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-public-ai.ps1
```

结果：

```text
Endpoint : https://text.pollinations.ai/openai
Model    : openai
Auth     : anonymous (no API key)
Request  : POST application/json (OpenAI chat-completions shape)
HTTP status: 200
Response shape: OpenAI-compatible choices[0].message.content present.
PUBLIC AI TEST PASSED
exit 0
```

实际响应正文前缀显示：

- `id` 以 `pllns_` 开头；
- `choices[0].message.content` = `connectivity ok`；
- 供应商返回 `model: gpt-oss-20b`；
- `usage.total_tokens` 为 169；
- `user_tier: anonymous`。

`verify.ps1 -TestPublicAI` 再次实际请求该端点并得到 HTTP 200，公共端点测试项通过。

失败路径也实际测试：将 `-Endpoint` 指向不可达的 `http://127.0.0.1:9/`，脚本输出 `HTTP status: 0` 和 transport error，并以 exit 1 退出。说明公共测试脚本在失败时不会假报成功。

## 7. Headless 核心服务测试

为验证纯服务逻辑而不依赖 ClassIsland GUI，在 `%TEMP%` 建立临时 .NET 10 console harness，引用 `Motivation.csproj`，未提交进仓库。实际执行的典型命令：

```powershell
.\.tools\dotnet\dotnet.exe run -c Release --project "$env:TEMP\motivation-qa-harness\MotivationQaHarness.csproj"
```

### 7.1 本地逻辑：36/36 通过

- `ParseReviewResponse`：合法 JSON 成功、风险 medium、confidence 保留、categories 保留；带 Markdown fence/前后文字的 JSON 可解析；非法文本和未知 risk 均返回 `IsSuccess=false`、`Risk=Unknown`。
- SettingsStore：默认内置预设数量 ≥14、默认活动供应商为 `pollinations-openai`、PublicFree 且不需要 key、内置 key 为空。
- `env:NAME`：从环境变量解析真实值；`settings.json` 保存引用而非解析后的 secret；重新加载后引用仍保留；直接值按原值返回。
- AuditLogService：实际生成 `audit-*.jsonl`；`apiKey`、`authorization`、Bearer 文本被替换；保留 `[REDACTED]`；设置 `Log.Enabled=false` 后审计服务自动禁用且不再新增行；恢复后重新启用。
- Queue：两个 Approved 项按先进先出返回，均标记 Displayed；中风险 Pending 经批量批准后追加到队尾并可显示；中风险拒绝后进入 Rejected 且不进入播放队列。
- Queue 去重：源码每个槽位最多 3 次 refetch，identity 同时使用正文与 `providerId + upstreamId`；headless 通过反射实际调用 `AddQuoteIdentity`，确认重复正文或重复上游身份被拒绝。同时源码写入 `queue_duplicate` 审计。
- `PluginRuntime`：完整初始化后 audit 服务可用；通过设置保存 `Log.Enabled=false` 后 `PluginRuntime.AuditLog.Enabled` 同步为 false，确认 queue-backend 的运行时 wiring 生效。

### 7.2 四个 live quote providers：56/56 通过

打开临时 harness 的 provider 开关后，再使用同一个 console 项目实际请求四个来源：

```powershell
$env:MOTIVATION_QA_PROVIDERS = "1"
.\.tools\dotnet\dotnet.exe run -c Release --project "$env:TEMP\motivation-qa-harness\MotivationQaHarness.csproj"
```

实际结果为 **56/56 通过，0 失败**（36 个基础检查 + 4 个 provider 各 5 个断言）：

- `hitokoto`：返回非空正文；ProviderId、UpstreamId、RequestMethod/RequestUrl 均非空；UpstreamIdKind = `uuid`。
- `jinrishici`：返回非空正文和请求元数据；UpstreamIdKind = `content-sha256`。
- `dlystc`：返回非空正文和请求元数据；UpstreamIdKind = `content-sha256`。
- `saintic`：返回非空正文和请求元数据；UpstreamIdKind = `numeric`。

测试报告不记录第三方名句正文，只记录协议字段和断言结果。该测试证明当前网络下四个 endpoint 的基础请求/解析可用，不代表所有查询参数、分类路径、长期可用性或限流行为都已覆盖。
### 7.3 可选 live AI 测试

使用 `AiReviewService.TestAsync` 对活动 Pollinations 预设发真实审查请求。

第一次尝试（公共配额触发）：

```text
LIVE AI RESULT: success=False; risk=Unknown; confidence=0.00;
error=AI 返回 HTTP 402 (Payment Required)：Free limit reached:
1 reply every 45 seconds per IP. Retry after 14s ...
```

结果符合 fail-closed：失败没有变成 Low，也没有进入通过状态。随后等待约 22 秒重试：

```text
LIVE AI RESULT: success=True; risk=Low; confidence=0.95;
requestId 非空; error 为空
```

第一次 standalone live AI 运行在加入去重/provider kind 断言前为 38/38 通过。随后将 `MOTIVATION_QA_PROVIDERS=1` 与 `MOTIVATION_QA_LIVE=1` 同时开启做最终综合运行，实际结果为 **60/60 通过，0 失败**（36 个基础 + 20 个 provider + 4 个 live AI 断言）。第一次的 402 已作为公共端点限流事实保留在报告中，不隐藏。
### 7.4 Host assembly type-load smoke (lead, final artifact)

A final reflection-only load test resolved the release plugin assembly against the locally installed
ClassIsland 2.1.0.1 host directory (without launching or modifying the host). All plugin entry types
loaded successfully:

- `Motivation.Plugin` derives from `ClassIsland.Core.Abstractions.PluginBase`;
- `Motivation.Components.QuoteAi.QuoteAiComponent` derives from `ComponentBase<QuoteAiConfig>`;
- `Motivation.SettingsPages.AiSettingsPage` derives from `SettingsPageBase`;
- `Motivation.SettingsPages.ReviewQueuePage` derives from `SettingsPageBase`.

Result: `LOAD RESULT: PASS` (4/4 type-load checks). This validates assembly/type compatibility with the
installed host dependencies, but it is not a substitute for importing the CIPX into a real ClassIsland
UI session.

## 8. 需求到验证的映射

| 用户要求 | 验证方式 | 结果/边界 |
| --- | --- | --- |
| 预展示 AI 审查 | `verify.ps1` 检查组件只显示 queue 返回状态；源码防御检查 `Approved/Displayed`；headless FIFO 验证未审查内容不会自动通过 | 通过静态+核心服务；未在宿主 UI 实机观察 |
| 低风险自动通过 | 源码正则检查 `Low -> Approved -> auto_pass`；`ParseReviewResponse` 验证 low/medium JSON；live AI 返回 Low | 通过；不代表所有 low 分类都准确 |
| 中风险统一人工复核 | 源码检查 `Medium -> AwaitingManualReview -> manual_required`；UI 命令静态检查；headless 批量批准 | 通过核心服务；UI 按钮未点击 |
| 高风险自动拦截 | 源码检查 `High -> Blocked -> auto_block`；UI `DisplayContent`/`MustMask` 检查；组件防御拒绝 High | 通过静态；没有真实高风险样本回放 |
| AI 失败 fail-closed | 无效解析/未知 risk headless 测试；live 第一个请求 402 后返回 success=false/Unknown；源码 `ai_failure` 分支检查 | 通过 |
| 批量抓取/审查 | `FillBatchLockedAsync`、`BatchSize`、`Task.WhenAll`、并发 semaphore、max buffer 静态检查 | 通过静态；未对真实多来源批量发网络负载 |
| 批量人工复核 | headless `ApproveSelected`/`RejectSelected`；UI 包含批准/拒绝选中、全部批准/全部拒绝 | 核心逻辑通过；UI 未点击 |
| FIFO 播放 | headless 两项 Approved 先入先出；源码检查队列首项取出、移除、批准追加、无头插 | 通过核心逻辑 |
| 队列去重 | 静态检查 content/upstream identity、每个槽位最多 3 次 refetch、`queue_duplicate` 日志；headless reflection 测试重复正文/上游身份 | 通过静态与核心逻辑；未构造真实上游重复样本做完整 batch 端到端 |
| 请求/审查/上游 ID 日志 | 静态检查 stages、requestId/upstreamId 字段、JSONL/no-BOM/redaction；headless 验证 JSONL 脱敏和 Log.Enabled | 通过静态/服务层；完整 AI 审查事件字段未做逐字段 harness 断言 |
| 多供应商与公共免费预设 | 静态检查 14 个预设、五类、三协议、Pollinations 匿名 | 通过；只实测 Pollinations，其他供应商无密钥/未实测 |
| 四个名句来源与 upstream ID | 源码检查 + headless harness 对 4 个来源各做一次 live fetch；验证正文、ProviderId、UpstreamId、UpstreamIdKind、RequestMethod/Url | 20/20 provider 断言通过；未覆盖全部自定义选项/长期限流 |
| 人工复核 UI 隐藏高风险正文 | UI view model `MustMask`、`DisplayContent`、`DisplayAttribution`；源码检查 High/Blocked | 通过静态；未做宿主截图/可访问性验证 |
| CIPX 打包与入口 | 最终 raw 命令、`verify.ps1` ZIP 内容、入口程序集检查 | 实际通过 |
| README/用户指南/隐私文档 | 文档 token 检查和人工校对 | 实际存在；未做文档站渲染测试 |
| 图标 256×256 PNG | Pillow 生成、PNG signature、IHDR 尺寸检查 | 实际通过 |

## 9. 未执行或无法声称已验证的项目

以下项目**没有**在本次环境执行，不能写成已通过：

1. 在真实 ClassIsland v2 宿主中安装 CIPX、重启和组件渲染。
2. 点击 AI 设置页、供应商 ComboBox、保存、测试连接 UI、批量复核按钮。
3. 宿主 Dispatcher 下的 UI 集合更新、动画和跨线程行为；headless 只验证无 Dispatcher 路径。
4. 四个真实名句来源 API 的长期稳定性、自定义 query/path 组合和限流行为；本次只各做一次基础 live fetch。
5. 除 Pollinations 外的 13 个供应商真实端点、密钥、限流和模型可用性。
6. 模型分类准确率、误报率、提示词注入对抗集和高风险样本回放。
7. 日志保留期到期删除的实际长期运行、并发压力和多进程竞争。
8. The updated `docs/ARCHITECTURE.md` was checked against actual audit stages; the earlier stage-list mismatch is resolved.
9. 正式发布元数据、许可证和仓库 URL 的最终确认。

## 10. 未解决/剩余风险

### 10.1 Stage list (resolved)

`docs/ARCHITECTURE.md` has been updated after the final source review. It now lists the actual
stages: `quote_request`, `queue_duplicate`, `ai_request`, `ai_review`, `decision`,
`manual_review`, `playback`, `queue_summary`, `queue_restored`, and `queue_error`.

This documentation-only mismatch is resolved and is not an open defect.

### 10.2 发布元数据仍是占位值

`manifest.yml:5` 的 URL 为 `https://github.com/local/Motivation`，`:8` 的作者为 `Motivation`，`:9` 的 repoOwner 为 `local`。本地构建/包结构正常，但正式分发前应替换为真实作者/仓库信息，否则插件市场/更新链接可能无效。

### 10.3 公共免费端点限流与隐私

实测 Pollinations 在相邻请求中返回过 HTTP 402：

```text
Free limit reached: 1 reply every 45 seconds per IP. Retry after 14s
```

同一端点在间隔后返回 HTTP 200 和有效 OpenAI-compatible 响应。因此：

- 公共端点不是稳定的生产 SLA；
- 批量审查和多人部署可能触发限流；
- 失败会按 fail-closed 进入人工复核或拦截，不会自动播放；
- 第三方会接收引用正文和元数据，敏感内容不得发送到该端点。

### 10.4 缺少宿主 UI 自动化

当前构建、核心服务和端点已验证，但 ClassIsland 的实际 XAML/控件渲染、设置页事件绑定、DI 生命周期和 CIPX 安装流程仍依赖宿主环境。建议发布前按 README 首次运行步骤在真实 ClassIsland v2 上做一次手动验收，并检查：

- 组件是否出现在组件列表；
- 两个设置页是否可从 Motivation 设置组打开；
- 中风险条目能否勾选/批量批准/拒绝；
- 高风险历史是否隐藏正文；
- 队列重启恢复是否符合描述；
- 日志目录按钮是否打开正确目录。

### 10.5 原始数据留存

高风险正文不会在组件或高风险历史行显示，但可能仍存在于 `review-queue.json`、其损坏备份和审计日志中（用于恢复/审计）。这不是运行时越权，但管理员必须按隐私文档限制目录 ACL、备份和日志分享；需要彻底删除时不能只清空 UI。

### 10.6 构建环境依赖

本机没有 `pwsh`。最终 raw CIPX 命令通过仓库级 `Directory.Build.targets` 与 `scripts/build.ps1` 的回退逻辑成功执行。分发源码时必须包含 `Directory.Build.targets`；若只复制脚本或只安装 .NET SDK，仍可能遇到 `ClassIsland.PluginSdk.targets` 的 9009 错误。

## 11. 最终判定

已实际执行并通过：

- .NET 10 Debug/Release 编译（0 warnings, 0 errors）；
- raw Release + `CreateCipx=true` 打包；
- `scripts/verify.ps1 -TestPublicAI`：128 passed, 0 failed, 1 non-blocking warning；
- 匿名 Pollinations HTTP 200 测试及失败路径 exit 1；
- headless 核心服务测试：本地 36/36；四个 live quote providers 56/56；四来源+live AI 综合 60/60。

未执行 GUI/CIPX 导入实测，且公共端点存在已记录的限流和隐私风险。这些限制不阻止构建产物交付，但发布前应完成真实宿主手动验收。除第 10 节列出的文档/发布元数据和未执行 UI 验证外，未发现会阻断本地构建、CIPX 生成或静态验收的功能缺陷。
