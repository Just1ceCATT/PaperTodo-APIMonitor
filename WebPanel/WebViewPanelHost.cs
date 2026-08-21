using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace PaperTodo.Plugin.ApiBalanceMonitor.WebPanel;

/// <summary>
/// WebView2 监视面板封装：5 个 bool/string 字段（_webViewReady / _documentReady / _pendingPayload / _webViewInitializationStarted / _lifetime）
/// 全部归入本类，Session 只通过三个动作交互：PostSnapshot / ReloadForProvider / Dispose。
/// WebMessageReceived 暴露单一事件供 Session 解析业务消息（ready / switchProvider）。
/// </summary>
internal sealed class WebViewPanelHost : IDisposable
{
    private const string HostVirtualName = "papertodo.balance.monitor.local";
    private const string RuntimeSubdir = ".runtime";
    private const string WebViewUserDataSubdir = "webview2";

    private readonly Grid _viewRoot;
    private readonly WebView2CompositionControl _webView;
    private readonly object _environmentGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Task<CoreWebView2Environment>? _environmentTask;
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
        _webView = new WebView2CompositionControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = true
        };

        _viewRoot = new Grid
        {
            Background = Brushes.Transparent,
            ClipToBounds = true
        };
        _viewRoot.Children.Add(_webView);
        _viewRoot.Loaded += OnViewRootLoaded;
        _viewRoot.SizeChanged += OnViewRootSizeChanged;
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
        var htmlFile = HtmlFileNameFor(provider);
        try
        {
            _webView.CoreWebView2?.Navigate($"https://{HostVirtualName}/web/{htmlFile}");
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

    private void OnViewRootLoaded(object sender, RoutedEventArgs e) => TryStartWebView();

    private void OnViewRootSizeChanged(object sender, SizeChangedEventArgs e) => TryStartWebView();

    private void TryStartWebView()
    {
        if (_webViewInitializationStarted ||
            _disposed ||
            !_viewRoot.IsLoaded ||
            _viewRoot.ActualWidth <= 0 ||
            _viewRoot.ActualHeight <= 0)
        {
            return;
        }
        _webViewInitializationStarted = true;
        _viewRoot.SizeChanged -= OnViewRootSizeChanged;
        _ = InitializeWebViewAsync(_lifetime.Token);
    }

    private async Task InitializeWebViewAsync(CancellationToken token)
    {
        try
        {
            var environment = await GetWebViewEnvironmentAsync();
            token.ThrowIfCancellationRequested();

            await _webView.EnsureCoreWebView2Async(environment);
            token.ThrowIfCancellationRequested();

            var core = _webView.CoreWebView2 ??
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
                _webView.DefaultBackgroundColor = System.Drawing.Color.Transparent;
            }
            catch
            {
            }

            _webViewReady = true;
            core.Navigate($"https://{HostVirtualName}/web/{htmlFile}");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch
        {
            _webViewInitializationStarted = false;
        }
    }

    private async Task<CoreWebView2Environment> GetWebViewEnvironmentAsync()
    {
        Task<CoreWebView2Environment> task;
        lock (_environmentGate)
        {
            task = _environmentTask ??= CreateWebViewEnvironmentAsync();
        }
        try
        {
            return await task;
        }
        catch
        {
            lock (_environmentGate)
            {
                if (ReferenceEquals(_environmentTask, task))
                {
                    _environmentTask = null;
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
            return;
        }
        _documentReady = true;
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
            try
            {
                _webView.CoreWebView2?.Reload();
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
        try
        {
            _webView.CoreWebView2?.PostWebMessageAsJson(payload);
        }
        catch
        {
        }
        try
        {
            _webView.CoreWebView2?.ExecuteScriptAsync(
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