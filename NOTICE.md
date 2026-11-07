# NOTICE

IslandQuotes
Copyright (C) 2026 Edge-HH

本程序是自由软件：你可以依据 GNU Affero General Public License 第 3 版的条款重新分发和/或修改它。本程序的发布是希望它有用，但**不提供任何担保**，甚至不包括适销性或特定用途适用性的默示担保。详见随附的 [`LICENSE.txt`](LICENSE.txt)。

## 第三方代码：ExtraIsland

本项目的部分代码改编自 **ExtraIsland** 的「名句一言」（Rhesis）组件：

- 项目：<https://github.com/LiPolymer/ExtraIsland>
- 版权：Copyright (C) 2024-2025 LiPolymer
- 许可：GNU Affero General Public License v3.0

| IslandQuotes 文件 | 改编自 ExtraIsland 文件 |
| --- | --- |
| `Services/RhesisProviders.cs` | `ExtraIsland/Shared/RhesisProviders.cs` |
| `Components/QuoteAi/QuoteAiConfig.cs` | `ExtraIsland/Components/Rhesis/RhesisConfig.cs` |
| `Components/QuoteAi/QuoteAiComponent.axaml` | `ExtraIsland/Components/Rhesis/Rhesis.axaml` |
| `Components/QuoteAi/QuoteAiComponent.axaml.cs` | `ExtraIsland/Components/Rhesis/Rhesis.axaml.cs` |
| `Components/QuoteAi/ContentSwapAnimator.cs` | `ExtraIsland/Shared/Animators.cs`（`GenericContentSwapAnimator`） |
| `Components/QuoteAi/QuoteAiSettings.axaml` | `ExtraIsland/Components/Rhesis/RhesisSettings.axaml` |
| `Components/QuoteAi/QuoteAiSettings.axaml.cs` | `ExtraIsland/Components/Rhesis/RhesisSettings.axaml.cs` |

上述文件已于 2026 年由 Edge-HH 修改，主要改动包括：

- 从 ExtraIsland 中拆出，改为独立插件，命名空间改为 `IslandQuotes`；
- 名句来源接口与抓取逻辑重构，新增自定义来源（顺序/随机播放、txt 导入）；
- 新增展示前 AI 审查、人工复核、FIFO 播放队列与审计日志的接入；
- 组件设置界面与配置项调整。

具体修改记录见本仓库的 git 历史。其余文件为 IslandQuotes 原创，同样以 AGPL-3.0 授权。`icon.png` / `icon.svg` 为原创图形，未使用 ExtraIsland 的图片素材。

## 名称与商标

ClassIsland、ExtraIsland 名称归各自权利人所有。IslandQuotes 是独立项目，与 ExtraIsland 及其作者没有隶属或背书关系。
