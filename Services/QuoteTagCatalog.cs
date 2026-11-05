using System.Globalization;
using IslandQuotes.Models;

namespace IslandQuotes.Services;

/// <summary>单个快速控制主题标签：显示名、提示词说明与用于匹配 AI 分类的别名。</summary>
public sealed record QuoteTagDefinition(
    string Label,
    string PromptHint,
    IReadOnlyList<string> Aliases);

/// <summary>内置主题标签目录与标签匹配逻辑；标签名与别名仅用于运营者快速控制，不改变安全边界的优先级。</summary>
public static class QuoteTagCatalog
{
    /// <summary>设置界面展示的内置主题标签；顺序即界面顺序。</summary>
    public static IReadOnlyList<QuoteTagDefinition> Tags { get; } =
    [
        new("热梗/网络流行语", "网络流行语、热梗、玩梗、缩写梗与社交媒体流行表达",
            ["热梗", "网络流行语", "流行语", "玩梗", "梗图", "meme"]),
        new("幽默/搞笑", "段子、冷笑话、谐音梗与轻松搞笑内容", ["幽默", "搞笑", "段子", "笑话", "趣味"]),
        new("爱情/恋爱", "恋爱、告白、情侣与婚姻情感主题", ["爱情", "恋爱", "情侣", "告白", "婚恋", "浪漫"]),
        new("情感/伤感", "离别、遗憾、孤独等抒情伤感内容", ["伤感", "孤独", "遗憾", "抒情", "emo"]),
        new("亲情/友情", "家人、友情与师生情谊主题", ["亲情", "友情", "家人", "友谊", "师生"]),
        new("励志/成长", "奋斗、自律、成长与激励主题", ["励志", "成长", "奋斗", "激励", "自律", "鸡汤"]),
        new("哲理/思考", "人生哲理、思辨与格言警句", ["哲理", "哲学", "思辨", "格言", "人生感悟"]),
        new("文学/诗词", "古典诗词、散文与现代文学摘录", ["文学", "诗词", "诗歌", "散文", "名著"]),
        new("校园/学习", "校园生活、读书学习方法与考试相关", ["校园", "学习", "读书", "考试", "课堂"]),
        new("自然/生活", "自然风景、季节、动植物与日常生活", ["自然", "风景", "季节", "生活", "旅行", "植物"]),
        new("历史/文化", "历史人物事件、传统文化与民俗", ["历史", "文化", "传统", "民俗", "国学"]),
        new("科普/科技", "科学知识、技术与数码相关", ["科普", "科学", "科技", "数码", "人工智能"]),
        new("体育/运动", "体育竞技与运动健身", ["体育", "运动", "健身", "竞技", "球赛"]),
        new("追星/娱乐", "明星偶像、影视综艺与饭圈话题", ["追星", "明星", "偶像", "娱乐", "综艺", "饭圈", "影视"]),
        new("二次元/游戏", "动漫、漫画、游戏与虚拟主播", ["二次元", "动漫", "动画", "漫画", "游戏", "虚拟主播", "ACG"]),
        new("商业/财经", "财经、商业与职场话题", ["财经", "商业", "金融", "职场", "投资"]),
        new("玄学/星座", "星座运势、占卜与玄学内容", ["玄学", "星座", "占卜", "运势", "塔罗"]),
        new("恐怖/惊悚", "恐怖故事、惊悚描写与灵异题材", ["恐怖", "惊悚", "灵异", "鬼故事", "血腥"])
    ];

    /// <summary>按显示名查找标签定义；未收录的自定义标签返回 null。</summary>
    public static QuoteTagDefinition? Find(string? label)
    {
        string trimmed = (label ?? string.Empty).Trim();
        if (trimmed.Length == 0) return null;

        foreach (QuoteTagDefinition tag in Tags)
        {
            if (string.Equals(tag.Label, trimmed, StringComparison.OrdinalIgnoreCase) ||
                tag.Aliases.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                return tag;
            }
        }

        return null;
    }

    /// <summary>规范化标签列表：去空白、去空项与超长项、按大小写不敏感去重。</summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string?>? tags)
    {
        if (tags is null) return [];

        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? tag in tags)
        {
            string trimmed = (tag ?? string.Empty).Trim();
            if (trimmed.Length == 0 || trimmed.Length > 40 || !seen.Add(trimmed)) continue;
            normalized.Add(trimmed);
        }

        return normalized;
    }

    /// <summary>
    /// 返回 AI 分类命中禁止标签时命中的标签名；按「分类与标签名/别名互为包含」匹配，未命中返回 null。
    /// </summary>
    public static string? FindBlockedTag(
        IEnumerable<string>? categories,
        IReadOnlyList<string> blockedTags)
    {
        if (categories is null || blockedTags.Count == 0) return null;

        var keywords = CollectKeywords(blockedTags);
        if (keywords.Count == 0) return null;

        foreach (string category in categories)
        {
            string normalizedCategory = Normalize(category);
            if (normalizedCategory.Length == 0) continue;

            foreach (var keyword in keywords)
            {
                if (Match(normalizedCategory, keyword)) return keyword.Original;
            }
        }

        return null;
    }

    private static List<(string Original, string Normalized)> CollectKeywords(IReadOnlyList<string> tags)
    {
        List<(string Original, string Normalized)> keywords = [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string label in tags)
        {
            QuoteTagDefinition? definition = Find(label);
            if (definition is null)
            {
                AddKeyword(label, keywords, seen);
                continue;
            }

            AddKeyword(definition.Label, keywords, seen);
            foreach (string alias in definition.Aliases) AddKeyword(alias, keywords, seen);
        }

        return keywords;
    }

    private static void AddKeyword(
        string value,
        List<(string Original, string Normalized)> keywords,
        ISet<string> seen)
    {
        string normalized = Normalize(value);
        if (normalized.Length < 2) return; // 过短的别名容易误伤正常分类。
        if (!seen.Add(normalized)) return;

        keywords.Add((value.Trim(), normalized));
    }

    private static bool Match(string normalizedCategory, (string Original, string Normalized) keyword)
    {
        string label = Normalize(keyword.Original);
        // 分类包含关键词（如「网络流行语/热梗」命中「热梗」），或关键词整体包含分类（分类过短时）。
        return normalizedCategory.Contains(keyword.Normalized, StringComparison.OrdinalIgnoreCase) ||
               (label.Length >= 2 && label.Contains(normalizedCategory, StringComparison.OrdinalIgnoreCase));
    }

    private static string Normalize(string? value)
    {
        return (value ?? string.Empty).Trim().ToLower(CultureInfo.InvariantCulture);
    }
}
