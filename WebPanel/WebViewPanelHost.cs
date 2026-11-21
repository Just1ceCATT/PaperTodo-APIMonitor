using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using PaperTodo.Plugin.ApiBalanceMonitor.Services;

namespace PaperTodo.Plugin.ApiBalanceMonitor.WebPanel;

/// <summary>
/// WebView2 监视面板封装：Session 只通过 PostSnapshot / ReloadForProvider / Dispose 交互，
/// WebMessageReceived 暴露单一事件供 Session 解析业务消息（ready / switchProvider）。
///
/// 初始化策略（混合）：
/// - 用户启动后短时间内打开面板 → 展开动画结束（约 300ms）后再初始化，期间显示原生骨架；
/// - 启动宽限期（5s）内无人打开 → 后台预热一个 WebView2 浏览器进程，后续打开直接复用；
/// - 所有面板共享同一个 CoreWebView2Environment，避免每张纸重复冷启动环境。
/// </summary>
internal sealed class WebViewPanelHost : IDisposable
{
    private const string HostVirtualName = "papertodo.balance.monitor.local";
    private const string RuntimeSubdir = ".runtime";
    private const string WebViewUserDataSubdir = "webview2";

    // 宿主展开动画约 220ms。从首次拿到有效尺寸起延后 300ms 再初始化 WebView2，
    // 确保初始化不与展开动画抢 UI 线程；这段时间显示原生骨架。
    private const int InitializationDelayMilliseconds = 300;
    // 启动宽限期：若用户在这段时间内没有打开面板，则在后台预热 WebView2；
    // 若用户抢先打开，则走上面的“延迟初始化 + 骨架”路径。
    private const int BackgroundWarmupDelayMilliseconds = 5000;

    private readonly Grid _viewRoot;
    // WebView2 控件延迟到真正初始化时创建：展开动画期间视觉树里只有轻量骨架，
    // 避免 WebView2CompositionControl 首次参与布局/渲染时的 D3DImage 初始化拖慢动画。
    private WebView2CompositionControl? _webView;
    // 原生骨架：WebView2 初始化/导航期间显示，避免面板区域空白或闪烁。
    private readonly Grid _placeholder;
    private readonly TextBlock _placeholderText;
    private readonly DispatcherTimer _initializationDelay;
    private readonly DispatcherTimer _backgroundWarmupDelay;
    // 所有纸片共享同一个 CoreWebView2Environment：
    // 同一 userDataFolder 下只创建一次，避免每张纸首次展开各自冷启动环境。
    // 失败时清空缓存，允许后续展开重试。
    private static readonly object EnvironmentGate = new();
    private static Task<CoreWebView2Environment>? _sharedEnvironmentTask;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _webViewInitializationStarted;
    private bool _webViewReady;
    private bool _documentReady;
    private string? _pendingPayload;
    private string? _pendingProviderForInit;
    private bool _disposed;

    /// <summary>Session 订阅此事件处理 ready / switchProvider 等业务消息；异常由 Session 吞掉。</summary>
    public event Action<string>? WebMessageReceived;

    public WebViewPanelHost()
    {
        // 原生骨架：面板初始化/导航期间露出的轻量占位。
        // 用系统 GrayText 保证浅色 / 深色主题下都可见，不引入插件主题依赖。
        _placeholderText = new TextBlock
        {
            Text = "正在加载监视面板…",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            Foreground = SystemColors.GrayTextBrush,
            IsHitTestVisible = false
        };
        _placeholder = new Grid
        {
            Background = Brushes.Transparent,
            IsHitTestVisible = false
        };
        _placeholder.Children.Add(_placeholderText);

        _viewRoot = new Grid
        {
            Background = Brushes.Transparent,
            ClipToBounds = true
        };
        _viewRoot.Children.Add(_placeholder);
        Panel.SetZIndex(_placeholder, 1);
        _viewRoot.Loaded += OnViewRootLoaded;
        _viewRoot.SizeChanged += OnViewRootSizeChanged;

        // 展开后的延迟初始化：给 220ms 展开动画留出时间，避免抢 UI 线程。
        _initializationDelay = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(InitializationDelayMilliseconds)
        };
        _initializationDelay.Tick += OnInitializationDelayTick;

