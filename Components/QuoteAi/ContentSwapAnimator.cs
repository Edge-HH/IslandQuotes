// IslandQuotes - Copyright (C) 2026 Edge-HH
// 本文件改编自 ExtraIsland（https://github.com/LiPolymer/ExtraIsland），
// Copyright (C) 2024-2025 LiPolymer，以 GNU AGPLv3 授权。
// 原始文件：ExtraIsland/Shared/Animators.cs 中的 GenericContentSwapAnimator
// 修改：2026 年由 Edge-HH 修改，详见 NOTICE.md 与 git 历史。
// SPDX-License-Identifier: AGPL-3.0-only

using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;

namespace IslandQuotes.Components.QuoteAi;

/// <summary>
/// 内容切换动画器。支持淡入淡出、四个方向的翻页、扫光，以及内容过长时的
/// 左右来回滚动（对齐 ClassIsland 自带滚动容器的观感）。
/// </summary>
/// <remarks>
/// 不使用 Avalonia 的 Animation/KeyFrame：FillMode.Forward 的动画实例在 RunAsync 结束后不会释放，
/// 每次切换都会在标签的值存储里多留几层动画优先级的值，越用越慢，且会压住本地值，
/// 导致中途打断后无法把透明度和位移恢复。这里改为按渲染帧（RequestAnimationFrame）直接写本地值：
/// 进度按时间计算，低端硬件掉帧时只是帧数变少，时长不变；新的更新随时可以从当前状态接管，无需锁。
/// 滚动和扫光使用同一套帧循环，避免多个计时器同时争用同一个 TranslateTransform。
/// </remarks>
public sealed class ContentSwapAnimator
{
    /// <summary>翻页位移量，对应上游 40 * motionMultiple(0.5)。</summary>
    const double MotionPixels = 20.0;

    static readonly TimeSpan SlidePhaseDuration = TimeSpan.FromMilliseconds(150);
    static readonly TimeSpan FadePhaseDuration = TimeSpan.FromMilliseconds(125);
    static readonly TimeSpan ShimmerDuration = TimeSpan.FromMilliseconds(900);
    static readonly TimeSpan ScrollHoldDuration = TimeSpan.FromMilliseconds(1300);
    static readonly TimeSpan ScrollMinDuration = TimeSpan.FromSeconds(1.2);
    static readonly TimeSpan ScrollMaxDuration = TimeSpan.FromSeconds(9);
    const double ShimmerBandHalfWidth = 0.16;

    enum Phase { Idle, Out, In, Scroll }

    /// <summary>渲染帧停止送达超过该时长后强制落地内容，避免标签停在半透明或错位状态。</summary>
    static readonly TimeSpan FrameStallTimeout = TimeSpan.FromSeconds(2);

    readonly Label _label;

    /// <summary>不参与平移的固定视口；裁剪由它完成，Label 只负责内容位移。</summary>
    readonly Control _viewport;

    /// <summary>宿主分配给组件的实际宽度；用于在宿主压缩组件时仍能看到长内容末尾。</summary>
    readonly Control _root;

    readonly TranslateTransform _transform;

    /// <summary>不加入视觉树的测量用文本块，只用稳定 API 估算文本宽度。</summary>
    readonly TextBlock _probe = new() { TextWrapping = TextWrapping.NoWrap };

    ContentAnimationMode _mode = ContentAnimationMode.Fade;
    ContentAnimationMode _outMode = ContentAnimationMode.Fade;
    Phase _phase;
    object? _targetContent;
    object? _displayedContent;
    string _currentText = string.Empty;
    int _scrollMaxLength;
    double _viewportWidth;
    double _scrollDistance;
    TimeSpan? _phaseStart;
    TimeSpan? _scrollStart;
    double _fromOpacity;
    double _fromX;
    double _fromY;
    int _frameToken;
    bool _frameRequested;
    long _lastFrameTimestamp;

