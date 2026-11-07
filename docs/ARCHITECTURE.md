# IslandQuotes 架构与内部契约

## 数据流

1. 组件按刷新间隔调用 `ReviewQueueService.GetNextForDisplayAsync(options, waitForRefill, ct)`；`waitForRefill` 最多只等 3 秒，抓取与 AI 审查在独立的后台补货任务中继续，绝不会把展示节奏卡在一次长审查上。
2. 队列若有已批准项，按 FIFO 出队并写 `playback` 日志。
3. 否则确保后台补货在运行：按「`MaxQueueLength` - 当前播放队列长度」从启用的每日一言来源抓取候选（播放队列不多于 `RefillThreshold` 时组件也会主动补充），每条写 `quote_request` 日志，包含 provider、请求 URL、上游 ID。补货任务使用插件生命周期令牌，主界面隐藏导致组件脱离视觉树时不会中止在途的抓取与审查。
4. 需要审查的候选经 `AiReviewService.ReviewBatchAsync` 合并为一次请求（共用一份系统提示词）；出错的条目会合并重试，直到成功或达到 `MaxReviewAttempts`。每次请求写一条 `ai_request`，每个条目写一条 `ai_review`（同次请求共享 correlationId）。
5. 决策按批次内的 `ReviewPolicy` 快照执行：`low` 自动批准并加入播放队列（严格档下置信度低于 0.7 时提升为 `medium` 转人工复核）；`medium` 默认进入待人工复核（宽松档自动放行）；`high` 自动拦截。
6. AI 分类会先与禁止主题标签做本地匹配（`QuoteTagCatalog.FindBlockedTag`），命中即强制拦截并写 `blocked_tag`，与提示词中的主题规则互为兜底；随后才套用风险映射。
7. AI 失败按 `ReviewSettings.FailureAction` 进入人工复核或拦截，绝不默认放行。
8. 人工复核页可逐条或批量批准/拒绝，批准项追加到播放队列末尾，操作写 `manual_review` 日志。

## 刷新与补货语义

- 组件同时依赖宿主 `PostMainTimerTicked` 与一个 1 秒 `DispatcherTimer`；节流使用单调时钟（`Stopwatch`），系统时间被校准不会导致长时间不刷新。
- 一次刷新最多运行 90 秒；超时只释放刷新锁，不取消后台补货。下一次 tick 会重新检查队列。
- 后台补货同一时间只运行一个，候选抓取使用最多 4 个并发（仅有顺序自定义来源时串行）；抓取完成的直通候选立即入队，不等待整批或 AI 审查。AI 审查在独立后台队列中串行处理，不占用补货闸门。存在直通来源时播放缓冲固定为 20 条（不受「最大队列长度」限制），补货阈值取配置值与缓冲一半的较大者。补货目标为「有效上限 - 播放队列长度 - 正在 AI 审查的候选数」，送审候选不会因积压被抓取后丢弃，各来源的实际份额按权重（再乘以审查通过率）分配。
- 来源选择：每个抓取槽位按权重抽一次来源；内容超出长度限制时在同一来源内重试（最多 4 次），只有请求失败或该来源重试用尽时才排除它并在剩余来源中重抽（单次获取总计最多 8 次），避免通过率低的来源把份额让给不受长度限制的自定义来源。内容与播放队列、待复核列表或正在 AI 审查的候选重复时，同一槽位在原来源重抓（最多 3 次），不重新抽签。
- 顺序播放的自定义来源取内容时附带运行期递增序号；并发槽位或 AI 审查完成先后不定，入队时据此把条目移到同来源序号更大的条目之前，保持来源内顺序。
- 来源配置（启用、权重、AI 审查开关、长度限制）变化后，组件的每秒定时器即检测到并整理播放队列（不必等下一次刷新）；抓取或审查途中被停用来源的产出不再入队：移除已停用来源和超出新长度限制的条目，并丢弃未经审查的直通条目以按新权重重新抓取；已通过 AI 审查的条目保留。出队时只取当前组件仍启用的来源。
- 后台补货没有完成任何审查的批次（抓取失败、内容全部重复、全部拦截或待复核积压）按 10 秒、20 秒、40 秒、60 秒退避（最长 60 秒），任何人工批准/拒绝都会立即解除退避。只要批次实际加入了播放队列或待复核列表，就不触发退避。
- 队列为空且已有句子时，组件保留当前句子并在队列出现内容后提前刷新；首次加载或补货停止时才显示占位状态。
- 待人工复核达到 50 条上限时不再堆积新的待复核结果，但抓取与 AI 审查继续进行，供应商恢复后低风险内容可以立即恢复播放；所有启用来源都需要 AI 审查且活动供应商配置无效时暂停抓取，并在主界面显示原因。
- 队列快照在获取写入闸门之前采集，避免 UI 线程同步等待持久化时与「拿到闸门后回 UI 线程取快照」互相等待；并发采集只允许较新的快照落盘。完整请求体/提示词/原始响应只用于本次运行的诊断与审计日志，不写入 `review-queue.json`。

