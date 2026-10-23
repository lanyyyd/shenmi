using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Services;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;

namespace SteamEyaWinUI;

/// <summary>
/// 全屏截图覆盖层：拖拽框选 → Enter/对勾复制，或另存为；Esc/叉号取消。
/// 交互对齐微信截图：开始框选后选区外压暗、选区内保持原图，工具条贴着选区右下角。
/// </summary>
public sealed partial class ScreenshotWindow : Window
{
    private readonly ScreenshotFrame _frame;

    private Point _startPoint;
    private Rect _selection;
    private bool _selecting;
    private bool _hasSelection;
    private bool _busy;

    public ScreenshotWindow(ScreenshotFrame frame)
    {
        _frame = frame;
        InitializeComponent();

        Title = "SteamEYA 截图";

        var presenter = AppWindow.Presenter as OverlappedPresenter;
        if (presenter is not null)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        AppWindow.MoveAndResize(new RectInt32(0, 0, frame.Width, frame.Height));

        CopyLabel.Text = Loc.T("Screenshot_Copy");
        SaveLabel.Text = Loc.T("Screenshot_Save");
        HintText.Text = Loc.T("Screenshot_Hint");
    }

    private async void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        Root.Focus(FocusState.Programmatic);

        try
        {
            var png = await ScreenshotService.EncodePngAsync(_frame.Pixels, _frame.Width, _frame.Height);
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(png);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }

            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            ShotImage.Source = bitmap;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"显示截图预览失败：{ex.Message}");
        }
    }

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_hasSelection)
        {
            UpdateSelection(_selection);
            PlaceToolbar(_selection);
        }
    }

    // ---------- 框选 ----------

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Root);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _startPoint = point.Position;
        _selecting = true;
        _hasSelection = false;
        Toolbar.Visibility = Visibility.Collapsed;
        MaskFull.Visibility = Visibility.Collapsed;

        UpdateSelection(new Rect(_startPoint, new Size(0, 0)));
        Root.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_selecting)
        {
            return;
        }

        var current = e.GetCurrentPoint(Root).Position;
        var rect = new Rect(
            Math.Min(_startPoint.X, current.X),
            Math.Min(_startPoint.Y, current.Y),
            Math.Abs(current.X - _startPoint.X),
            Math.Abs(current.Y - _startPoint.Y));

        UpdateSelection(rect);
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_selecting)
        {
            return;
        }

        _selecting = false;
        Root.ReleasePointerCapture(e.Pointer);

        _hasSelection = _selection.Width >= 4 && _selection.Height >= 4;
        if (_hasSelection)
        {
            PlaceToolbar(_selection);
            Toolbar.Visibility = Visibility.Visible;
        }
        else
        {
            // 只是点了一下没拖动：回到"未选择"状态，等待重新框选。
            ResetSelection();
        }

        e.Handled = true;
    }

    private void UpdateSelection(Rect rect)
    {
        _selection = rect;

        var width = Root.ActualWidth;
        var height = Root.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        rect = new Rect(
            Math.Clamp(rect.X, 0, width),
            Math.Clamp(rect.Y, 0, height),
            Math.Clamp(rect.Width, 0, width - Math.Clamp(rect.X, 0, width)),
            Math.Clamp(rect.Height, 0, height - Math.Clamp(rect.Y, 0, height)));

        // 上、下、左、右四块压暗，中间保持原亮度（等价于在遮罩上"挖洞"）。
        MaskTop.Width = width;
        MaskTop.Height = rect.Y;
        Canvas.SetLeft(MaskTop, 0);
        Canvas.SetTop(MaskTop, 0);

        MaskBottom.Width = width;
        MaskBottom.Height = Math.Max(0, height - rect.Y - rect.Height);
        Canvas.SetLeft(MaskBottom, 0);
        Canvas.SetTop(MaskBottom, rect.Y + rect.Height);

        MaskLeft.Width = rect.X;
        MaskLeft.Height = rect.Height;
        Canvas.SetLeft(MaskLeft, 0);
        Canvas.SetTop(MaskLeft, rect.Y);

        MaskRight.Width = Math.Max(0, width - rect.X - rect.Width);
        MaskRight.Height = rect.Height;
        Canvas.SetLeft(MaskRight, rect.X + rect.Width);
        Canvas.SetTop(MaskRight, rect.Y);

        foreach (var mask in new[] { MaskTop, MaskBottom, MaskLeft, MaskRight })
        {
            mask.Visibility = Visibility.Visible;
        }

        SelectionBorder.Width = rect.Width;
        SelectionBorder.Height = rect.Height;
        Canvas.SetLeft(SelectionBorder, rect.X);
        Canvas.SetTop(SelectionBorder, rect.Y);
        SelectionBorder.Visibility = Visibility.Visible;
    }

    private void ResetSelection()
    {
        _hasSelection = false;
        Toolbar.Visibility = Visibility.Collapsed;
        MaskFull.Visibility = Visibility.Visible;

        foreach (var mask in new[] { MaskTop, MaskBottom, MaskLeft, MaskRight })
        {
            mask.Visibility = Visibility.Collapsed;
        }

        SelectionBorder.Visibility = Visibility.Collapsed;
    }

    private void PlaceToolbar(Rect rect)
    {
        const double ToolbarWidth = 240;
        const double ToolbarHeight = 48;
        const double Gap = 8;

        var left = Math.Min(rect.X + rect.Width - ToolbarWidth, Root.ActualWidth - ToolbarWidth - Gap);
        left = Math.Max(Gap, left);

        var top = rect.Y + rect.Height + Gap;
        if (top + ToolbarHeight > Root.ActualHeight)
        {
            top = Math.Max(Gap, rect.Y - ToolbarHeight - Gap);
        }

        Canvas.SetLeft(Toolbar, left);
        Canvas.SetTop(Toolbar, top);
    }

    // ---------- 键盘 ----------

    private async void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Enter && _hasSelection && !_busy)
        {
            e.Handled = true;
            await CopyAsync();
        }
    }

    // ---------- 动作 ----------

    private async void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (!_busy)
        {
            await CopyAsync();
        }
    }

    private async Task CopyAsync()
    {
        var png = await BuildPngAsync();
        if (png is null)
        {
            return;
        }

        _busy = true;
        try
        {
            var ok = await ScreenshotService.CopyToClipboardAsync(png);
            AppState.ShowStatus(
                Loc.T(ok ? "Screenshot_Copied" : "Screenshot_CopyFailed"),
                ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);

            if (ok)
            {
                Close();
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_busy || !_hasSelection)
        {
            return;
        }

        _busy = true;
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                SuggestedFileName = $"SteamEYA_{DateTime.Now:yyyyMMdd_HHmmss}"
            };
            picker.FileTypeChoices.Add("PNG", new List<string> { ".png" });

            // 无打包 WinUI：保存对话框必须显式绑定窗口句柄。
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return;
            }

            var png = await BuildPngAsync();
            if (png is null)
            {
                return;
            }

            await File.WriteAllBytesAsync(file.Path, png);
            AppState.ShowStatus(Loc.T("Screenshot_Saved"), InfoBarSeverity.Success);
            Close();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"保存截图失败：{ex.Message}");
            AppState.ShowStatus(Loc.Tf("Screenshot_SaveFailed_Format", ex.Message), InfoBarSeverity.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>按当前选区裁出像素并编码成 PNG；选区无效时返回 null。</summary>
    private async Task<byte[]?> BuildPngAsync()
    {
        if (!_hasSelection)
        {
            return null;
        }

        var scale = _frame.Scale <= 0 ? 1.0 : _frame.Scale;
        var x = (int)Math.Round(_selection.X * scale);
        var y = (int)Math.Round(_selection.Y * scale);
        var width = (int)Math.Round(_selection.Width * scale);
        var height = (int)Math.Round(_selection.Height * scale);

        if (width < 1 || height < 1)
        {
            return null;
        }

        try
        {
            var cropped = ScreenshotService.Crop(_frame, x, y, width, height);
            return await ScreenshotService.EncodePngAsync(cropped, width, height);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"生成截图失败：{ex.Message}");
            AppState.ShowStatus(Loc.T("Screenshot_CopyFailed"), InfoBarSeverity.Error);
            return null;
        }
    }
}
