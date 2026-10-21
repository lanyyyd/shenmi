using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Models;
using SteamEyaWinUI.Services;
using SteamEyaWinUI.Services.RemoteAssist;
using Windows.System;

namespace SteamEyaWinUI.Views;

/// <summary>
/// 主控端的远程画面窗口：一帧一帧把解出来的 BGRA 塞进 WriteableBitmap，并把鼠标/键盘
/// 折算成输入事件发回被控端。
///
/// 不用 SwapChainPanel 的原因：那要 ISwapChainPanelNative 的 COM 互操作，在 WinUI 3 + AOT 下
/// 既要自己声明接口又要管 swapchain 生命周期，收益只有几毫秒；WriteableBitmap 一条内存拷贝就能跑满 30fps。
/// </summary>
public sealed partial class RemoteAssistWindow : Window
{
    private readonly AssistSession _session;
    private WriteableBitmap? _bitmap;
    private int _frameWidth;
    private int _frameHeight;
    private bool _renderPending;
    private bool _disposed;
    private bool _fullScreen;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _noVideoTimer;

    internal RemoteAssistWindow(AssistSession session)
    {
        InitializeComponent();
        _session = session;

        Title = Loc.T("Assist_Window_Title");
        CadBtn.Content = Loc.T("Assist_SendCad");
        FullScreenBtn.Content = Loc.T("Assist_FullScreen");
        DisconnectBtn.Content = Loc.T("Assist_Disconnect");
        StatusText.Text = Loc.T("Assist_Connecting");

        _session.FrameReady += OnFrameReady;
        _session.CursorMoved += OnCursorMoved;
        _session.Closed += OnSessionClosed;

        Closed += (_, _) => TearDown();
        Activated += (_, _) => RootHost.Focus(FocusState.Programmatic);

        var presenter = AppWindow.Presenter as OverlappedPresenter;
        presenter?.IsResizable = true;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 760));

        RootHost.Focus(FocusState.Programmatic);
    }

    private void OnFrameReady(byte[] bgra, int width, int height)
    {
        if (_disposed)
        {
            return;
        }

        // 解码线程可能比 UI 快：上一帧还没画完就直接丢，宁降帧率不涨延迟。
        if (_renderPending)
        {
            return;
        }
        _renderPending = true;

        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (_disposed)
                {
                    return;
                }

                if (_bitmap is null || _frameWidth != width || _frameHeight != height)
                {
                    _frameWidth = width;
                    _frameHeight = height;
                    _bitmap = new WriteableBitmap(width, height);
                    ScreenImage.Source = _bitmap;
                    _noVideoTimer.Stop();
                    UpdateStatus();
                }

                using var stream = _bitmap!.PixelBuffer.AsStream();
                stream.Write(bgra, 0, Math.Min(bgra.Length, width * height * 4));
                _bitmap.Invalidate();
            }
            catch (Exception ex)
            {
                AppLog.Warn($"渲染远程画面失败：{ex.Message}");
            }
            finally
            {
                _renderPending = false;
            }
        });
    }

    private void OnCursorMoved(bool visible, float x, float y)
    {
        if (_disposed)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed || _bitmap is null)
            {
                return;
            }

            CursorShape.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (!visible)
            {
                return;
            }

            // 画面是 Uniform 缩放，光标要按实际绘制区域换算，否则会偏。
            var (drawnWidth, drawnHeight, offsetX, offsetY) = GetDrawnRect();
            Canvas.SetLeft(CursorShape, offsetX + x * drawnWidth);
            Canvas.SetTop(CursorShape, offsetY + y * drawnHeight);
        });
    }

    private (double Width, double Height, double OffsetX, double OffsetY) GetDrawnRect()
    {
        var availableWidth = ScreenImage.ActualWidth;
        var availableHeight = ScreenImage.ActualHeight;
        if (_frameWidth <= 0 || _frameHeight <= 0 || availableWidth <= 0 || availableHeight <= 0)
        {
            return (availableWidth, availableHeight, 0, 0);
        }

        var scale = Math.Min(availableWidth / _frameWidth, availableHeight / _frameHeight);
        var width = _frameWidth * scale;
        var height = _frameHeight * scale;
        return (width, height, (availableWidth - width) / 2, (availableHeight - height) / 2);
    }

    private void UpdateStatus()
    {
        var codec = _session.HardwareDecode ? Loc.T("Assist_CodecHardware") : Loc.T("Assist_CodecSoftware");
        StatusText.Text = Loc.Tf("Assist_Status_Format", _frameWidth, _frameHeight, codec);
    }

    // ---------------- 鼠标 ----------------
    private (float X, float Y, bool Inside) ToNormalized(Windows.Foundation.Point point)
    {
        var (width, height, offsetX, offsetY) = GetDrawnRect();
        if (width <= 0 || height <= 0)
        {
            return (0, 0, false);
        }

        var x = (point.X - offsetX) / width;
        var y = (point.Y - offsetY) / height;
        var inside = x >= 0 && x <= 1 && y >= 0 && y <= 1;
        return ((float)Math.Clamp(x, 0, 1), (float)Math.Clamp(y, 0, 1), inside);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(ScreenImage);
        var (x, y, inside) = ToNormalized(point.Position);
        if (!inside)
        {
            return;
        }

        _ = _session.SendInputAsync(new AssistInputEvent
        {
            Kind = AssistInputEvent.KindMouseMove,
            X = x,
            Y = y,
        });
        e.Handled = true;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        RootGrid.CapturePointer(e.Pointer);
        var point = e.GetCurrentPoint(ScreenImage);
        var (x, y, inside) = ToNormalized(point.Position);
        if (inside)
        {
            _ = _session.SendInputAsync(new AssistInputEvent
            {
                Kind = AssistInputEvent.KindMouseMove,
                X = x,
                Y = y,
            });
        }

        var button = point.Properties.IsRightButtonPressed ? (byte)1
            : point.Properties.IsMiddleButtonPressed ? (byte)2
            : (byte)0;

        _ = _session.SendInputAsync(new AssistInputEvent
        {
            Kind = AssistInputEvent.KindMouseButton,
            Button = button,
            Down = 1,
        });
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        RootGrid.ReleasePointerCapture(e.Pointer);
        var point = e.GetCurrentPoint(ScreenImage);
        var button = point.Properties.IsRightButtonPressed ? (byte)1
            : point.Properties.IsMiddleButtonPressed ? (byte)2
            : (byte)0;

        _ = _session.SendInputAsync(new AssistInputEvent
        {
            Kind = AssistInputEvent.KindMouseButton,
            Button = button,
            Down = 0,
        });
        e.Handled = true;
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(ScreenImage).Properties.MouseWheelDelta;
        _ = _session.SendInputAsync(new AssistInputEvent
        {
            Kind = AssistInputEvent.KindMouseWheel,
            Delta = delta,
        });
        e.Handled = true;
    }

    // ---------------- 键盘 ----------------
    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            if (_fullScreen)
            {
                ToggleFullScreen();
                e.Handled = true;
            }
            return;
        }

        SendKey(e.Key, down: true);
        e.Handled = true;
    }

    private void OnKeyUp(object sender, KeyRoutedEventArgs e)
    {
        SendKey(e.Key, down: false);
        e.Handled = true;
    }

    private void SendKey(VirtualKey key, bool down)
    {
        var vk = (uint)key;
        var scan = MapVirtualKey(vk, 0);
        if (scan == 0)
        {
            return;
        }

        // 方向键 / 编辑键区在对端必须带"扩展键"标志，否则会被当成小键盘。
        // 右 Ctrl(0xA3) / 右 Alt(0xA5) 直接用数值判断，避免不同 SDK 版本里枚举成员名不一致。
        var extended = key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down
            or VirtualKey.Home or VirtualKey.End or VirtualKey.PageUp or VirtualKey.PageDown
            or VirtualKey.Insert or VirtualKey.Delete
            || (uint)key is 0xA3 or 0xA5;

        _ = _session.SendInputAsync(new AssistInputEvent
        {
            Kind = AssistInputEvent.KindKey,
            Button = (byte)(scan & 0xFF),
            Down = down ? (byte)1 : (byte)0,
            Flags = (byte)(extended ? 1 : 0),
        });
    }

    // ---------------- 工具条 ----------------
    private void OnSendCadClick(object sender, RoutedEventArgs e)
    {
        // Ctrl+Alt+Del 走的是安全注意序列，SendInput 注入无效，只能提示用户手动按。
        StatusText.Text = Loc.T("Assist_CadHint");
    }

    private void OnFullScreenClick(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void ToggleFullScreen()
    {
        _fullScreen = !_fullScreen;
        AppWindow.SetPresenter(_fullScreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
        Toolbar.Visibility = _fullScreen ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnDisconnectClick(object sender, RoutedEventArgs e)
    {
        await DisconnectAsync();
    }

    private async Task DisconnectAsync()
    {
        try
        {
            _session.Close("controller_disconnect");
        }
        catch
        {
            // 忽略
        }
        await Task.Yield();
        Close();
    }

    private void OnSessionClosed(string reason)
    {
        if (_disposed)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed)
            {
                return;
            }
            StatusText.Text = reason switch
            {
                "timeout" => Loc.T("Assist_Ended_Timeout"),
                "peer_bye" or "host_user_disconnect" => Loc.T("Assist_Ended_ByPeer"),
                _ => Loc.T("Assist_Ended"),
            };
            Close();
        });
    }

    private void TearDown()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _noVideoTimer.Stop();

        _session.FrameReady -= OnFrameReady;
        _session.CursorMoved -= OnCursorMoved;
        _session.Closed -= OnSessionClosed;
        _session.Dispose();

        if (ReferenceEquals(AssistService.ControllerSession, _session))
        {
            AssistService.ControllerSession = null;
        }
    }

    [LibraryImport("user32.dll")]
    private static partial uint MapVirtualKey(uint code, uint mapType);
}