## 显示样式

- 切换动画：`ContentAnimationMode`（无、渐入渐出、上/下/左/右翻页、扫光）由 `ContentSwapAnimator` 按渲染帧实现；旧版 `IsAnimationEnabled` / `IsSwapAnimationEnabled` 只在读取旧设置时用于推导模式。
- 扫光通过在 `Foreground` 上临时套用移动的 `LinearGradientBrush` 实现，扫光结束后恢复原画刷。
- 长内容滚动：`ScrollMaxLength` 大于 0 且文本超过该字数时，不参与平移的父级视口把宽度限制为前 N 个字符的测量宽度，标签按完整文本宽度平移并左右来回滚动；滚动与切换动画共用同一个帧循环和 `TranslateTransform`。
- 字体、字号、字重、主文字颜色、额外信息字号/颜色、垂直微调由组件每秒检查一次并套用到标签；留空/0 表示继承主界面主题值。扫光颜色留空时按文字明暗使用纯白/纯黑；滚动速度可配置。


## 运行时单例

- `PluginRuntime.SettingsStore`：`<PluginConfigFolder>/settings.json`。
- `PluginRuntime.QuoteSources`：四个内置每日一言来源。
- `PluginRuntime.AiReview`：OpenAI 兼容、Anthropic、Gemini 协议审查。
- `PluginRuntime.AuditLog`：`<PluginConfigFolder>/log/audit-yyyy-MM-dd.jsonl`。
- `PluginRuntime.Queue`：批量审查、人工复核、FIFO 播放与快照恢复。

## 宿主版本兼容

插件以最低支持版本（ClassIsland.PluginSdk 2.1.0.1 / Avalonia 11）的 API 构建，同一个 CIPX 需同时运行在 Avalonia 11（2.1.0.x）与 Avalonia 12（2.1.1.1 起）的宿主上：

- **XAML 不在构建期编译**（`EnableAvaloniaXamlCompilation=false`）。`.axaml` 作为嵌入资源，由 `Core/RuntimeXaml.cs` 通过反射调用宿主自带的 `AvaloniaRuntimeXamlLoader` 加载（不静态引用 `Avalonia.Markup.Xaml.Loader`：ClassIsland 2.1.0.1 自带的是 11.0.0.0，静态引用更高版本会加载失败）。构建期编译的 XAML 会写死 Avalonia 内部方法签名（如 `DynamicResourceExtension.ProvideValue`），跨大版本即 `MissingMethodException`。
- XAML 绑定/标记扩展里**不要引用插件自身的类型**（如 `AncestorType=local:QuoteAiSettings`、`{x:Type local:...}`）：宿主把插件放在独立的 AssemblyLoadContext 中，运行时编译器解析不到，会报 `Unable to resolve type`。需要时在 code-behind 里设置（如 `DataContext = this`）。根元素的 `x:Class` / `x:TypeArguments` 不受影响。
- 有 `x:Name` 的控件在 code-behind 的 `InitializeComponent()` 里用 `FindControl` 取得（名称生成器已关闭）。
- FluentAvalonia 3 给控件加了 `FA` 前缀（`SettingsExpander` → `FASettingsExpander`），`RuntimeXaml` 按宿主实际加载的 FluentAvalonia 改写 XAML 中的控件名。
- C# 代码只能使用两个版本签名一致的宿主 API。例如 `AppBase.GetRootWindow()` 的返回类型在 2.1.1.1 由 `Window` 改为 `TopLevel`，因此改用 `AppBase.MainWindow`。

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
