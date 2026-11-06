# IslandQuotes

[![CI](https://github.com/Edge-HH/IslandQuotes/actions/workflows/ci.yml/badge.svg)](https://github.com/Edge-HH/IslandQuotes/actions/workflows/ci.yml)
[![Release](https://github.com/Edge-HH/IslandQuotes/actions/workflows/release.yml/badge.svg)](https://github.com/Edge-HH/IslandQuotes/actions/workflows/release.yml)
[![License: AGPL v3](https://img.shields.io/badge/License-AGPL_v3-blue.svg)](LICENSE.txt)

IslandQuotes 是一个面向 **ClassIsland v2** 的每日一言插件。它在名句真正显示到班级大屏之前，先调用所选的 AI 供应商做内容风险审查：只有低风险内容自动通过，中风险内容进入人工复核，高风险内容自动拦截。

> 插件 ID：`ink.islandquotes.quoteai`
> 入口程序集：`IslandQuotes.dll`
> 目标框架：.NET 8（`net8.0-windows`）
> 宿主要求：ClassIsland **2.1.0.1 或更新版本**（插件 API `2.0.0.0`，以 ClassIsland.PluginSdk `2.1.0.1` 构建）

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

## 安装

1. 前往 [Releases](https://github.com/Edge-HH/IslandQuotes/releases/latest) 下载最新的 `IslandQuotes.cipx`（可核对随附 `.sha256` 校验文件）。
2. 打开 ClassIsland 的插件管理界面，选择导入本地插件包，选中下载的 `.cipx` 文件；具体导入入口以宿主版本界面为准。
3. 把 **GoodQuotes** 组件加入需要的主界面/组件配置。
4. 按[用户指南](docs/USER_GUIDE.md)完成 AI 审查供应商与内容来源配置。

环境要求：Windows 10/11，ClassIsland v2 ≥ 2.1.0.1。

## 快速配置

1. 打开插件设置中的 **AI审查设置** 页面，在供应商预设中选择一个配置。默认启用 **OpenAI 兼容**，需要自行填写 Base URL、模型与 API Key。
2. API Key 也可填写 `env:OPENAI_API_KEY` 这样的环境变量引用，避免把密钥直接写进 `settings.json`。
3. 点击 **保存设置**，再点击 **测试连接**。测试会发送一条无害的内置引用到当前供应商。
4. 回到组件设置，选择名句来源（以及各来源是否启用 AI 审查）、刷新间隔和排除列表。

### 内置供应商预设

| 预设 | 默认协议 | 密钥 |
| --- | --- | --- |
| OpenAI 兼容（默认，需自行填写地址与模型） | OpenAI 兼容 | 按服务要求 |
| OpenAI、DeepSeek、Moonshot（月之暗面）、Xiaomi MiMo、DashScope / 通义千问、智谱 GLM、SiliconFlow、Groq、Together AI | OpenAI 兼容 | 需要 |
| Google Gemini | Gemini 原生 | 需要 |
| Anthropic Claude | Anthropic 原生 | 需要 |
| Ollama 本地模型（默认 `qwen2.5:7b`，`http://127.0.0.1:11434/v1`） | OpenAI 兼容 | 默认不需要 |

模型可用性、限速与价格以各供应商控制台为准。待审文本会发送给所选供应商，请先阅读其隐私政策；在意隐私时请选择本地模型或自建/可信供应商，详见[隐私与安全说明](docs/PRIVACY_SECURITY.md)。

## 人工复核与播放队列

- **待复核**：中风险、AI 失败但采用「转人工复核」策略的项目。可单条或批量「批准/拒绝」。
- **播放队列**：自动通过或人工批准的条目按加入顺序追加到队尾；显示时才从队首取出（FIFO）。拒绝的项目永不播放。
- **历史**：人工拒绝、高风险拦截和已播放内容。高风险/已拦截条目的正文与署名统一显示为「高风险内容已隐藏」，供应商和上游 ID 仍保留用于审计。

队列状态会持久化；重启后已批准条目回到播放队列，审查中/待审查条目转为人工复核，绝不自动视为低风险。批量抓取、缓冲上限与 FIFO 完整语义见[用户指南](docs/USER_GUIDE.md)。

## 审计日志

日志写入 `<PluginConfigFolder>\log\audit-yyyy-MM-dd.jsonl`：每日一个 UTF-8 JSONL 文件（默认保留 30 天），记录来源请求、AI 请求/审查、决策、人工复核与播放事件；日志服务会自动脱敏 `apiKey`、`authorization` 等敏感字段。`<PluginConfigFolder>` 是 ClassIsland 分配给本插件的配置目录，可在 AI 设置页或人工复核页点击 **打开审计日志目录**。

> 严禁把真实 API Key、完整 `Authorization` 请求头或包含密钥/个人信息的日志、截图发到公开 Issue、聊天群或仓库。

## 从源码构建

### 环境要求

- Windows 10/11
- .NET SDK **10 或更新版本**（构建脚本会强制检查；插件目标框架为 .NET 8，与 ClassIsland 2.1.0.1 宿主运行时一致）
- 首次还原 NuGet 包时需要访问 NuGet 源（或预先配置好可用的离线源）

构建脚本优先使用仓库内 SDK（`.tools\dotnet\dotnet.exe`）；不存在时才使用 `PATH` 中的 `dotnet`。

### 标准命令

在仓库根目录执行：

```powershell
dotnet build IslandQuotes.csproj -c Release -p:CreateCipx=true
```

或使用构建/验证脚本（会自动检查 SDK 主版本并打印产物）：

```powershell
.\scripts\build.ps1 -CreateCipx
.\scripts\verify.ps1
```

> 如果 CIPX 打包阶段提示找不到 `pwsh`（只安装了 Windows PowerShell 5.1 的环境），可在命令后追加 `-p:PowershellBinaryName=powershell.exe`。`scripts/build.ps1 -CreateCipx` 会自动检测并处理该情况。

### 产物

| 产物 | 路径 |
| --- | --- |
| 插件程序集 | `bin\Release\net8.0-windows\IslandQuotes.dll` |
| 清单 | `bin\Release\net8.0-windows\manifest.yml` |
| 图标 | `bin\Release\net8.0-windows\icon.png` |
| ClassIsland 插件包 | `cipx\IslandQuotes.cipx` |

`IslandQuotes.cipx` 由 `ClassIsland.PluginSdk` 的 `CreateCipx` 目标打包 `bin\Release\net8.0-windows\` 生成。

本仓库的 CI 在每次推送到 `main` / PR 时运行 `verify.ps1`（静态验收 + Release 构建 + CIPX 包内容检查）并上传 CIPX 产物；每次在 GitHub 上发布 Release 时，Release 工作流会为该 Release 的 tag 重新构建 `IslandQuotes.cipx`（及 `.sha256` 校验文件）并覆盖 Release 中的同名附件（也可在 Actions 中手动运行并填写 tag 来重新覆盖）。发布前请确保 `manifest.yml` 的 `version` 与 tag 一致（如 `1.0.0.0` 对应 `v1.0.0`）。

## 开发与验证

```powershell
# 仅构建
.\scripts\build.ps1

# 构建并生成 CIPX，再执行静态验收、构建和包内容检查
.\scripts\verify.ps1

# 可选：OpenAI 兼容端点连通性诊断（不属于常规构建），API Key 取自 OPENAI_API_KEY
.\scripts\test-public-ai.ps1 -Endpoint https://api.openai.com/v1/chat/completions -Model gpt-4o-mini
```

连通性测试只用于诊断，失败不会影响本地构建。实际执行过的命令、时间、产物和失败信息记录在 [docs/TEST_REPORT.md](docs/TEST_REPORT.md)（测试报告只记录实际执行结果）。

## 文档

| 文档 | 内容 |
| --- | --- |
| [用户指南](docs/USER_GUIDE.md) | 安装升级、AI 供应商配置、来源与组件设置、复核流程、FIFO 队列语义、排障 |
| [隐私与安全](docs/PRIVACY_SECURITY.md) | 信任边界、发送给 AI 供应商的数据、API Key 存储、日志脱敏、安全使用清单 |
| [架构说明](docs/ARCHITECTURE.md) | 数据流、运行时单例、日志字段与安全边界 |
| [测试报告](docs/TEST_REPORT.md) | 实际执行的构建与验收记录 |

## 来源与致谢

本项目把 [ExtraIsland](https://github.com/Lipoly/ExtraIsland) 中的「名句一言」组件提取、重构为独立插件，并新增 AI 预展示审查、人工复核、FIFO 播放队列和审计能力。本仓库的界面、文案、队列服务和图标均为重新实现；`icon.png` 为原创盾牌/引号/星光图形，没有复用 ExtraIsland 的图片素材。

## 许可 / License

本项目以 **GNU Affero General Public License v3.0（AGPL-3.0）** 分发，完整文本见 [`LICENSE.txt`](LICENSE.txt)，并随插件输出和 CIPX 包一起分发。名句文本、AI 模型输出及各供应商服务受对应第三方条款约束；本插件不对第三方内容的准确性、合法性或持续可用性作担保。

This project is distributed under the **GNU Affero General Public License v3.0**; the full text is in [`LICENSE.txt`](LICENSE.txt). The extracted and refactored quote-component/provider portions are derived from [ExtraIsland](https://github.com/Lipoly/ExtraIsland) by LiPolymer, which is also licensed under AGPLv3. Original IslandQuotes UI/service code and icon are provided under the same license. Quote texts, AI model outputs and third-party API services remain subject to their own terms.