    IBrush? _normalBrush;
    IBrush? _shimmerColorBrush;
    int _scrollPixelsPerSecond = 42;
    LinearGradientBrush? _shimmerBrush;
    GradientStop? _shimmerBefore;
    GradientStop? _shimmerHighlight;
    GradientStop? _shimmerAfter;
    bool _shimmerActive;

    public ContentSwapAnimator(Label label, Control viewport, Control root)
    {
        _label = label;
        _viewport = viewport;
        _root = root;
        _viewport.ClipToBounds = true;
        if (label.RenderTransform is TranslateTransform transform)
        {
            _transform = transform;
        }
        else
        {
            _transform = new TranslateTransform();
            label.RenderTransform = _transform;
        }

        // 脱离视觉树后不再有渲染帧，直接落地已显示的内容，避免停在半透明或错位状态。
        // 仅当确实有动画/滚动在进行时才处理；空闲时不能把 Content 改回 null。
        label.DetachedFromVisualTree += (_, _) =>
        {
            if (_phase == Phase.Idle) return;
            object? content = _targetContent ?? _displayedContent;
            Finish(content, content?.ToString() ?? _currentText);
        };
    }

    /// <summary>
    /// 更新内容并播放切换动画。<paramref name="scrollMaxLength"/> 大于 0 且内容超过该字数时，
    /// 标签会限制在对应宽度内并左右滚动显示。
    /// </summary>
    public void Update(object? content, ContentAnimationMode mode, int scrollMaxLength)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Update(content, mode, scrollMaxLength));
            return;
        }

        string text = content?.ToString() ?? string.Empty;
        int normalizedMaxLength = Math.Max(0, scrollMaxLength);
        bool sameContent = string.Equals(text, _currentText, StringComparison.Ordinal);

        // 重复显示相同内容时不要重放动画；只需按最新的字体/长度设置刷新几何信息。
        if (sameContent && _phase is Phase.Idle or Phase.Scroll)
        {
            double oldDistance = _scrollDistance;
            double oldViewportWidth = _viewportWidth;

            _mode = mode;
            _scrollMaxLength = normalizedMaxLength;
            UpdateViewport(text);
            RefreshScrollAfterGeometryChange(oldDistance, oldViewportWidth);
            return;
        }

        StopShimmer();
        _mode = mode;
        _scrollMaxLength = normalizedMaxLength;
        _targetContent = content;

        TopLevel? topLevel = mode == ContentAnimationMode.None ? null : TopLevel.GetTopLevel(_label);
        if (topLevel is null)
        {
            Finish(content, text);
            return;
        }

        // 淡出途中再次更新：继续当前淡出，结束时直接换成最新内容。
        if (_phase == Phase.Out) return;

        // 空闲、淡入或滚动途中：从当前透明度与位移开始淡出，不会跳变。
        BeginOutPhase();
        RequestFrame(topLevel);
    }

    /// <summary>应用自定义文字颜色；传 null 表示恢复主题颜色。扫光进行中时不会打断当前扫光。</summary>
    public void SetNormalForeground(IBrush? brush)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => SetNormalForeground(brush));
            return;
        }

        _normalBrush = brush;
        if (!_shimmerActive) ApplyNormalForeground();
    }

    /// <summary>设置扫光高光颜色；传 null 表示自动使用文字本色的半透明。</summary>
    public void SetShimmerColor(IBrush? brush)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => SetShimmerColor(brush));
            return;
        }

        _shimmerColorBrush = brush;
        if (_shimmerHighlight is not null)
            _shimmerHighlight.Color = ResolveShimmerColor(GetBaseColor());
    }

    /// <summary>设置长内容滚动速度，单位像素/秒。</summary>
    public void SetScrollSpeed(int pixelsPerSecond)
    {
        _scrollPixelsPerSecond = Math.Clamp(pixelsPerSecond, 5, 500);
    }

    /// <summary>字体、字号或滚动长度变化后重新计算滚动几何信息。</summary>
    public void RefreshMetrics(int scrollMaxLength)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => RefreshMetrics(scrollMaxLength));
            return;
        }

        _scrollMaxLength = Math.Max(0, scrollMaxLength);

        if (_phase is Phase.Out or Phase.In) return;

        double oldDistance = _scrollDistance;
        double oldViewportWidth = _viewportWidth;
        UpdateViewport(_currentText);
        if (_phase is Phase.Idle or Phase.Scroll)
            RefreshScrollAfterGeometryChange(oldDistance, oldViewportWidth);
    }

    /// <summary>组件重新附着到视觉树后调用，恢复之前被打断的内容与滚动。</summary>
    public void Resume()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(Resume);
            return;
        }

        if (_label.Content is null && _displayedContent is not null)
            _label.Content = _displayedContent;

        UpdateViewport(_currentText);
        if (_phase is Phase.Idle or Phase.Scroll)
        {
            if (_scrollDistance > 0.5)
            {
                StartScroll();
            }
            else
            {
                _phase = Phase.Idle;
                _transform.X = 0;
            }
        }
    }

    /// <summary>
    /// 由组件的秒级定时器调用：渲染帧可能因窗口隐藏、最小化或 UI 线程阻塞而停止送达，
    /// 这里在长时间没有帧时强制落地内容，避免内容切换停在半透明或滚动卡死。
    /// </summary>
    public void Tick()
    {
        if (!Dispatcher.UIThread.CheckAccess()) return;
        if (_phase == Phase.Idle) return;

        long now = Stopwatch.GetTimestamp();
        double stalled = _lastFrameTimestamp == 0
            ? double.PositiveInfinity
            : (now - _lastFrameTimestamp) / (double)Stopwatch.Frequency;

        if (stalled < FrameStallTimeout.TotalSeconds) return;

        if (_phase is Phase.Out or Phase.In)
        {
            object? content = _targetContent ?? _displayedContent;
            Finish(content, content?.ToString() ?? _currentText);
            return;
        }

        if (_phase == Phase.Scroll)
        {
            RestartScroll();
        }
    }

    void BeginOutPhase()
    {
        _phase = Phase.Out;
        _phaseStart = null;
        _lastFrameTimestamp = Stopwatch.GetTimestamp();
        _outMode = _mode;
        _fromOpacity = _label.Opacity;
        _fromX = _transform.X;
        _fromY = _transform.Y;
    }

    void RequestFrame(TopLevel topLevel)
    {
        if (_frameRequested) return;
        _frameRequested = true;
        int token = _frameToken;
        topLevel.RequestAnimationFrame(time => OnFrame(time, token));
    }

    void OnFrame(TimeSpan time, int token)
    {
        if (token != _frameToken) return;
        _frameRequested = false;
        if (_phase == Phase.Idle) return;

        _lastFrameTimestamp = Stopwatch.GetTimestamp();

        try
        {
            _phaseStart ??= time;

            switch (_phase)
            {
                case Phase.Out:
                    StepOut(time);
                    break;
                case Phase.In:
                    StepIn(time);
                    break;
                case Phase.Scroll:
                    StepScroll(time);
                    break;
            }
        }
        catch
        {
            // 渲染回调异常时直接落地内容，避免标签停留在透明或错位状态。
            Finish(_targetContent, _targetContent?.ToString() ?? _currentText);
            return;
        }

        if (_phase == Phase.Idle) return;

        TopLevel? topLevel = TopLevel.GetTopLevel(_label);
        if (topLevel is null)
        {
            Finish(_targetContent, _targetContent?.ToString() ?? _currentText);
            return;
        }

        RequestFrame(topLevel);
    }

    void StepOut(TimeSpan time)
    {
        TimeSpan duration = _outMode is ContentAnimationMode.SlideUp
            or ContentAnimationMode.SlideDown
            or ContentAnimationMode.SlideLeft
            or ContentAnimationMode.SlideRight
            ? SlidePhaseDuration
            : FadePhaseDuration;
        double progress = Progress(time, duration);
        double eased = progress * progress; // QuadraticEaseIn

        // 退出位移是相对于当前位置的增量：滚动到一半时切换内容也不会先往回跳。
        (double deltaX, double deltaY) = GetExitDelta(_outMode);
        double exitX = _fromX + deltaX;
        double exitY = _fromY + deltaY;
        _label.Opacity = _fromOpacity * (1 - eased);
        _transform.X = _fromX + (exitX - _fromX) * eased;
        _transform.Y = _fromY + (exitY - _fromY) * eased;

        if (progress < 1) return;

        string text = _targetContent?.ToString() ?? string.Empty;
        _currentText = text;
        _label.Content = _targetContent;
        _displayedContent = _targetContent;
        UpdateViewport(text);
        _lastFrameTimestamp = Stopwatch.GetTimestamp();

        (double entryX, double entryY) = GetEntryOffset(_outMode);
        _transform.X = entryX;
        _transform.Y = entryY;
        _label.Opacity = 0;

        if (_outMode == ContentAnimationMode.Shimmer) BeginShimmer();

        _phase = Phase.In;
        _phaseStart = null;
    }

    void StepIn(TimeSpan time)
    {
        double elapsedMs = (time - _phaseStart!.Value).TotalMilliseconds;
        bool shimmer = _outMode == ContentAnimationMode.Shimmer;

        double fadeProgress = Math.Clamp(elapsedMs / FadePhaseDuration.TotalMilliseconds, 0, 1);
        double totalProgress = shimmer
            ? Math.Clamp(elapsedMs / ShimmerDuration.TotalMilliseconds, 0, 1)
            : fadeProgress;

        double eased = 1 - (1 - fadeProgress) * (1 - fadeProgress); // QuadraticEaseOut
        _label.Opacity = eased;

        (double entryX, double entryY) = GetEntryOffset(_outMode);
        _transform.X = entryX * (1 - eased);
        _transform.Y = entryY * (1 - eased);

        if (shimmer) UpdateShimmer(totalProgress);

        if (totalProgress < 1) return;

        StopShimmer();
        _label.Opacity = 1;
        _transform.X = 0;
        _transform.Y = 0;
        _phase = Phase.Idle;
        StartScroll();
    }

    void StepScroll(TimeSpan time)
    {
        if (_scrollStart is null)
        {
            _scrollStart = time;
            RecalculateScrollDistance();
            if (_scrollDistance <= 0.5)
            {
                _phase = Phase.Idle;
                _transform.X = 0;
                return;
            }
        }

        double elapsed = (time - _scrollStart.Value).TotalSeconds;

        TimeSpan duration = GetScrollDuration();
        double forward = duration.TotalSeconds;
        double hold = ScrollHoldDuration.TotalSeconds;
        double cycle = 2 * (hold + forward);
        double position = elapsed % cycle;
        double offset = 0;

        if (position < hold)
        {
            offset = 0;
        }
        else if (position < hold + forward)
        {
            offset = -_scrollDistance * SmoothStep((position - hold) / forward);
        }
        else if (position < 2 * hold + forward)
        {
            offset = -_scrollDistance;
        }
        else
        {
            offset = -_scrollDistance * (1 - SmoothStep((position - 2 * hold - forward) / forward));
        }

        _transform.X = offset;
        _transform.Y = 0;
        _label.Opacity = 1;
    }

    /// <summary>
    /// 几何刷新后决定是否重启滚动：内容/尺寸没变时不能重置滚动进度，
    /// 否则每次无变化的刷新、改颜色或高度变化都会让长文本跳回开头。
    /// </summary>
    void RefreshScrollAfterGeometryChange(double oldDistance, double oldViewportWidth)
    {
        if (_scrollDistance <= 0.5)
        {
            _phase = Phase.Idle;
            _transform.X = 0;
            CancelPendingFrame();
            return;
        }

        bool geometryChanged = Math.Abs(_scrollDistance - oldDistance) > 0.5
            || Math.Abs(_viewportWidth - oldViewportWidth) > 0.5;

        if (_phase == Phase.Scroll && !geometryChanged)
        {
            EnsureScrollFrame();
            return;
        }

        RestartScroll();
    }

    void EnsureScrollFrame()
    {
        if (_frameRequested) return;
        TopLevel? topLevel = TopLevel.GetTopLevel(_label);
        if (topLevel is not null) RequestFrame(topLevel);
    }

    void StartScroll()
    {
        UpdateViewport(_currentText);
        RestartScroll();
    }

    void RestartScroll()
    {
        if (_scrollDistance <= 0.5)
        {
            _phase = Phase.Idle;
            _transform.X = 0;
            CancelPendingFrame();
            return;
        }

        _phase = Phase.Scroll;
        _phaseStart = null;
        _scrollStart = null;
        _lastFrameTimestamp = Stopwatch.GetTimestamp();

        TopLevel? topLevel = TopLevel.GetTopLevel(_label);
        if (topLevel is null)
        {
            // 尚未附着到视觉树；Resume() 会在附着后重新启动滚动。
            CancelPendingFrame();
            return;
        }

        CancelPendingFrame();
        RequestFrame(topLevel);
    }

    /// <summary>作废已经排队的渲染帧回调，保证随后的 RequestFrame 不会因为旧标记被跳过。</summary>
    void CancelPendingFrame()
    {
        _frameToken++;
        _frameRequested = false;
    }

    TimeSpan GetScrollDuration()
    {
        double speed = _scrollPixelsPerSecond > 0 ? _scrollPixelsPerSecond : 42;
        double seconds = _scrollDistance / speed;
        if (!double.IsFinite(seconds) || seconds <= 0) return ScrollMinDuration;
        double min = ScrollMinDuration.TotalSeconds;
        double max = ScrollMaxDuration.TotalSeconds;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, min, max));
    }

    double Progress(TimeSpan time, TimeSpan duration)
    {
        if (_phaseStart is null) return 0;
        double total = duration.TotalMilliseconds;
        if (total <= 0) return 1;
        return Math.Clamp((time - _phaseStart.Value).TotalMilliseconds / total, 0, 1);
    }

    static double SmoothStep(double value)
    {
        double clamped = Math.Clamp(value, 0, 1);
        return clamped * clamped * (3 - 2 * clamped);
    }

    /// <summary>内容超过滚动长度时把视口宽度限制在前 N 个字符的宽度并计算滚动距离。</summary>
    void UpdateViewport(string text)
    {
        _scrollDistance = 0;

        if (_scrollMaxLength <= 0 || text.Length <= _scrollMaxLength)
        {
            _viewportWidth = 0;
            _viewport.MaxWidth = double.PositiveInfinity;
            _viewport.Width = double.NaN;
            _label.MaxWidth = double.PositiveInfinity;
            _label.Width = double.NaN;
            _label.ClipToBounds = false;
            _transform.X = 0;
            return;
        }

        _label.MaxWidth = double.PositiveInfinity;
        _label.ClipToBounds = false;

        double horizontalPadding = _label.Padding.Left + _label.Padding.Right;
        string prefix = TakeCharacters(text, _scrollMaxLength);
        double measured = MeasureText(prefix) + horizontalPadding + 2;
        _viewportWidth = Math.Max(12, measured);

        // 用 MaxWidth 而不是固定 Width：宿主用无穷宽度测量组件时，视口仍可被外层裁剪。
        _viewport.Width = double.NaN;
        _viewport.MaxWidth = _viewportWidth;

        // 明确按完整文本给出 Label 宽度，保证超出的文字确实被排版出来、由不移动的视口裁剪。
        double textWidth = MeasureText(text);
        _label.Width = textWidth + horizontalPadding + 2;

        double visible = GetVisibleWidth(horizontalPadding);
        _scrollDistance = Math.Max(0, textWidth - visible);
    }

    /// <summary>宿主实际可见宽度：视口前缀宽度与组件中该视口起点之后的剩余宽度取较小值。</summary>
    double GetVisibleWidth(double horizontalPadding)
    {
        double originX = 0;
        try
        {
            Point? origin = _viewport.TranslatePoint(new Point(0, 0), _root);
            if (origin is { } point && double.IsFinite(point.X) && point.X > 0)
                originX = point.X;
        }
        catch
        {
            // 未附着或无法转换坐标时按没有偏移处理。
        }

        double rootWidth = _root.Bounds.Width;
        double slot = rootWidth > 0.5
            ? Math.Max(1, rootWidth - originX)
            : _viewportWidth;
        double viewport = Math.Min(_viewportWidth, slot);
        return Math.Max(1, viewport - horizontalPadding);
    }

    /// <summary>布局完成后再按实际可见宽度修正滚动距离（宿主可能把组件压得更窄）。</summary>
    void RecalculateScrollDistance()
    {
        if (_scrollMaxLength <= 0 || _currentText.Length <= _scrollMaxLength)
        {
            _scrollDistance = 0;
            return;
        }

        double horizontalPadding = _label.Padding.Left + _label.Padding.Right;
        double textWidth = MeasureText(_currentText);
        _label.Width = textWidth + horizontalPadding + 2;
        _scrollDistance = Math.Max(0, textWidth - GetVisibleWidth(horizontalPadding));
    }

    /// <summary>按标签当前字体测量文本宽度；失败时退回按字号估算。</summary>
    double MeasureText(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        try
        {
            _probe.Text = text;
            _probe.FontFamily = _label.FontFamily;
            _probe.FontSize = _label.FontSize;
            _probe.FontWeight = _label.FontWeight;
            _probe.FontStyle = _label.FontStyle;
            _probe.FontStretch = _label.FontStretch;
            _probe.Measure(Size.Infinity);
            double width = _probe.DesiredSize.Width;
            return double.IsFinite(width) && width > 0 ? width : EstimateTextWidth(text);
        }
        catch
        {
            return EstimateTextWidth(text);
        }
    }

    double EstimateTextWidth(string text)
    {
        double fontSize = _label.FontSize > 0 ? _label.FontSize : 12;
        double width = 0;
        foreach (char character in text)
        {
            width += character <= '\u007f' ? fontSize * 0.55 : fontSize;
        }
        return width;
    }

    /// <summary>截取前 N 个字符，避免把代理对切成半个字符。</summary>
    static string TakeCharacters(string text, int count)
    {
        if (count <= 0) return string.Empty;
        if (text.Length <= count) return text;

        int end = count;
        if (end > 0
            && char.IsHighSurrogate(text[end - 1])
            && end < text.Length
            && char.IsLowSurrogate(text[end]))
        {
            end--;
        }

        return end <= 0 ? string.Empty : text[..end];
    }

    static (double X, double Y) GetExitDelta(ContentAnimationMode mode) => mode switch
    {
        ContentAnimationMode.SlideUp => (0, -MotionPixels),
        ContentAnimationMode.SlideDown => (0, MotionPixels),
        ContentAnimationMode.SlideLeft => (-MotionPixels, 0),
        ContentAnimationMode.SlideRight => (MotionPixels, 0),
        _ => (0, 0)
    };

    static (double X, double Y) GetEntryOffset(ContentAnimationMode mode)
    {
        (double x, double y) = GetExitDelta(mode);
        return (-x, -y);
    }

    void BeginShimmer()
    {
        Color baseColor = GetBaseColor();
        Color highlightColor = ResolveShimmerColor(baseColor);

        _shimmerBefore = new GradientStop(baseColor, 0);
        _shimmerHighlight = new GradientStop(highlightColor, 0.5);
        _shimmerAfter = new GradientStop(baseColor, 1);

        // 滚动模式下标签宽度是整段隐藏文本；相对坐标会把光带按全文宽度铺开，
        // 可见区域看起来只会整块闪烁。此时按可见视口宽度使用绝对坐标。
        bool scrolling = _scrollDistance > 0.5;
        RelativePoint startPoint;
        RelativePoint endPoint;
        if (scrolling)
        {
            double visible = GetVisibleWidth(_label.Padding.Left + _label.Padding.Right);
            startPoint = new RelativePoint(0, 0.5, RelativeUnit.Absolute);
            endPoint = new RelativePoint(Math.Max(1, visible), 0.5, RelativeUnit.Absolute);
        }
        else
        {
            startPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative);
            endPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative);
        }

        _shimmerBrush = new LinearGradientBrush
        {
            StartPoint = startPoint,
            EndPoint = endPoint,
            GradientStops = new GradientStops
            {
                _shimmerBefore,
                _shimmerHighlight,
                _shimmerAfter
            }
        };

        _shimmerActive = true;
        _label.Foreground = _shimmerBrush;
        UpdateShimmer(0);
    }

    void UpdateShimmer(double progress)
    {
        if (!_shimmerActive
            || _shimmerBefore is null
            || _shimmerHighlight is null
            || _shimmerAfter is null)
        {
            return;
        }

        // 光带从左侧进入，扫过整段文字后从右侧离开。
        double center = -0.25 + 1.5 * Math.Clamp(progress, 0, 1);
        _shimmerBefore.Offset = Math.Clamp(center - ShimmerBandHalfWidth, 0, 1);
        _shimmerHighlight.Offset = Math.Clamp(center, 0, 1);
        _shimmerAfter.Offset = Math.Clamp(center + ShimmerBandHalfWidth, 0, 1);
    }

    void StopShimmer()
    {
        if (!_shimmerActive) return;

        _shimmerActive = false;
        _shimmerBrush = null;
        _shimmerBefore = null;
        _shimmerHighlight = null;
        _shimmerAfter = null;
        ApplyNormalForeground();
    }

    void ApplyNormalForeground()
    {
        if (_normalBrush is not null)
        {
            _label.Foreground = _normalBrush;
        }
        else
        {
            // 清掉本地值，继续跟随主题/继承的前景色。
            _label.ClearValue(TemplatedControl.ForegroundProperty);
        }
    }

    Color GetBaseColor()
    {
        if (_normalBrush is ISolidColorBrush normal) return normal.Color;
        if (_label.Foreground is ISolidColorBrush current) return current.Color;
        return Colors.White;
    }

    /// <summary>默认扫光不透明度：光带处文字降到本色的这个比例，任何背景下都自然。</summary>
    const double AutoShimmerOpacity = 0.3;

    /// <summary>自定义扫光色优先（#AARRGGBB 的透明度同样生效）；否则用文字本色半透明扫过。</summary>
    Color ResolveShimmerColor(Color baseColor)
    {
        if (_shimmerColorBrush is ISolidColorBrush custom)
        {
            Color c = custom.Color;
            byte alpha = (byte)Math.Round(c.A * custom.Opacity * baseColor.A / 255d);
            return Color.FromArgb(alpha, c.R, c.G, c.B);
        }

        return Color.FromArgb(
            (byte)Math.Round(baseColor.A * AutoShimmerOpacity),
            baseColor.R,
            baseColor.G,
            baseColor.B);
    }

    /// <summary>立即显示内容并复位：停止进行中的动画，透明度为 1，位移归零。</summary>
    void Finish(object? content, string text)
    {
        _frameToken++;
        _frameRequested = false;
        StopShimmer();
        _phase = Phase.Idle;
        _targetContent = null;
        _currentText = text;
        if (content is not null)
        {
            _label.Content = content;
            _displayedContent = content;
        }

        _label.Opacity = 1;
        _transform.X = 0;
        _transform.Y = 0;
        UpdateViewport(text);

        // 无论是否播放切换动画，超过滚动长度时都应该继续滚动。
        if (_scrollDistance > 0.5) StartScroll();
    }
}
