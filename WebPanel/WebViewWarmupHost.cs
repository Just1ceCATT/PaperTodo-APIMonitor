using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using PaperTodo.Plugin.ApiBalanceMonitor.Services;

namespace PaperTodo.Plugin.ApiBalanceMonitor.WebPanel;

/// <summary>
/// 启动宽限期后的后台预热宿主。
///
/// 不能用折叠状态的面板控件预热：面板此时尺寸为 0，`EnsureCoreWebView2Async` 会一直挂起，
/// 等用户展开时才继续，反而把冷启动塞回动画帧（实测日志可复现）。
/// 这里改为在屏幕外创建一个隐藏的 1×1 窗口 + WebView2 控件，用共享 Environment 初始化并
/// 导航到 about:blank，只为了让浏览器进程常驻；面板自己的控件仍延迟到展开动画结束后创建。
///
/// 进程内只预热一次；后续面板复用同一个浏览器进程。
/// </summary>
internal static class WebViewWarmupHost
{
    private static Window? _window;
    private static Grid? _root;
    private static WebView2CompositionControl? _webView;
    private static bool _started;

    public static void EnsureStarted()
    {
        if (_started)
        {
            return;
        }
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted)
        {
            return;
        }
        if (!dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(new Action(EnsureStarted));
            return;
        }
        if (_started)
        {
            return;
        }
        _started = true;
        _ = StartAsync();
    }

    private static async Task StartAsync()
    {
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            var environment = await WebViewPanelHost.SharedEnvironmentAsync();
            EnsureWindow();

            var webView = new WebView2CompositionControl
            {
                Width = 1,
                Height = 1,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                IsHitTestVisible = false
            };
            try
            {
                webView.DefaultBackgroundColor = System.Drawing.Color.Transparent;
            }
            catch
            {
            }
            _root!.Children.Add(webView);
            if (_window!.IsVisible == false)
            {
                _window.Show();
            }

            await webView.EnsureCoreWebView2Async(environment);
            var core = webView.CoreWebView2
                ?? throw new InvalidOperationException("Warmup WebView2 returned no CoreWebView2.");
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Navigate("about:blank");
            _webView = webView;
            HookTrace.Write(
                $"webview.warmup done ms={Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds:F1}");
        }
        catch (Exception ex)
        {
            HookTrace.Write(
                $"webview.warmup failed ms={Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds:F1} " +
                $"err={ex.GetType().Name}:{ex.Message}");
            Reset();
            _started = false;
        }
    }

    private static void EnsureWindow()
    {
        if (_window != null)
        {
            return;
        }
        _root = new Grid
        {
            Width = 1,
            Height = 1,
            Background = Brushes.Transparent,
            ClipToBounds = true
        };
        _window = new Window
        {
            Content = _root,
            Width = 1,
            Height = 1,
            Left = -32000,
            Top = -32000,
            WindowStartupLocation = WindowStartupLocation.Manual,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Opacity = 0.01,
            ShowActivated = false,
            ShowInTaskbar = false,
            Focusable = false,
            IsHitTestVisible = false
        };
    }

    private static void Reset()
    {
        try
        {
            _root?.Children.Clear();
        }
        catch
        {
        }
        try
        {
            _window?.Close();
        }
        catch
        {
        }
        _webView = null;
        _root = null;
        _window = null;
    }
}
