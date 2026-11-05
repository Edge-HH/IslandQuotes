# IslandQuotes 架构与内部契约

## 数据流

1. 组件定时调用 `ReviewQueueService.GetNextForDisplayAsync(options, ct)`。
2. 队列若有已批准项，按 FIFO 出队并写 `playback` 日志。
3. 否则按 `BatchSize` 从启用的每日一言来源抓取候选，每条写 `quote_request` 日志，包含 provider、请求 URL、上游 ID。
4. 按 `ItemsPerReviewRequest` 分组，经 `AiReviewService.ReviewBatchAsync` 合并请求（同组共用一份系统提示词），组间按 `MaxConcurrentReviews` 并发；每次请求写一条 `ai_request`，每个条目写一条 `ai_review`（同组共享 correlationId）。
5. `low` 自动批准并加入播放队列；`medium` 进入待人工复核；`high` 自动拦截。
6. AI 失败按 `ReviewSettings.FailureAction` 进入人工复核或拦截，绝不默认放行。
7. 人工复核页可逐条或批量批准/拒绝，批准项追加到播放队列末尾，操作写 `manual_review` 日志。

## 运行时单例

- `PluginRuntime.SettingsStore`：`<PluginConfigFolder>/settings.json`。
- `PluginRuntime.QuoteSources`：四个内置每日一言来源。
- `PluginRuntime.AiReview`：OpenAI 兼容、Anthropic、Gemini 协议审查。
- `PluginRuntime.AuditLog`：`<PluginConfigFolder>/log/audit-yyyy-MM-dd.jsonl`。
- `PluginRuntime.Queue`：批量审查、人工复核、FIFO 播放与快照恢复。

## 日志字段

每行 JSON 至少包含 `timestamp`、`stage`、`refreshSessionId`、`itemId`、`providerId`、`upstreamId`、`correlationId`、`data`。

Stage：`quote_request` / `queue_duplicate` / `ai_request` / `ai_review` / `decision` / `manual_review` / `playback` / `queue_summary` / `queue_restored` / `queue_error`。

密钥、Authorization、Cookie 永不写入日志。

## 安全边界

- 仅 `low` 以及人工批准的内容可以进入播放队列。
- `high` 内容不进入播放 UI，历史与日志 UI 必须遮蔽正文。
- 审查失败、解析失败、超时均 fail closed。
- 公用免密钥端点有隐私、限流与可用性风险，用户需自行确认。
- AI 分类不保证完全准确，人工复核是最终控制。
