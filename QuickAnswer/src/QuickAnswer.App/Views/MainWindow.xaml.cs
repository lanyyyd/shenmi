using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using QuickAnswer.App.Interop;
using QuickAnswer.App.Services;
using QuickAnswer.Core.Capture;
using QuickAnswer.Core.Detect;
using QuickAnswer.Core.Match;
using QuickAnswer.Core.Models;
using QuickAnswer.Core.Ocr;
using QuickAnswer.Core.Orchestration;
using QuickAnswer.Core.Parse;
using static QuickAnswer.App.Interop.NativeMethods;

namespace QuickAnswer.App.Views;

public sealed partial class MainWindow : Window
{
    private const int HotkeyRecognize = 1;
    private const int HotkeyLoop = 2;
    private const int HotkeyStealth = 3;
    private const int HotkeyPanic = 4;

    private readonly WinRtOcrEngine _ocr;
    private readonly AnswerOrchestrator _orchestrator;

    private CancellationTokenSource? _loopCts;
    private SubclassProc? _wndProc;

    private OverlayWindow? _overlay;
    private StealthWindow? _stealth;

    private ulong _lastHash;
    private string _lastStem = "";
    private DateTime _lastTriggerUtc = DateTime.MinValue;
    private bool _working;
    private bool _ready;

    public MainWindow()
    {
        InitializeComponent();

        Title = "QuickAnswer";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarArea);

        WindowHelper.Resize(this, 420, 680);
        WindowHelper.CenterOnScreen(this, 420, 680);
        WindowHelper.HideFromSwitcher(this);

        _ocr = WinRtOcrEngine.Create();
        _orchestrator = new AnswerOrchestrator(App.Api, App.Config)
        {
            StatusChanged = s => Post(() => StatusLabel.Text = s),
            AnswerReady = _ => { },
        };

        LoadConfigIntoUi();
        HookEvents();

