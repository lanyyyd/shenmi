using System.Diagnostics;
using System.Text;
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
using QuickAnswer.Core.Diagnostics;
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
    private readonly OcrPipeline _pipeline;
    private readonly AnswerOrchestrator _orchestrator;

    private CancellationTokenSource? _loopCts;
    private SubclassProc? _wndProc;

    private OverlayWindow? _overlay;
    private StealthWindow? _stealth;

    private byte[]? _lastFrame;
    private string _lastFingerprint = "";
    private int _sameFrameTicks;
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
        // 主窗口就是普通软件窗口：Alt+Tab 能看到、任务栏有图标。
        // 隐蔽只作用于悬浮窗和标注层，不该让用户找不到主程序。
        AppWindow.IsShownInSwitchers = true;

        _ocr = WinRtOcrEngine.Create();
        _pipeline = new OcrPipeline(_ocr);
        OcrDiagnostics.Enabled = App.Config.SaveDiagnostics;
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

        // 上次是在监听状态就直接接着监听；没框选区域就别自动开跑
        bool resume = App.Config.AutoLoop && App.Config.Region is not null;
        UpdateLoopButtons(resume);
        if (resume)
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

        UpdateLoopButtons(false);           // 构造阶段必然没在监听，真正的状态在首次激活时定
        StealthSwitch.IsOn = cfg.StealthMode;
        ProtectSwitch.IsOn = cfg.ProtectFromCapture;

        OpacitySlider.Value = Math.Clamp(cfg.OverlayOpacity * 100, 0, 100);
        OpacityLabel.Text = DescribeOpacity(OpacitySlider.Value);

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
                        SetAutoLoop(!App.Config.AutoLoop);
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
    /// 一次检测周期。绝大多数情况下在「画面没变」那步就返回了 —— 这正是设计意图：
    /// 画面没变就不做 OCR，否则每秒跑一次 OCR 会把 CPU 吃满。
    ///
    /// 但闸门不能太粗：以前用 9×8 dHash 判「变没变」，换一行字指纹只差几位，
    /// 直接被当成没变，用户点了下一题也不触发 —— 只能手动识别。
    /// 现在用 64×64 逐格比较（见 FrameDiff），并在「没变」时允许重试两次，
    /// 因为 OCR 偶尔会把一帧读空。
    /// </summary>
    private async Task TickAsync(ScreenRect region, bool force, CancellationToken ct)
    {
        var frame = ScreenCapturer.CaptureGray(region, FrameDiff.W, FrameDiff.H);
        if (frame is null)
            return;

        bool changed = force || FrameDiff.HasChanged(_lastFrame, frame);

        if (changed)
        {
            _sameFrameTicks = 0;

            // 画面还在动（加载动画、弹窗渐入）就先等等，别把过渡态当成新题目
            if (!force)
            {
                await Task.Delay(ChangeThresholds.SettleDelay, ct).ConfigureAwait(false);

                var again = ScreenCapturer.CaptureGray(region, FrameDiff.W, FrameDiff.H);
                if (again is not null)
                {
                    // 只有「整页还在动」才放弃这一轮。用大阈值是为了别被一个闪烁的光标
                    // 或秒表数字卡住 —— 那种小改动该照样识别。
                    if (FrameDiff.HasChanged(frame, again, FrameDiff.BusyCells))
                    {
                        // 仍在变化，这一轮放弃；记下当前画面，下一轮接着判
                        _lastFrame = again;
                        return;
                    }

                    frame = again;
                }
            }
        }
        else
        {
            // 和上一帧一模一样。允许重试两次：上一次可能是 OCR 读空了，
            // 不是画面没问题。超过就安静等画面变化。
            if (_sameFrameTicks++ >= ChangeThresholds.MaxSameFrameRetries)
                return;
        }

        _lastFrame = frame;

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
            var outcome = await _pipeline.RunAsync(region, App.Config.OcrScale, ct).ConfigureAwait(false);
            if (outcome is null || outcome.Lines.Count == 0 || ct.IsCancellationRequested)
                return;

            var question = QuestionParser.Parse(outcome.Lines);

            // 这一帧不像题目：可能是桌面图标、报错弹窗、加载占位。
            // 注意别把这一帧记成"已处理"—— 下一帧很可能就是真题。
            if (question.RawOcr.Length < 6)
                return;
            if (TextNormalizer.ChineseRatio(question.RawOcr) < 0.25 && question.Options.Count == 0)
                return;

            // 图太糊、或者根本没框到题目：认出来的东西撑不起一次检索。
            // 这时候送出去问 AI，只会拿到一个看起来很确定的错答案 —— 比不给答案更糟。
            // 手动识别（force）不受此限，用户都主动点了，就照他的意思跑。
            if (!force && outcome.Quality.IsPoor)
            {
                Post(() => SetStatus("这块看不清，建议重新框选", "#FFD97706"));
                _ = OcrDiagnostics.SaveAsync(outcome, region, question, "质量分过低，未作答");
                return;
            }

            var fingerprint = BuildFingerprint(question);

            if (!force && _lastFingerprint.Length > 0)
            {
                var sim = Similarity.Dice(fingerprint, _lastFingerprint);
                if (sim >= App.Config.ChangeThreshold)
                {
                    Post(() => SetStatus("监听中", "#FF166534"));
                    return;   // 还是同一题
                }
            }

            _lastFingerprint = fingerprint;
            _lastTriggerUtc = DateTime.UtcNow;

            var result = await _orchestrator.ProcessAsync(question, ct).ConfigureAwait(false);
            if (result is null || ct.IsCancellationRequested)
                return;

            Post(() => Render(question, result));

            // 留样只在两种情况：用户手动点的（他明确觉得有问题），或者这题没出答案。
            // 自动出对的那些不占磁盘，20 个名额留给真正需要看的。
            bool failed = result.Text.Length == 0 && result.Letter.Length == 0;
            if (force || failed)
                _ = OcrDiagnostics.SaveAsync(outcome, region, question, force ? "手动识别" : "未取到答案");
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
            ShowStealth(result);
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

    private void ShowStealth(AnswerResult result)
    {
        if (_overlay is not null)
        {
            _overlay.Close();
            _overlay = null;
        }

        _stealth ??= new StealthWindow();
        // 不调 Activate：会把键盘焦点从题目软件抢走
        _stealth.Render(result);
    }

    /// <summary>
    /// 「是不是同一题」的指纹：题干 + 全部选项。
    ///
    /// 只比题干会被模板题骗过去 —— 「下列说法正确的是」这种句子在整张卷子里
    /// 重复十几次，光比题干就永远是同一题，新题被静默丢掉，用户只能手动识别。
    /// 选项也进指纹之后，题干雷同、选项不同的两道题会被正确区分开。
    /// </summary>
    private static string BuildFingerprint(ParsedQuestion q)
    {
        var sb = new StringBuilder(q.Stem ?? "");
        foreach (var o in q.Options)
            sb.Append('|').Append(o.Text);

        return TextNormalizer.Normalize(sb.ToString());
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

        _lastFrame = null;
        _lastFingerprint = "";
        _sameFrameTicks = 0;

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

        _lastFrame = null;
        _lastFingerprint = "";
        _sameFrameTicks = 0;

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
        Add("热键", "Alt+Q 识别 · Alt+W 循环 · Alt+D 隐蔽模式 · Alt+Shift+D 应急隐藏");
        Add("后端自检", App.Config.ServerUrl + "/api/v1/qa/diag");

        panel.Children.Add(BuildOverlaySection());
        panel.Children.Add(BuildDiagnosticsSection());

        return panel;
    }

    /// <summary>设置页里的诊断区：留样开关 + 打开目录。</summary>
    private UIElement BuildDiagnosticsSection()
    {
        var muted = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 107, 114, 128));

        var box = new StackPanel { Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };

        box.Children.Add(new TextBlock
        {
            Text = "识别诊断",
            FontSize = 11,
            Foreground = muted,
        });

        box.Children.Add(new TextBlock
        {
            Text = "识别不准时，最近 20 次的截图和识别文本会留在这里。把这几个文件发出来，就能直接看出是字太小、深色底没反相、还是框选歪了。",
            FontSize = 10.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = muted,
        });

        var row = new Grid();
        row.Children.Add(new TextBlock
        {
            Text = "保存识别样本",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var toggle = new ToggleSwitch
        {
            IsOn = App.Config.SaveDiagnostics,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            OnContent = "",
            OffContent = "",
        };
        toggle.Toggled += (_, _) =>
        {
            App.Config.SaveDiagnostics = toggle.IsOn;
            OcrDiagnostics.Enabled = toggle.IsOn;
            App.Config.Save();
        };
        row.Children.Add(toggle);
        box.Children.Add(row);

        var open = new Button
        {
            Content = "打开诊断目录",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        open.Click += (_, _) => OpenDiagnosticsFolder();
        box.Children.Add(open);

        return box;
    }

    private void OpenDiagnosticsFolder()
    {
        try
        {
            Directory.CreateDirectory(OcrDiagnostics.Dir);
            Process.Start(new ProcessStartInfo
            {
                FileName = OcrDiagnostics.Dir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            SetStatus("打不开目录：" + ex.Message, "#FFDC2626");
        }
    }

    /// <summary>设置页里的浮层设置：点击穿透、位置复位。</summary>
    private UIElement BuildOverlaySection()
    {
        var box = new StackPanel { Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };

        box.Children.Add(new TextBlock
        {
            Text = "答案浮层",
            FontSize = 11,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 107, 114, 128)),
        });

        var row = new Grid();
        row.Children.Add(new TextBlock
        {
            Text = "点击穿透（开启后无法拖动）",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Width = 210,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var toggle = new ToggleSwitch
        {
            IsOn = App.Config.StealthClickThrough,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            OnContent = "",
            OffContent = "",
        };
        toggle.Toggled += (_, _) =>
        {
            App.Config.StealthClickThrough = toggle.IsOn;
            App.Config.Save();
        };
        row.Children.Add(toggle);
        box.Children.Add(row);

        var reset = new Button
        {
            Content = "把浮层复位到屏幕中央",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        reset.Click += (_, _) => ResetOverlayPositions();
        box.Children.Add(reset);

        return box;
    }

    /// <summary>把拖丢了的浮层拉回屏幕中央。</summary>
    private void ResetOverlayPositions()
    {
        App.Config.StealthLeft = -1;
        App.Config.StealthTop = -1;
        App.Config.OverlayLeft = -1;
        App.Config.OverlayTop = -1;
        App.Config.Save();

        _stealth?.ResetPosition();
        _overlay?.ResetPosition();

        SetStatus("浮层已复位到屏幕中央", "#FF6B7280");
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

        _lastFrame = null;
        _lastFingerprint = "";
        _sameFrameTicks = 0;
        UpdateRegionLabel();
        SetStatus("已清除框选区域", "#FF6B7280");
    }

    // ---------------------------------------------------------------- 开始 / 暂停

    private void OnStartClick(object sender, RoutedEventArgs e) => SetAutoLoop(true);

    private void OnPauseClick(object sender, RoutedEventArgs e) => SetAutoLoop(false);

    /// <summary>
    /// 开始/暂停的唯一入口（按钮、Alt+W、启动续跑都走这里），
    /// 免得按钮状态和实际循环状态对不上。
    /// </summary>
    private void SetAutoLoop(bool on, bool save = true)
    {
        if (on && App.Config.Region is null)
        {
            SetStatus("请先框选题目区域", "#FFDC2626");
            UpdateLoopButtons(false);
            return;
        }

        App.Config.AutoLoop = on;
        if (save)
            App.Config.Save();

        if (on)
        {
            // 重新开始时丢掉上一帧：屏幕上正显示的那道题应该马上被识别一次，
            // 而不是等用户翻页才动
            _lastFrame = null;
            _sameFrameTicks = 0;
            StartLoop();
        }
        else
        {
            StopLoop();
        }

        UpdateLoopButtons(on);
    }

    private void UpdateLoopButtons(bool running)
    {
        // 状态只靠「哪个可点」表达：跑着的时候开始变灰、暂停可点，反之亦然
        StartBtn.IsEnabled = !running;
        PauseBtn.IsEnabled = running;
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
        // XAML 解析阶段就会进来一次：Slider 的 Minimum 赋值时，默认 Value=0 可能被拉起，
        // 于是 ValueChanged 提前触发 —— 那时 OpacityLabel 还没被创建出来。
        // 不挡这一下，整个窗口会在 InitializeComponent 里抛 NRE，外面只会看到
        // "Failed to assign to property RangeBase.Minimum" 这种完全对不上号的错误。
        if (OpacityLabel is null)
            return;

        var pct = (int)Math.Round(e.NewValue);
        OpacityLabel.Text = DescribeOpacity(pct);

        App.Config.OverlayOpacity = pct / 100.0;

        // 两种呈现方式共用这一个设置：0 就是「完全不要背景色」
        _overlay?.SetOpacity(pct / 100.0);
        _stealth?.SetOpacity(pct / 100.0);
    }

    private static string DescribeOpacity(double percent)
        => percent <= 0 ? "0% · 无背景" : $"{(int)Math.Round(percent)}%";

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
