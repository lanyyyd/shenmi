using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using SteamEyaWinUI.Services;
using Windows.Graphics;

namespace SteamEyaWinUI;

/// <summary>
/// 应用内通知浮窗：工作区右下角的无边框置顶卡片。
///
/// 存在的理由：Windows App SDK 的系统通知在「未打包 + Win10 + 管理员运行 + 系统通知总开关关闭」
/// 等情况下会**静默失败**（不抛异常、不进日志），用户完全无感。这个浮窗不依赖任何系统通知机制，
/// 只要进程在跑就一定可见，用作兜底通道；系统通知可用时两者并行，用户可在设置里二选一。
///
/// 交互：点击卡片触发 <see cref="Clicked"/> 并关闭；右上角 × 仅关闭；6 秒后自动淡出。
/// 多条通知向上堆叠，最多 4 条，超出时最早的先退场。
/// </summary>
public sealed partial class ToastWindow : Window
{
    private const double WidthDip = 380;
    private const double HeightDip = 104;
    private const int MaxStacked = 4;
    private const int GapDip = 10;
    private const int EdgeDip = 16;
    private const double VisibleSeconds = 6;

    private const int HwndTopmost = -1;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    private static readonly List<ToastWindow> Open = [];

    private readonly DispatcherTimer _lifeTimer;
    private readonly DispatcherTimer _fadeTimer;
    private bool _closing;
    private double _opacity;

    /// <summary>点击卡片时触发（关闭按钮不触发）。</summary>
    public event Action? Clicked;

    public ToastWindow(string title, string body, string? glyph = null)
    {
        InitializeComponent();

        TitleText.Text = title;
        BodyText.Text = body;
        if (!string.IsNullOrWhiteSpace(glyph))
        {
            IconGlyph.Glyph = glyph;
        }

        Title = "SteamEYA";
        Card.Opacity = 0;

        // 跟随用户在设置里选的主题：浮窗是独立 Window，不会自动继承主窗口的 RequestedTheme。
        try
        {
            var settings = AppState.SettingsService.Load();
            Card.RequestedTheme = UiColorService.ResolveElementTheme(settings.Theme, settings.UiColor);
        }
        catch
        {
            // 读不到设置就用系统主题。
        }

        var presenter = AppWindow.Presenter as OverlappedPresenter;
        if (presenter is not null)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        _lifeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(VisibleSeconds) };
        _lifeTimer.Tick += (_, _) =>
        {
            _lifeTimer.Stop();
            BeginFadeOut();
        };

        _fadeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _fadeTimer.Tick += OnFadeTick;

        Closed += (_, _) =>
        {
            lock (Open)
            {
                Open.Remove(this);
            }
        };
    }

    /// <summary>
    /// 退出程序时统一关掉所有浮窗：WinUI 3 的窗口都关闭后消息循环才结束，
    /// 留着一条浮窗会让进程退不干净（任务管理器里残留）。
    /// </summary>
    internal static void CloseAll()
    {
        ToastWindow[] all;
        lock (Open)
        {
            all = Open.ToArray();
            Open.Clear();
        }

        foreach (var toast in all)
        {
            try
            {
                toast.Close();
            }
            catch
            {
                // 已经关掉的忽略。
            }
        }
    }

    /// <summary>显示浮窗：先摆好位置再淡入，避免出现「先在屏幕中央闪一下」。</summary>
    public void ShowToast()
    {
        try
        {
            TrimStack();

            // 先放到屏幕外显示，保证 XAML 内容完成首帧渲染，再移到右下角。
            AppWindow.MoveAndResize(new RectInt32(-20000, -20000, PhysicalWidth, PhysicalHeight));
            AppWindow.Show();

            PositionAtCorner();
            BeginFadeIn();
            _lifeTimer.Start();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"显示通知浮窗失败：{ex.Message}");
            Close();
        }
    }

    private void TrimStack()
    {
        ToastWindow? oldest = null;
        lock (Open)
        {
            if (Open.Count >= MaxStacked)
            {
                oldest = Open[0];
            }
        }

        // 超出上限时让最早的一条先退场（在锁外调用，避免关闭回调重入 Gate）。
        oldest?.Close();
    }

    private void PositionAtCorner()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = hwnd == 0 ? 1.0 : GetDpiForWindow(hwnd) / 96.0;

        var width = PhysicalWidth;
        var height = PhysicalHeight;
        var edge = (int)Math.Round(EdgeDip * scale);
        var gap = (int)Math.Round(GapDip * scale);

        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var work = area.WorkArea;

        int index;
        lock (Open)
        {
            index = Open.Count;
            Open.Add(this);
        }

        var x = work.X + work.Width - width - edge;
        var y = work.Y + work.Height - height - edge - index * (height + gap);

        // 堆到屏幕顶部以外时贴住上沿，避免跑出可见区域。
        var minY = work.Y + edge;
        if (y < minY)
        {
            y = minY;
        }

        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));

        if (hwnd != 0)
        {
            // 不抢焦点：通知出现时用户可能正在别的窗口打字。
            SetWindowPos(hwnd, HwndTopmost, x, y, width, height, SwpNoActivate | SwpShowWindow);
        }
    }

    private int PhysicalWidth => (int)Math.Round(WidthDip * CurrentScale);

    private int PhysicalHeight => (int)Math.Round(HeightDip * CurrentScale);

    private double CurrentScale
    {
        get
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            return hwnd == 0 ? 1.0 : GetDpiForWindow(hwnd) / 96.0;
        }
    }

    private void BeginFadeIn()
    {
        _opacity = 0;
        Card.Opacity = 0;
        _fadeTimer.Start();
    }

    private void OnFadeTick(object? sender, object e)
    {
        // 淡入到 1 后停；淡出到 0 后真正关窗。
        var step = _closing ? -0.09 : 0.09;
        _opacity = Math.Clamp(_opacity + step, 0, 1);
        Card.Opacity = _opacity;

        if (!_closing && _opacity >= 1)
        {
            _fadeTimer.Stop();
        }
        else if (_closing && _opacity <= 0)
        {
            _fadeTimer.Stop();
            Close();
        }
    }

    private void BeginFadeOut()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        _opacity = Card.Opacity;
        _fadeTimer.Start();
    }

    private void OnCardTapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        _lifeTimer.Stop();

        try
        {
            Clicked?.Invoke();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"处理通知点击失败：{ex.Message}");
        }

        BeginFadeOut();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        // RoutedEventArgs 没有 Handled；Button 内部已把指针事件标记为已处理，
        // 不会再冒泡成卡片的 Tapped，这里只做关闭。
        _lifeTimer.Stop();
        BeginFadeOut();
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint hWnd, int hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
