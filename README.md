# IslandQuotes

IslandQuotes 是一个面向 **ClassIsland v2** 的独立插件。它在「每日一言」真正显示到班级大屏之前，先调用所选的 AI 供应商做内容风险审查；只有低风险内容自动通过，中风险内容进入人工复核，高风险内容自动拦截。

> 插件 ID：`ink.islandquotes.quoteai`  
> 入口程序集：`IslandQuotes.dll`  
> 当前清单版本：`1.0.0.0`，ClassIsland API `2.0.0.0`（以 ClassIsland.PluginSdk `2.1.0.1` 构建，兼容 2.1.0.1 及以上宿主）  
> 目标框架：`.NET 8` / `net8.0-windows`  
> 当前状态：功能与验收结果见 [docs/TEST_REPORT.md](docs/TEST_REPORT.md)（测试报告只记录实际执行结果）。

## 来源与重构说明

本项目把 [ExtraIsland](https://github.com/Lipoly/ExtraIsland) 中的「名句一言」组件提取、重构为独立插件，并新增 AI 预展示审查、人工复核、FIFO 播放队列和审计能力。

- 原始组件实现：`C:\Users\user\Documents\Code\ExtraIsland\ExtraIsland\Components\Rhesis\`
- 原始名句来源实现：`C:\Users\user\Documents\Code\ExtraIsland\ExtraIsland\Shared\RhesisProviders.cs`
- 路径差异说明：需求记录中的旧路径写作 `C:\Users\user\Documents\Codes\extraisland`；实际核对到的源码位于 `C:\Users\user\Documents\Code\ExtraIsland`（目录名是 `Code` 和 `ExtraIsland`）。
- 本仓库的界面、文案、队列服务和图标均为重新实现；`icon.png` 为原创盾牌/引号/星光图形，没有复用 ExtraIsland 的图片素材。

## 功能一览

| 能力 | 行为 |
| --- | --- |
| 可选的预展示 AI 审查 | 在组件设置的每个内容来源中可单独开关「对该来源启用 AI 审查」（内置来源默认开启，自定义来源默认关闭）。开启后该来源的内容进入组件显示之前先审查，不会先把未审查内容显示出来；关闭后只应用本地排除词，其余直接播放（审计决策 `review_disabled`）。 |
| 低风险自动通过 | `RiskLevel.Low` → 自动加入播放队列，按批准顺序播放。 |
| 中风险统一人工复核 | `RiskLevel.Medium` → 进入「人工复核与播放队列」页面，必须由人工批准后才可播放。 |
| 高风险自动拦截 | `RiskLevel.High` → 直接进入拦截历史，不进入人工可批准队列，正文与署名在复核页隐藏。 |
| AI 失败时关闭（fail-closed） | 超时、限流、无效 JSON、未知风险等都不会自动放行。默认「转人工复核」，也可设为「直接拦截」。 |
| 批量抓取与并发审查 | 播放队列为空时抓取一批候选（默认 3 条，缓冲上限 12 条），并按最大并发数审查。 |
| 自定义来源 | 点击「内容来源」标题右侧的添加按钮新增自定义来源：每行一条内容，可选顺序播放（记忆上次播放位置）或随机播放，也可从本地 txt 导入。 |
| FIFO 播放队列 | 低风险自动通过和中风险人工批准都追加到队尾；显示时从队首取出，保证先进先出。 |
| 本地忽略词 | 命中组件设置中的忽略词时直接按高风险拦截并记录 `local_ignore`，即使 AI 返回低风险也不放行。 |
| 提示词快速控制 | AI 设置页可选择判断严格程度（严格/均衡/宽松，影响提示词尺度与本地过滤力度），并勾选允许/禁止的主题标签（如热梗、爱情、励志）；禁止主题由本地强制拦截（`blocked_tag`），允许主题仅作为提示词偏好。 |
| 多供应商预设 | 默认「OpenAI 兼容」，另含主流官方供应商与本地 Ollama 预设，覆盖 OpenAI/Anthropic/Gemini 三类协议。 |
| 审计日志 | 每日 UTF-8 JSONL，记录请求、审查、决策、人工复核、播放和队列恢复事件。 |
| 状态持久化 | 待复核、已批准队列与历史写入 `review-queue.json`；重启后不把未审查内容当低风险播放。 |

### 风险决策

```text
抓取候选
   │
   ├─ 命中本地忽略词 ───────────────→ 高风险拦截（local_ignore）
   │
   └─ AI 审查
        ├─ 命中禁止主题 ─────────────→ 高风险拦截（blocked_tag，本地强制）
        ├─ Low    ── 严格档且置信度<0.7 → 等待人工复核（manual_required_strict）
        │          └─ 其余 ──────────→ 自动通过 → FIFO 播放队列
        ├─ Medium ── 宽松档 ─────────→ 自动通过（auto_pass_relaxed）
        │          └─ 其余 ──────────→ 等待人工复核
        ├─ High   ───────────────────→ 自动拦截（内容隐藏）
        └─ 失败/未知 ── 默认 ────────→ 等待人工复核
                        或配置为 ────→ 直接拦截
```

注意：人工复核只能批准 `Medium`、失败/未知状态的项目；高风险项目不会出现在待复核列表中。严格档下的低风险条目会提升为 medium 后转人工，保证可被批准。AI 分类不是内容安全的绝对保证，发布者仍需承担最终审核责任。

审计中的自动决策名称固定为 `auto_pass`、`auto_pass_relaxed`、`manual_required`、`manual_required_strict`、`auto_block`、`ai_failure`、`local_ignore`、`blocked_tag`；人工动作写入 `manual_review`。

## 构建

### 环境要求

- Windows 10/11
- ClassIsland v2 **2.1.0.1 或更新版本**（插件 API `2.0.0.0`）
- .NET SDK **8 或更新版本**（插件目标为 `net8.0-windows`，与 ClassIsland 2.1.0.1 宿主运行时一致）
- 首次还原 NuGet 包时需要访问 NuGet 源（或预先配置好可用的离线源）

本仓库的构建脚本优先使用仓库内 SDK：`.tools\dotnet\dotnet.exe`；不存在时才使用 `PATH` 中的 `dotnet`。

### 标准命令

在仓库根目录执行：

```powershell
dotnet build IslandQuotes.csproj -c Release -p:CreateCipx=true
```

本工作区已安装本地 SDK 时，也可直接执行：

```powershell
.\.tools\dotnet\dotnet.exe build IslandQuotes.csproj -c Release -p:CreateCipx=true
```

> 如果 CIPX 打包阶段提示找不到 `pwsh`（只安装了 Windows PowerShell 5.1 的环境），可在命令后追加 `-p:PowershellBinaryName=powershell.exe`。`scripts/build.ps1 -CreateCipx` 会自动检测并处理该情况。

推荐使用构建/验证脚本（会自动检查 SDK 主版本并打印产物）：

```powershell
.\scripts\build.ps1 -CreateCipx
.\scripts\verify.ps1
```

### 产物

| 产物 | 路径 |
| --- | --- |
| 插件程序集 | `bin\Release\net8.0-windows\IslandQuotes.dll` |
| 清单 | `bin\Release\net8.0-windows\manifest.yml` |
| 图标 | `bin\Release\net8.0-windows\icon.png` |
| ClassIsland 插件包 | `cipx\IslandQuotes.cipx` |

`IslandQuotes.cipx` 由 `ClassIsland.PluginSdk` 的 `CreateCipx` 目标打包 `bin\Release\net8.0-windows\` 生成。安装时使用 ClassIsland v2 的插件管理界面选择生成的 `.cipx` 文件；具体导入入口以宿主版本界面为准。

## 首次运行配置

1. 在 ClassIsland 中把 **GoodQuotes** 组件加入需要的组件配置/主界面配置。
2. 打开 ClassIsland 的插件设置，进入 **IslandQuotes** 设置组。
3. 打开 **AI审查设置** 页面。
4. 在供应商预设中选择一个配置。默认启用的是 **OpenAI 兼容**，需要自行填写 Base URL、模型与 API Key；也可以改选内置的官方供应商预设。
5. 如预设要求密钥，在 **API Key** 中填写；也可填写 `env:环境变量名`，例如 `env:OPENAI_API_KEY`，避免把密钥直接写进 `settings.json`。
6. 检查 Base URL、协议、模型和请求超时。内置预设通常已填好，只有自定义网关/Ollama 等场景才需要改动。
7. 点击 **保存设置**，再点击 **测试连接**。测试会发送一条无害的内置引用到当前供应商。
8. 回到组件设置，选择名句来源（以及各来源是否启用 AI 审查）、刷新间隔和排除列表。

### 内置供应商预设

| 预设 | 默认协议 | 密钥 |
| --- | --- | --- |
| OpenAI 兼容（默认，需自行填写地址与模型） | OpenAI 兼容 | 按服务要求 |
| OpenAI、DeepSeek、Moonshot（月之暗面）、Xiaomi MiMo、DashScope / 通义千问、智谱 GLM、SiliconFlow、Groq、Together AI | OpenAI 兼容 | 需要 |
| Google Gemini | Gemini 原生 | 需要 |
| Anthropic Claude | Anthropic 原生 | 需要 |
| Ollama 本地模型（默认 `qwen2.5:7b`，`http://127.0.0.1:11434/v1`） | OpenAI 兼容 | 默认不需要 |

模型可用性、限速与价格以各供应商控制台为准。待审文本会发送给所选供应商，请先阅读其隐私政策；在意隐私时请选择本地模型或自建/可信供应商。

## 组件设置

「GoodQuotes」组件设置中可配置：

- 名句来源的启用状态、权重与「对该来源启用 AI 审查」；权重为 0 等同于不参与选择。
- 自定义来源：点击「内容来源」标题右侧的添加按钮新增，可编辑名称和内容（每行一条）、选择顺序/随机播放、从本地 txt 导入或删除。
- 默认启用的来源为「今日诗词」和「诏预」，另外提供「一言」和「每日一句」预设。
- 刷新间隔（秒）；设为 `0` 表示关闭自动刷新，不等于立即显示。
- 字数限制；按来源能力用于过滤过长内容。
- 作者、出处、动画和额外信息展示方式。
- 排除列表（每行一个）；命中后直接拦截。

AI 供应商、API Key、审查失败策略等是插件级全局设置，不保存在每个组件的独立配置中。

## 人工复核与播放队列

打开 **人工复核与播放队列** 页面：

- **待复核**：中风险、AI 失败但采用「转人工复核」策略的项目。可勾选多条后使用「批准选中/拒绝选中」，也可「全部批准/全部拒绝」。
- **播放队列**：已自动通过或人工批准的条目，按加入顺序显示 `#1`、`#2`……。批准只会追加到队尾；显示时才从队首取出。
- **历史**：最近的人工拒绝、高风险拦截和已播放内容。高风险/已拦截条目的正文与署名统一显示为「高风险内容已隐藏」，供应商和上游 ID 仍保留用于审计。

一条中风险内容被批准后，不会插入到队首；它按批准操作加入 FIFO 队尾。拒绝的项目永不播放。队列状态会持久化，重启后：

- 已批准条目回到播放队列；
- 审查中/待审查条目转为人工复核，绝不自动视为低风险；
- 已播放/拦截/拒绝条目进入历史；
- 队列快照损坏时会保留备份并继续以安全状态启动。

### 批量抓取与缓冲

播放队列为空时，组件会按固定批次大小（3 条）抓取候选，并同时调用 AI；实际并发数受「最大并发审查数」限制。待复核数与播放队列数之和达到缓冲上限（12 条）后，组件暂停继续抓取，直到人工处理或播放腾出空间。

## 审计日志

日志目录：

```text
<PluginConfigFolder>\log\audit-yyyy-MM-dd.jsonl
```

`<PluginConfigFolder>` 是 ClassIsland 分配给本插件的配置目录。可在 AI 设置页或人工复核页点击 **打开审计日志目录**。

- 每行一个 JSON 事件，UTF-8 无 BOM，按本地日期分文件。
- 主要 `stage`：`quote_request`、`ai_request`、`ai_review`、`decision`、`manual_review`、`playback`、`queue_restored`、`queue_summary`、`queue_duplicate`、`queue_error`。
- 顶层公共字段包括 `timestamp`、`stage`、`refreshSessionId`、`itemId`、`providerId`、`upstreamId`、`correlationId`、`data`。
- `data` 中会包含来源请求 URL、上游 ID、AI 请求 ID、风险、置信度、理由、决策、人工备注或错误摘要等。
- 日志默认保留 30 天；可在 AI 设置页调整保留天数。
- `log` 设置可关闭日志；`LogFullPrompts` / `LogFullResponses` 可控制完整提示词和响应是否写入。即使关闭完整记录，仍建议把日志视为包含第三方引用内容的本地文件。
- 日志服务会按字段名脱敏 `apiKey`、`authorization`、`token`、`secret`、`cookie`、`password` 等字段，并替换文本中的 Bearer 值；这不是「可以随意贴出日志」的理由。

> 严禁把真实 API Key、完整 `Authorization` 请求头或包含密钥/个人信息的日志、截图发到公开 Issue、聊天群或仓库。
## 常见问题

### 长时间没有新句子

1. 检查播放队列是否已有内容；有队列时会优先播放，不会重复抓取。
2. 检查待复核数量。中风险内容不会自动显示，需要人工批准。
3. 检查组件刷新间隔是否为 `0`，以及来源是否启用、权重是否大于 0。
4. 检查日志中的 `quote_request`、`decision`、`queue_error`。
5. 缓冲区满时也会暂停抓取；清理复核队列或提高合理的缓冲上限。

### 所有内容都显示「中风险」

模型可能较保守，或内容确实需要人工判断。可先批量复核；若长期过多，换用更稳定的模型并审阅系统提示词。不要为了「少弹窗」把失败或中风险直接改成低风险自动通过。

### AI 超时或连接失败

- 检查 Base URL、协议和模型名是否正确。
- 在 AI 设置页点击「测试连接」。
- 适当提高请求超时（通常 30–90 秒），并检查本机网络、代理和 DNS。
- 查看日志中的 `ai_request`、`ai_review` 错误摘要。错误不会自动放行内容。

### AI 返回无法解析的 JSON

插件要求严格 JSON；模型若输出 Markdown、解释文字或缺少 `risk/confidence/categories/reason`，会被视为审查失败。可尝试更换模型，或让自定义系统提示词只补充审查标准，不要移除 JSON 约束。失败内容默认进入人工复核。

### 供应商返回 401/429

401 通常是 API Key 错误或未填写；429 表示限流或额度不足。检查密钥与控制台配额，稍后重试或切换到其他供应商。

### 使用 Ollama 本地模型

1. 本机安装并启动 Ollama。
2. 拉取预设模型，例如 `ollama pull qwen2.5:7b`。
3. 选择「Ollama 本地模型」预设，确认 Base URL 为 `http://127.0.0.1:11434/v1`、模型名一致。
4. 点击测试连接。若 ClassIsland 与 Ollama 不在同一用户/容器网络，需把地址改为实际可达的地址。

本地预设默认只访问回环地址；若手动改成远程地址，引用文本就会发送到该远程服务。

## 开发与验证

```powershell
# 仅构建
.\scripts\build.ps1

# 构建并生成 CIPX，再执行静态验收、构建和包内容检查
.\scripts\verify.ps1

# 可选：OpenAI 兼容端点连通性诊断（不属于常规构建），API Key 取自 OPENAI_API_KEY
.\scripts\test-public-ai.ps1 -Endpoint https://api.openai.com/v1/chat/completions -Model gpt-4o-mini
```

连通性测试只用于诊断，失败不会影响本地构建。实际执行过的命令、时间、产物和失败信息记录在 [docs/TEST_REPORT.md](docs/TEST_REPORT.md)。

## 更多文档

- [用户指南](docs/USER_GUIDE.md)
- [隐私与安全](docs/PRIVACY_SECURITY.md)
- [测试报告](docs/TEST_REPORT.md)

## 许可与第三方内容 / License

本项目以 **GNU Affero General Public License v3.0（AGPL-3.0）** 分发，完整文本见 [`LICENSE.txt`](LICENSE.txt)，并会随插件输出和 CIPX 包一起分发。名句文本、AI 模型输出及各供应商服务受对应第三方条款约束；本插件不对第三方内容的准确性、合法性或持续可用性作担保。

This project is distributed under the **GNU Affero General Public License v3.0**; the full text is in [`LICENSE.txt`](LICENSE.txt). The extracted and refactored quote-component/provider portions are derived from ExtraIsland by LiPolymer, which is also licensed under AGPLv3. Original IslandQuotes UI/service code and icon are provided under the same license. Quote texts, AI model outputs and third-party API services remain subject to their own terms.
