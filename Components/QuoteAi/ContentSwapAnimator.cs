using Avalonia.Controls;
using Avalonia.Threading;

namespace Motivation.Components.QuoteAi;

/// <summary>
/// 轻量的内容切换器。没有依赖自定义 XAML 转换器；即使计时器在控件卸载后触发，
/// 也只会安全地尝试恢复透明度，不会改变审查或播放队列状态。
/// </summary>
public sealed class ContentSwapAnimator
{
    readonly Label _label;
    int _generation;

    public ContentSwapAnimator(Label label)
    {
        _label = label;
    }

    public void Update(object? content, bool animationEnabled, bool swapAnimationEnabled)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Update(content, animationEnabled, swapAnimationEnabled));
            return;
        }

        int generation = Interlocked.Increment(ref _generation);
        _label.Content = content;

        if (!animationEnabled)
        {
            _label.Opacity = 1;
            return;
        }

        // 翻页模式使用更明显的淡入；普通模式只做短促的切换提示。
        _label.Opacity = swapAnimationEnabled ? 0.15 : 0.45;
        TimeSpan duration = TimeSpan.FromMilliseconds(swapAnimationEnabled ? 260 : 140);
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (generation == Volatile.Read(ref _generation)) _label.Opacity = 1;
        };
        timer.Start();
    }
}