        // 启动宽限期内的后台预热：若用户 5 秒内没有打开面板，则预先初始化 WebView2；
        // 若用户已经打开，ScheduleInitialization 会取消这个计时器。
        _backgroundWarmupDelay = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(BackgroundWarmupDelayMilliseconds)
        };
        _backgroundWarmupDelay.Tick += OnBackgroundWarmupDelayTick;
        _backgroundWarmupDelay.Start();
    }

    /// <summary>宿主创建 session 时挂在 body 上的根 Grid。</summary>
    public FrameworkElement ViewRoot => _viewRoot;

    /// <summary>
    /// 当前选中的 provider，用于 InitializeWebViewAsync 阶段选 HTML 文件。Session 在 ctor
    /// 阶段与 ReloadForProvider 后调用。
    /// </summary>
    public void SetActiveProvider(string provider) => _pendingProviderForInit = provider;

    /// <summary>把最新 payload 推给 HTML 面板；未就绪时缓存，就绪后自动补发。</summary>
    public void PostSnapshot(string payload)
    {
        if (_disposed)
        {
            return;
        }
        if (!_webViewReady || !_documentReady)
        {
            _pendingPayload = payload;
            return;
        }
        PostPayload(payload);
    }

    /// <summary>供应商切换后重新导航 WebView2 到对应面板 HTML；Session 在调用前应已更新 _state.Provider。</summary>
    public void ReloadForProvider(string provider)
    {
        _documentReady = false;
        _pendingPayload = null;
        _pendingProviderForInit = provider;
        ShowPlaceholder("正在加载监视面板…");
        var htmlFile = HtmlFileNameFor(provider);
        try
        {
            _webView?.CoreWebView2?.Navigate($"https://{HostVirtualName}/web/{htmlFile}");
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _initializationDelay.Stop();
        _initializationDelay.Tick -= OnInitializationDelayTick;
        _backgroundWarmupDelay.Stop();
        _backgroundWarmupDelay.Tick -= OnBackgroundWarmupDelayTick;
        _lifetime.Cancel();
        try
        {
            _webView?.Dispose();
        }
        catch
        {
        }
    }

    // ---------------- 内部实现 ----------------

    private void OnViewRootLoaded(object sender, RoutedEventArgs e) => ScheduleInitialization();

    private void OnViewRootSizeChanged(object sender, SizeChangedEventArgs e) => ScheduleInitialization();

    /// <summary>
    /// 首次拿到有效尺寸后启动 300ms 延迟，等展开动画结束再初始化。
    /// 后续 SizeChanged 不重置计时，避免动画/布局帧把初始化无限推后；
    /// 若计时期间又折叠回 0 尺寸，则取消本次初始化。
    /// </summary>
    private void ScheduleInitialization()
    {
        if (_webViewInitializationStarted || _disposed || !_viewRoot.IsLoaded)
        {
            return;
        }
        if (_viewRoot.ActualWidth <= 0 || _viewRoot.ActualHeight <= 0)
        {
            _initializationDelay.Stop();
            return;
        }
        // 用户已经打开面板：取消后台预热，改走“展开动画结束后初始化 + 骨架”。
        _backgroundWarmupDelay.Stop();
        if (!_initializationDelay.IsEnabled)
        {
            HookTrace.Write(
                $"webview.schedule size={_viewRoot.ActualWidth:F0}x{_viewRoot.ActualHeight:F0} " +
                $"delay={InitializationDelayMilliseconds}ms");
            ShowPlaceholder("正在加载监视面板…");
            _initializationDelay.Start();
        }
    }

    private void OnInitializationDelayTick(object? sender, EventArgs e)
    {
        _initializationDelay.Stop();
        if (_webViewInitializationStarted || _disposed)
        {
            return;
        }
        if (!_viewRoot.IsLoaded ||
            _viewRoot.ActualWidth <= 0 ||
            _viewRoot.ActualHeight <= 0)
        {
            return;
        }
        _webViewInitializationStarted = true;
        _viewRoot.SizeChanged -= OnViewRootSizeChanged;
        HookTrace.Write("webview.init start (after expand delay)");
        _ = InitializeWebViewAsync(_lifetime.Token);
    }

    /// <summary>
    /// 启动宽限期已到且用户仍未打开面板：通过隐藏的 1×1 宿主预热浏览器进程。
    /// 注意不能用本面板自己的控件预热——折叠时尺寸为 0，EnsureCoreWebView2Async 会挂起，
    /// 等展开才继续，反而造成卡顿。
    /// </summary>
    private void OnBackgroundWarmupDelayTick(object? sender, EventArgs e)
    {
        _backgroundWarmupDelay.Stop();
        if (_webViewInitializationStarted || _disposed)
        {
            return;
        }
        // 用户已经打开（正文已有尺寸）：交给正常的延迟初始化路径。
        if (_viewRoot.IsLoaded &&
            _viewRoot.ActualWidth > 0 &&
            _viewRoot.ActualHeight > 0)
        {
            return;
        }
        HookTrace.Write("webview.background-warmup requested");
        WebViewWarmupHost.EnsureStarted();
    }

    /// <summary>切换到骨架并隐藏未就绪的 WebView2。</summary>
    private void ShowPlaceholder(string text)
    {
        _placeholderText.Text = text;
        _placeholder.Visibility = Visibility.Visible;
        if (_webView is { } webView)
        {
            webView.IsHitTestVisible = false;
        }
    }

    /// <summary>内容就绪：隐藏骨架并恢复 WebView2 命中。</summary>
    private void RevealContent()
    {
        if (_webView is { } webView)
        {
            webView.IsHitTestVisible = true;
        }
        _placeholder.Visibility = Visibility.Collapsed;
    }

    private async Task InitializeWebViewAsync(CancellationToken token)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var environment = await GetWebViewEnvironmentAsync();
            token.ThrowIfCancellationRequested();

            // 控件在真正初始化时才加入视觉树，展开动画期间不承担 D3D/Composition 首次渲染成本。
            var webView = EnsureWebViewCreated();
            await webView.EnsureCoreWebView2Async(environment);
            token.ThrowIfCancellationRequested();

            var core = webView.CoreWebView2 ??
                throw new InvalidOperationException("WebView2 初始化失败。");
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = true;
            core.Settings.IsStatusBarEnabled = false;
            core.NavigationCompleted += OnWebViewNavigationCompleted;
            core.ProcessFailed += OnWebViewProcessFailed;
            core.WebMessageReceived += OnWebMessageReceivedInternal;

            var pluginDirectory =
                Path.GetDirectoryName(typeof(WebViewPanelHost).Assembly.Location)
                ?? AppContext.BaseDirectory;
            var htmlFile = HtmlFileNameFor(_pendingProviderForInit ?? "deepseek");
            if (!File.Exists(Path.Combine(pluginDirectory, "web", htmlFile)))
            {
                throw new InvalidOperationException($"缺少 web/{htmlFile}。");
            }

            core.SetVirtualHostNameToFolderMapping(
                HostVirtualName,
                pluginDirectory,
                CoreWebView2HostResourceAccessKind.DenyCors);

            try
            {
                webView.DefaultBackgroundColor = System.Drawing.Color.Transparent;
            }
            catch
            {
            }

            _webViewReady = true;
            core.Navigate($"https://{HostVirtualName}/web/{htmlFile}");
            HookTrace.Write(
                $"webview.init done ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            HookTrace.Write(
                $"webview.init failed " +
                $"ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} " +
                $"err={ex.GetType().Name}:{ex.Message}");
            _webViewInitializationStarted = false;
            ShowPlaceholder("监视面板加载失败");
            if (!_disposed)
            {
                // 初始化失败时恢复 SizeChanged 订阅；下次尺寸变化（或切换供应商）可重试。
                _viewRoot.SizeChanged -= OnViewRootSizeChanged;
                _viewRoot.SizeChanged += OnViewRootSizeChanged;
            }
        }
    }

    /// <summary>
    /// 创建 WebView2 控件并加入视觉树（放在骨架下方）。
    /// 只在真正开始初始化时调用，确保展开动画期间视觉树保持轻量。
    /// </summary>
    private WebView2CompositionControl EnsureWebViewCreated()
    {
        if (_webView is { } existing)
        {
            return existing;
        }
        var webView = new WebView2CompositionControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            // 就绪前禁用命中：骨架负责这段加载态，避免空白 WebView2 抢占点击。
            IsHitTestVisible = false
        };
        try
        {
            webView.DefaultBackgroundColor = System.Drawing.Color.Transparent;
        }
        catch
        {
        }
        _webView = webView;
        // 插到骨架下方；骨架自身有更高 ZIndex。
        _viewRoot.Children.Insert(0, webView);
        HookTrace.Write("webview.control created");
        return webView;
    }

    /// <summary>供后台预热宿主复用同一个 Environment（不启动浏览器进程）。</summary>
    internal static Task<CoreWebView2Environment> SharedEnvironmentAsync() =>
        GetWebViewEnvironmentAsync();

    private static async Task<CoreWebView2Environment> GetWebViewEnvironmentAsync()
    {
        Task<CoreWebView2Environment> task;
        lock (EnvironmentGate)
        {
            task = _sharedEnvironmentTask ??= CreateWebViewEnvironmentAsync();
        }
        try
        {
            return await task.ConfigureAwait(true);
        }
        catch
        {
            lock (EnvironmentGate)
            {
                if (ReferenceEquals(_sharedEnvironmentTask, task))
                {
                    _sharedEnvironmentTask = null;
                }
            }
            throw;
        }
    }

    private static Task<CoreWebView2Environment> CreateWebViewEnvironmentAsync()
    {
        var pluginDirectory =
            Path.GetDirectoryName(typeof(WebViewPanelHost).Assembly.Location)
            ?? AppContext.BaseDirectory;
        var userDataFolder = Path.Combine(pluginDirectory, RuntimeSubdir, WebViewUserDataSubdir);
        Directory.CreateDirectory(userDataFolder);
        return CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: userDataFolder,
            options: null);
    }

    private void OnWebViewNavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            ShowPlaceholder("监视面板加载失败");
            return;
        }
        _documentReady = true;
        // 导航完成即显示页面，避免等 ready 握手时骨架停留过久；
        // ready 之后 PostPayload 会再补一次数据。
        RevealContent();
        if (_pendingPayload != null)
        {
            var payload = _pendingPayload;
            _pendingPayload = null;
            PostPayload(payload);
        }
    }

    private void OnWebViewProcessFailed(
        object? sender,
        CoreWebView2ProcessFailedEventArgs e)
    {
        if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited)
        {
            _documentReady = false;
            ShowPlaceholder("正在重新加载监视面板…");
            try
            {
                _webView?.CoreWebView2?.Reload();
            }
            catch
            {
            }
        }
    }

    private void OnWebMessageReceivedInternal(
        object? sender,
        CoreWebView2WebMessageReceivedEventArgs e)
    {
        WebMessageReceived?.Invoke(e.WebMessageAsJson);
    }

    private void PostPayload(string payload)
    {
        var core = _webView?.CoreWebView2;
        if (core == null)
        {
            return;
        }
        try
        {
            core.PostWebMessageAsJson(payload);
        }
        catch
        {
        }
        try
        {
            _ = core.ExecuteScriptAsync(
                $"window.__renderBalance && window.__renderBalance({payload});");
        }
        catch
        {
        }
    }

    private static string HtmlFileNameFor(string provider) => provider switch
    {
        "minimax" => "minimax.html",
        "opencode" => "opencode.html",
        _ => "monitor.html"
    };
}