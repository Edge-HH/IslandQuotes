using System.Text.Json.Serialization;
using IslandQuotes.Core;

namespace IslandQuotes.Models;

public enum CustomQuotePlayMode
{
    Sequential = 0,
    Random = 1
}

/// <summary>用户在组件设置中添加的自定义来源，每行一条内容。</summary>
public sealed class CustomQuoteSource : ObservableObject
{
    public const string ProviderIdPrefix = "custom-";

    string _id = Guid.NewGuid().ToString("N");

    public string Id
    {
        get => _id;
        set => SetProperty(ref _id, string.IsNullOrWhiteSpace(value) ? Guid.NewGuid().ToString("N") : value);
    }

    [JsonIgnore]
    public string ProviderId => ProviderIdPrefix + Id;

    string _name = "自定义来源";

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value ?? string.Empty);
    }

    string _content = string.Empty;

    /// <summary>自定义内容，一行一条。</summary>
    public string Content
    {
        get => _content;
        set
        {
            if (!SetProperty(ref _content, value ?? string.Empty)) return;
            OnPropertyChanged(nameof(EntryCount));
        }
    }

    CustomQuotePlayMode _playMode = CustomQuotePlayMode.Sequential;

    public CustomQuotePlayMode PlayMode
    {
        get => _playMode;
        set
        {
            if (!SetProperty(ref _playMode, value)) return;
            OnPropertyChanged(nameof(PlayModeIndex));
        }
    }

    /// <summary>供 ComboBox 的 SelectedIndex 绑定，0 为顺序播放，1 为随机播放。</summary>
    [JsonIgnore]
    public int PlayModeIndex
    {
        get => PlayMode == CustomQuotePlayMode.Random ? 1 : 0;
        set => PlayMode = value == 1 ? CustomQuotePlayMode.Random : CustomQuotePlayMode.Sequential;
    }

    int _nextIndex;

    /// <summary>顺序播放时下一条内容的行号（按非空行计），用于记忆上次播放到的位置。</summary>
    public int NextIndex
    {
        get => _nextIndex;
        set => SetProperty(ref _nextIndex, Math.Max(0, value));
    }

    [JsonIgnore]
    public int EntryCount => GetEntries().Count;

    public IReadOnlyList<string> GetEntries()
    {
        return Content
            .Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Length > 0)
            .ToArray();
    }
}
