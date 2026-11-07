using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ClassIsland.Shared;
using Microsoft.Extensions.Logging;

namespace IslandQuotes.Core;

/// <summary>
/// 在运行时用宿主自带的 Avalonia XAML 编译器加载界面。
/// </summary>
/// <remarks>
/// 构建期编译的 XAML 会把对 Avalonia 内部方法的调用（如 MarkupExtension.ProvideValue 的具体签名）
/// 直接写进插件程序集，宿主一升级 Avalonia 大版本（ClassIsland 2.1.1.1 起为 Avalonia 12）
/// 就会抛 MissingMethodException。改为运行时加载后，XAML 始终按宿主当前的 Avalonia 版本编译，
/// 同一份插件可同时运行在 Avalonia 11 与 12 的宿主上。
/// </remarks>
internal static class RuntimeXaml
{
    const string FluentAvaloniaControlsNamespace = "FluentAvalonia.UI.Controls";

    static readonly Assembly LocalAssembly = typeof(RuntimeXaml).Assembly;

    static readonly Regex FluentAvaloniaPrefixPattern = new(
        @"xmlns:(\w+)=""clr-namespace:FluentAvalonia\.UI\.Controls;assembly=FluentAvalonia""",
        RegexOptions.Compiled);

    /// <summary>
    /// 运行时 XAML 编译器只能解析已加载程序集里的类型与 xmlns 映射，
    /// 首次加载前先把插件引用的程序集（Avalonia.Markup、ClassIsland.Core、FluentAvalonia 等）载入。
    /// </summary>
    static readonly Lazy<bool> ReferencedAssembliesLoaded = new(() =>
    {
        string[] xamlOnlyAssemblies = ["Avalonia.Markup", "Avalonia.Markup.Xaml", "Avalonia.Controls", "ClassIsland.Core", "FluentAvalonia"];
        IEnumerable<AssemblyName> names = LocalAssembly.GetReferencedAssemblies()
            .Concat(xamlOnlyAssemblies.Select(n => new AssemblyName(n)));
        foreach (AssemblyName name in names)
        {
            try
            {
                Assembly.Load(name);
            }
            catch
            {
                // 宿主缺少的程序集由真正用到它的 XAML 报错，这里不提前失败。
            }
        }
        return true;
    });