        Activated += OnFirstActivated;
        Closed += OnClosed;
    }

    // ---------------------------------------------------------------- 初始化

    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_ready)
            return;
        _ready = true;

        if (App.Config.ProtectFromCapture)
            WindowHelper.ProtectFromCapture(this);

        SetupHotkeys();

        if (!_ocr.IsAvailable)
        {
            SetStatus("OCR 不可用", "#FFDC2626");
            PreviewAnswer.Text = _ocr.UnavailableReason ?? "系统缺少 OCR 语言包";
        }

        _ = LoadModelsAsync();

        if (App.Config.AutoLoop && App.Config.Region is not null)
            StartLoop();
    }

    private void HookEvents()
    {
        try
        {
            var tb = AppWindow.TitleBar;
            tb.ButtonBackgroundColor = Colors.Transparent;
            tb.ButtonInactiveBackgroundColor = Colors.Transparent;
        }
        catch
        {
        }
    }

    private void LoadConfigIntoUi()
    {
        var cfg = App.Config;

        AutoLoopSwitch.IsOn = cfg.AutoLoop;
        StealthSwitch.IsOn = cfg.StealthMode;
        ProtectSwitch.IsOn = cfg.ProtectFromCapture;

        OpacitySlider.Value = Math.Clamp(cfg.OverlayOpacity * 100, 10, 100);
        OpacityLabel.Text = $"{(int)OpacitySlider.Value}%";

        UpdateRegionLabel();
        UpdateStats();
    }

    private async Task LoadModelsAsync()
    {
        try
        {
            var models = await App.Api.GetModelsAsync();

            if (models is null)
            {
                // 请求本身失败：多半是后端还没装题库模块，或者服务器地址少了路径
                SetStatus("题库模块未就绪", "#FFDC2626");
                PreviewAnswer.Text = "连不上题库接口，检查后端是否已部署 /admin-qa.html 那套模块";
                PreviewDetail.Text = App.Config.ServerUrl + "/api/v1/qa/models";
                SourceText.Text = "离线";
                return;
            }

            ModelBox.ItemsSource = models;

            if (models.Count == 0)
            {
                PreviewAnswer.Text = "后台还没有配置 AI 模型";
                PreviewDetail.Text = "去 " + App.Config.ServerUrl + "/admin-qa.html 添加一个";
                return;
            }

            var current = models.FirstOrDefault(m => m.Id == App.Config.ModelId) ?? models.FirstOrDefault();
            if (current is not null)
            {
                ModelBox.SelectedItem = current;
                App.Config.ModelId = current.Id;
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("MainWindow.LoadModels", ex);
        }
    }

    // ---------------------------------------------------------------- 热键

    private void SetupHotkeys()
    {
        var hwnd = WindowHelper.Hwnd(this);
        if (hwnd == IntPtr.Zero)
            return;

        _wndProc = (h, msg, w, l, id, data) =>
        {
            if (msg == WM_HOTKEY)
            {
                switch (w.ToInt32())
                {
                    case HotkeyRecognize:
                        _ = RecognizeOnceAsync();
                        break;
                    case HotkeyLoop:
                        AutoLoopSwitch.IsOn = !AutoLoopSwitch.IsOn;
                        break;
                    case HotkeyStealth:
                        StealthSwitch.IsOn = !StealthSwitch.IsOn;
                        break;
                    case HotkeyPanic:
                        // 应急：任何浮层出问题（白屏、挡住鼠标）按这个立刻清场
                        HideAllOverlays();
                        if (StealthSwitch.IsOn)
                            StealthSwitch.IsOn = false;
                        SetStatus("已隐藏所有浮层", "#FF6B7280");
                        break;
                }
                return IntPtr.Zero;
            }

            return DefSubclassProc(h, msg, w, l);
        };

        SetWindowSubclass(hwnd, _wndProc, (UIntPtr)101, IntPtr.Zero);

        RegisterHotKey(hwnd, HotkeyRecognize, MOD_ALT | MOD_NOREPEAT, 0x51);  // Alt+Q
        RegisterHotKey(hwnd, HotkeyLoop, MOD_ALT | MOD_NOREPEAT, 0x57);       // Alt+W
        RegisterHotKey(hwnd, HotkeyStealth, MOD_ALT | MOD_NOREPEAT, 0x44);    // Alt+D

        // 应急退出：Alt+Shift+D。浮层白屏/挡鼠标时靠它自救
        RegisterHotKey(hwnd, HotkeyPanic, MOD_ALT | MOD_SHIFT | MOD_NOREPEAT, 0x44);
    }

    private void ReleaseHotkeys()
    {
        var hwnd = WindowHelper.Hwnd(this);
        if (hwnd == IntPtr.Zero)
            return;

        UnregisterHotKey(hwnd, HotkeyRecognize);
        UnregisterHotKey(hwnd, HotkeyLoop);
        UnregisterHotKey(hwnd, HotkeyStealth);
        UnregisterHotKey(hwnd, HotkeyPanic);
    }

    // ---------------------------------------------------------------- 监听循环

    private void StartLoop()
    {
        if (_loopCts is not null)
            return;

        _loopCts = new CancellationTokenSource();
        SetStatus("监听中", "#FF166534");
        _ = Task.Run(() => LoopAsync(_loopCts.Token));
    }

    private void StopLoop()
    {
        _loopCts?.Cancel();
        _loopCts?.Dispose();
        _loopCts = null;
        SetStatus("已暂停", "#FF6B7280");
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_working && App.Config.Region is { } region)
                    await TickAsync(region, false, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // 单帧失败不该让循环停下来
            }

            try
            {
                var delay = Math.Clamp(App.Config.IntervalMs, 200, 5000);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch
            {
                break;
            }
        }
    }

    /// <summary>
    /// 一次检测周期。绝大多数情况下在第 3 行就返回了 —— 这正是设计意图：
    /// 画面没变就不做 OCR，否则每秒跑一次 OCR 会把 CPU 吃满。
    /// </summary>
    private async Task TickAsync(ScreenRect region, bool force, CancellationToken ct)
    {
        var gray = ScreenCapturer.CaptureGray(region, DHash.W, DHash.H);
        if (gray is null)
            return;

        var hash = DHash.Compute(gray);
        int dist = DHash.Hamming(hash, _lastHash);

        if (!force && _lastHash != 0 && dist <= ChangeThresholds.Unchanged)
            return;

        // 画面还在动（加载动画、弹窗渐入）就先等等，别把过渡态当成新题目
        if (!force && dist <= ChangeThresholds.PageSwitch)
        {
            await Task.Delay(ChangeThresholds.SettleDelay, ct).ConfigureAwait(false);

            var again = ScreenCapturer.CaptureGray(region, DHash.W, DHash.H);
            if (again is null)
                return;

            var hash2 = DHash.Compute(again);
            if (DHash.Hamming(hash, hash2) > 2)
                return;   // 仍在变化，这一轮放弃

            hash = hash2;
        }

        _lastHash = hash;

        if (!force && DateTime.UtcNow - _lastTriggerUtc < ChangeThresholds.Debounce)
            return;

        await RecognizeRegionAsync(region, force, ct).ConfigureAwait(false);
    }

    private async Task RecognizeRegionAsync(ScreenRect region, bool force, CancellationToken ct)
    {
        if (_working)
            return;
        _working = true;
        Post(() =>
        {
            BusyBar.Visibility = Visibility.Visible;
            SetStatus("识别中…", "#FF2563EB");
        });

        try
        {
            var img = ScreenCapturer.CaptureScaled(region, App.Config.OcrScale);
            if (img is null)
                return;

            var lines = await _ocr.RecognizeAsync(img, App.Config.OcrScale).ConfigureAwait(false);
            if (lines.Count == 0 || ct.IsCancellationRequested)
                return;

            var question = QuestionParser.Parse(lines);

            // 这一帧不像题目：可能是桌面图标、报错弹窗、加载占位
            if (question.RawOcr.Length < 6)
                return;
            if (TextNormalizer.ChineseRatio(question.RawOcr) < 0.25 && question.Options.Count == 0)
                return;

            var stemNorm = TextNormalizer.Normalize(question.Stem ?? "");

            if (!force && _lastStem.Length > 0)
            {
                var sim = Similarity.Dice(stemNorm, _lastStem);
                if (sim >= App.Config.ChangeThreshold)
                {
                    Post(() => SetStatus("监听中", "#FF166534"));
                    return;   // 还是同一题
                }
            }

            _lastStem = stemNorm;
            _lastTriggerUtc = DateTime.UtcNow;

            var result = await _orchestrator.ProcessAsync(question, ct).ConfigureAwait(false);
            if (result is null || ct.IsCancellationRequested)
                return;

            Post(() => Render(question, result));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Post(() => SetStatus("出错：" + ex.Message, "#FFDC2626"));
        }
        finally
        {
            _working = false;
            Post(() =>
            {
                BusyBar.Visibility = Visibility.Collapsed;
                if (StatusLabel.Text is "识别中…" or "查题库…" or "问 AI…")
                    SetStatus("监听中", "#FF166534");
            });
        }
    }

    // ---------------------------------------------------------------- 呈现

    private void Render(ParsedQuestion question, AnswerResult result)
    {
        UpdatePreview(question, result);

        if (App.Config.StealthMode)
            ShowStealth(question, result);
        else
            ShowOverlay(result);

        UpdateStats();
        App.Config.Save();
    }

    private void UpdatePreview(ParsedQuestion question, AnswerResult result)
    {
        if (result.Text.Length == 0 && result.Letter.Length == 0)
        {
            PreviewLetter.Text = "!";
            PreviewAnswer.Text = result.Detail;
            PreviewDetail.Text = "";
            SourceText.Text = "失败";
            return;
        }

        bool isChoiceLike = question.Options.Count > 0;
        PreviewLetter.Text = isChoiceLike
            ? (result.Letter.Length > 0 ? result.Letter : "?")
            : (question.Type == QuestionType.Judge ? Truncate(result.Text, 1) : "A");

        PreviewAnswer.Text = Truncate(result.Text, 30);
        PreviewDetail.Text = result.Detail;
        SourceText.Text = result.Source.Length > 0 ? result.Source : "—";

        SourceBadge.Background = new SolidColorBrush(
            result.Source == "题库" ? Windows.UI.Color.FromArgb(255, 22, 101, 52) : Windows.UI.Color.FromArgb(255, 30, 41, 59));
    }

    private void ShowOverlay(AnswerResult result)
    {
        if (_stealth is not null)
        {
            _stealth.Close();
            _stealth = null;
        }

        _overlay ??= new OverlayWindow();
        _overlay.Render(result);
        _overlay.Activate();
    }

    private void ShowStealth(ParsedQuestion question, AnswerResult result)
    {
        if (_overlay is not null)
        {
            _overlay.Close();
            _overlay = null;
        }

        _stealth ??= new StealthWindow();
        // 不调 Activate：会把键盘焦点从题目软件抢走
        _stealth.Render(question, result);
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s))
            return "";
        s = s.Replace('\n', ' ').Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }

    // ---------------------------------------------------------------- 交互

    private async void OnSelectRegionClick(object sender, RoutedEventArgs e)
    {
        HideAllOverlays();

        var picker = new RegionSelectorWindow();
        var rect = await picker.PickAsync();

        if (rect is null || rect.Value.IsEmpty)
        {
            Post(() => SetStatus("已取消框选", "#FF6B7280"));
            return;
        }

        App.Config.Region = rect.Value;
        App.Config.Save();

        _lastHash = 0;
        _lastStem = "";

        UpdateRegionLabel();
        SetStatus("监听中", "#FF166534");
    }

    private async void OnRecognizeClick(object sender, RoutedEventArgs e) => await RecognizeOnceAsync();

    private async Task RecognizeOnceAsync()
    {
        if (App.Config.Region is not { } region)
        {
            SetStatus("请先框选题目区域", "#FFDC2626");
            return;
        }

        _lastHash = 0;
        _lastStem = "";

        try
        {
            await TickAsync(region, true, CancellationToken.None);
        }
        catch
        {
        }
    }

    private async void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = "账户与连接",
                Content = BuildAccountPanel(),
                PrimaryButtonText = "退出登录",
                SecondaryButtonText = "清除框选",
                CloseButtonText = "关闭",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
            };

            var result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
                DoLogout();
            else if (result == ContentDialogResult.Secondary)
                ClearRegion();
        }
        catch (Exception ex)
        {
            CrashLog.Write("MainWindow.Settings", ex);
        }
    }

    private StackPanel BuildAccountPanel()
    {
        var panel = new StackPanel { Spacing = 14, MinWidth = 320 };

        void Add(string label, string value)
        {
            var block = new StackPanel { Spacing = 3 };
            block.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 11,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 107, 114, 128)),
            });
            block.Children.Add(new TextBlock
            {
                Text = value,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 20, 23, 28)),
            });
            panel.Children.Add(block);
        }

        Add("账号", App.Session.DisplayName);
        Add("服务器", App.Config.ServerUrl);
        Add("今日", $"{App.Config.TodayCount} 题 · 命中 {App.Config.TodayHit}");
        Add("热键", "Alt+Q 识别 · Alt+W 循环 · Alt+D 隐蔽模式");

        return panel;
    }

    private void DoLogout()
    {
        StopLoop();
        HideAllOverlays();

        App.Config.Token = null;
        App.Config.Username = null;
        App.Config.Nickname = null;
        App.Config.Save();

        App.Session.SignOut();
        App.ShowLogin();
    }

    private void ClearRegion()
    {
        App.Config.Region = null;
        App.Config.Save();

        _lastHash = 0;
        _lastStem = "";
        UpdateRegionLabel();
        SetStatus("已清除框选区域", "#FF6B7280");
    }

    private void OnAutoLoopToggled(object sender, RoutedEventArgs e)
    {
        App.Config.AutoLoop = AutoLoopSwitch.IsOn;
        App.Config.Save();

        if (AutoLoopSwitch.IsOn)
            StartLoop();
        else
            StopLoop();
    }

    private void OnStealthToggled(object sender, RoutedEventArgs e)
    {
        App.Config.StealthMode = StealthSwitch.IsOn;
        App.Config.Save();

        if (StealthSwitch.IsOn)
        {
            _overlay?.Close();
            _overlay = null;
        }
        else
        {
            _stealth?.Close();
            _stealth = null;
        }
    }

    private void OnProtectToggled(object sender, RoutedEventArgs e)
    {
        App.Config.ProtectFromCapture = ProtectSwitch.IsOn;
        App.Config.Save();

        if (ProtectSwitch.IsOn)
            WindowHelper.ProtectFromCapture(this);
        else
            WindowHelper.DisableProtection(this);
    }

    private void OnOpacityChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        // XAML 解析阶段就会进来一次：Slider 的 Minimum="10" 赋值时，默认 Value=0 被强制
        // 拉起到 10，于是 ValueChanged 提前触发 —— 那时 OpacityLabel 还没被创建出来。
        // 不挡这一下，整个窗口会在 InitializeComponent 里抛 NRE，外面只会看到
        // "Failed to assign to property RangeBase.Minimum" 这种完全对不上号的错误。
        if (OpacityLabel is null)
            return;

        var pct = (int)Math.Round(e.NewValue);
        OpacityLabel.Text = $"{pct}%";

        App.Config.OverlayOpacity = pct / 100.0;
        _overlay?.SetOpacity(pct / 100.0);
    }

    private void OnModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModelBox.SelectedItem is ModelInfo m)
        {
            App.Config.ModelId = m.Id;
            App.Config.Save();
        }
    }

    // ---------------------------------------------------------------- 状态显示

    private void SetStatus(string text, string colorHex)
    {
        StatusLabel.Text = text;
        try
        {
            StatusDot.Fill = new SolidColorBrush(ParseColor(colorHex));
        }
        catch
        {
        }
    }

    private void UpdateRegionLabel()
    {
        if (App.Config.Region is { } r)
            RegionLabel.Text = $"{r.X}, {r.Y} · {r.Width}×{r.Height}";
        else
            RegionLabel.Text = "未框选区域";
    }

    private void UpdateStats()
    {
        App.Config.RollDayIfNeeded();
        StatsLabel.Text = $"今日 {App.Config.TodayCount} 题 · 命中 {App.Config.TodayHit}";
    }

    private void HideAllOverlays()
    {
        _overlay?.Close();
        _overlay = null;
        _stealth?.Close();
        _stealth = null;
    }

    private void Post(Action action)
    {
        try
        {
            DispatcherQueue.TryEnqueue(() => action());
        }
        catch
        {
        }
    }

    private static Windows.UI.Color ParseColor(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 8)
        {
            return Windows.UI.Color.FromArgb(
                Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16),
                Convert.ToByte(hex[4..6], 16), Convert.ToByte(hex[6..8], 16));
        }
        if (hex.Length == 6)
        {
            return Windows.UI.Color.FromArgb(255,
                Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16));
        }
        return Colors.Gray;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        ReleaseHotkeys();
        StopLoop();
        HideAllOverlays();
    }
}
