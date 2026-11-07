using IslandQuotes.Models;

namespace IslandQuotes.Services;

/// <summary>把组件设置中的一个自定义来源包装为名句来源。</summary>
public sealed class CustomQuoteProvider : IQuoteProvider
{
    readonly CustomQuoteSource _source;
    readonly object _sync = new();

    /// <summary>顺序播放取出顺序的全局序号；来源实例每次刷新都会重建，因此不能放在实例上。</summary>
    static long s_sequence;

    public CustomQuoteProvider(CustomQuoteSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public string Id => _source.ProviderId;

    public string DisplayName => string.IsNullOrWhiteSpace(_source.Name) ? "自定义来源" : _source.Name.Trim();

    public string Description => "自定义内容，每行一条";

    public bool IsEnabledByDefault => true;

    public int DefaultWeight => 1;

    // 用户自己编写的内容默认不送 AI 审查，可在来源设置中开启。
    public bool IsAiReviewEnabledByDefault => false;

    /// <summary>顺序播放需要按来源顺序取内容，不能并行抓取。</summary>
    public bool RequiresSequentialFetch => _source.PlayMode == CustomQuotePlayMode.Sequential;

    public Task<QuoteContent> FetchAsync(
        QuoteProviderConfig config,
        int lengthLimitation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string content;
        long sequence = 0;
        lock (_sync)
        {
            IReadOnlyList<string> entries = _source.GetEntries();
            if (entries.Count == 0)
                throw new InvalidOperationException($"自定义来源「{DisplayName}」没有内容。");

            if (_source.PlayMode == CustomQuotePlayMode.Random)
            {
                content = entries[Random.Shared.Next(entries.Count)];
            }
            else
            {
                int index = _source.NextIndex % entries.Count;
                content = entries[index];
                _source.NextIndex = (index + 1) % entries.Count;
                sequence = Interlocked.Increment(ref s_sequence);
            }
        }

        return Task.FromResult(new QuoteContent
        {
            Content = content,
            ProviderId = Id,
            ProviderName = DisplayName,
            RequestMethod = "LOCAL",
            RequestUrl = string.Empty,
            SourceSequence = sequence
        });
    }
}