    /// <summary>
    /// 加载界面；失败时把异常写入 ClassIsland 日志，并在界面上直接显示错误，避免出现无提示的空白页。
    /// </summary>
    /// <returns>是否加载成功。失败时 <paramref name="root"/> 的内容已替换为错误提示。</returns>
    public static bool TryLoad<T>(T root) where T : ContentControl
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            Load(root);
            Log(logger => logger.LogInformation("已加载界面 {View}，耗时 {Elapsed}ms", typeof(T).Name, stopwatch.ElapsedMilliseconds));
            return true;
        }
        catch (Exception exception)
        {
            Log(logger => logger.LogError(exception, "加载界面 {View} 失败", typeof(T).Name));
            ShowError(root, exception);
            return false;
        }
    }

    /// <summary>在界面上显示错误（同时写入日志），用于加载后的初始化失败。</summary>
    public static void ReportError(ContentControl root, string stage, Exception exception)
    {
        Log(logger => logger.LogError(exception, "界面 {View} {Stage}失败", root.GetType().Name, stage));
        ShowError(root, exception);
    }

    static void ShowError(ContentControl root, Exception exception)
    {
        root.Content = new TextBlock
        {
            Text = $"IslandQuotes 界面加载失败，请将 ClassIsland 日志反馈给开发者。\n{exception.GetType().FullName}: {exception.Message}",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(8)
        };
    }

    static void Log(Action<ILogger> write)
    {
        try
        {
            write(IAppHost.GetService<ILoggerFactory>().CreateLogger("IslandQuotes.RuntimeXaml"));
        }
        catch
        {
            // 宿主日志不可用时退回标准输出。
            write(new ConsoleFallbackLogger());
        }
    }

    sealed class ConsoleFallbackLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Console.WriteLine($"[IslandQuotes] {logLevel}: {formatter(state, exception)} {exception}");
    }

    /// <summary>加载与 <typeparamref name="T"/> 同名的嵌入 .axaml 并填充到 <paramref name="root"/>。</summary>
    public static void Load<T>(T root) where T : class
    {
        _ = ReferencedAssembliesLoaded.Value;

        string resourceName = typeof(T).FullName + ".axaml";
        using Stream stream = LocalAssembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"找不到嵌入的 XAML 资源：{resourceName}");
        using var reader = new StreamReader(stream);
        string xaml = AdaptFluentAvaloniaNames(reader.ReadToEnd());

        string relativePath = typeof(T).FullName!["IslandQuotes.".Length..].Replace('.', '/') + ".axaml";
        var baseUri = new Uri($"avares://{LocalAssembly.GetName().Name}/{relativePath}");
        RuntimeLoad.Value(xaml, LocalAssembly, root, baseUri, false);
    }

    /// <summary>
    /// 通过反射调用宿主自带的 AvaloniaRuntimeXamlLoader.Load(string, Assembly, object, Uri, bool)。
    /// 不静态引用 Avalonia.Markup.Xaml.Loader：各版本宿主自带的程序集版本号不同
    /// （ClassIsland 2.1.0.1 为 11.0.0.0，2.1.1.1 为 12.1.1.0），静态引用高于宿主的版本会直接
    /// FileNotFoundException。该重载在 11.x 与 12.x 中签名一致。
    /// </summary>
    static readonly Lazy<Func<string, Assembly, object, Uri, bool, object>> RuntimeLoad = new(() =>
    {
        Assembly loaderAssembly = Assembly.Load(new AssemblyName("Avalonia.Markup.Xaml.Loader"));
        Type loaderType = loaderAssembly.GetType("Avalonia.Markup.Xaml.AvaloniaRuntimeXamlLoader", throwOnError: true)!;
        MethodInfo load = loaderType.GetMethod(
                "Load",
                BindingFlags.Public | BindingFlags.Static,
                [typeof(string), typeof(Assembly), typeof(object), typeof(Uri), typeof(bool)])
            ?? throw new MissingMethodException(loaderType.FullName, "Load(string, Assembly, object, Uri, bool)");

        return (xaml, assembly, root, uri, designMode) =>
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveNewerFrameworkAssembly;
            try
            {
                return load.Invoke(null, [xaml, assembly, root, uri, designMode])!;
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
            finally
            {
                AppDomain.CurrentDomain.AssemblyResolve -= ResolveNewerFrameworkAssembly;
            }
        };
    });

    /// <summary>运行时框架程序集的公钥标记（System.*、Microsoft.Extensions.* 等）。</summary>
    static readonly HashSet<string> FrameworkPublicKeyTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "b03f5f7f11d50a3a", "cc7b13ffcd2ddd51", "adb9793829ddae60", "7cec85d7bea7798e", "31bf3856ad364e35"
    };

    /// <summary>
    /// 运行时 XAML 编译器解析 xmlns 映射时会读取进程内<b>所有</b>已加载程序集的特性。
    /// 若同时安装了面向更高 .NET 版本构建的其他插件（例如引用 System.Runtime 10.0 而宿主为 .NET 8），
    /// 读取其特性会抛 FileNotFoundException，导致本插件界面也无法加载。
    /// 加载期间把这类“请求的框架版本高于宿主”的绑定重定向到宿主已加载的同名程序集。
    /// </summary>
    static Assembly? ResolveNewerFrameworkAssembly(object? sender, ResolveEventArgs args)
    {
        AssemblyName requested;
        try
        {
            requested = new AssemblyName(args.Name);
        }
        catch
        {
            return null;
        }

        byte[]? token = requested.GetPublicKeyToken();
        if (token is not { Length: > 0 } || !FrameworkPublicKeyTokens.Contains(Convert.ToHexString(token))) return null;

        foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies())
        {
            AssemblyName name = loaded.GetName();
            if (string.Equals(name.Name, requested.Name, StringComparison.OrdinalIgnoreCase)
                && System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(loaded) == System.Runtime.Loader.AssemblyLoadContext.Default)
            {
                return loaded;
            }
        }
        return null;
    }

    /// <summary>
    /// FluentAvalonia 3（ClassIsland 2.1.1.1 起）给控件统一加了 FA 前缀，例如 SettingsExpander → FASettingsExpander。
    /// 按宿主实际加载的 FluentAvalonia 把 XAML 中的控件名改成存在的那一个，新旧写法都能加载。
    /// </summary>
    static string AdaptFluentAvaloniaNames(string xaml)
    {
        Match prefixMatch = FluentAvaloniaPrefixPattern.Match(xaml);
        if (!prefixMatch.Success) return xaml;

        Assembly fluentAvalonia;
        try
        {
            fluentAvalonia = Assembly.Load("FluentAvalonia");
        }
        catch
        {
            return xaml;
        }

        string prefix = Regex.Escape(prefixMatch.Groups[1].Value);
        var usage = new Regex($@"(?<![\w.:]){prefix}([:|])([A-Za-z_]\w*)");
        var cache = new Dictionary<string, string>(StringComparer.Ordinal);

        return usage.Replace(xaml, match =>
        {
            string name = match.Groups[2].Value;
            if (!cache.TryGetValue(name, out string? resolved))
            {
                resolved = ResolveFluentAvaloniaTypeName(fluentAvalonia, name);
                cache[name] = resolved;
            }
            return $"{prefixMatch.Groups[1].Value}{match.Groups[1].Value}{resolved}";
        });
    }

    static string ResolveFluentAvaloniaTypeName(Assembly fluentAvalonia, string name)
    {
        if (HasType(fluentAvalonia, name)) return name;
        if (HasType(fluentAvalonia, "FA" + name)) return "FA" + name;
        if (name.StartsWith("FA", StringComparison.Ordinal) && HasType(fluentAvalonia, name[2..])) return name[2..];
        return name;
    }

    static bool HasType(Assembly assembly, string name) =>
        assembly.GetType($"{FluentAvaloniaControlsNamespace}.{name}", throwOnError: false) is not null;
}
