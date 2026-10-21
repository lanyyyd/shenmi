using System.Runtime.InteropServices;

namespace SteamEyaWinUI.Services.RemoteAssist;

/// <summary>
/// 键鼠注入：SendInput + 扫描码。
///
/// 用扫描码而不是虚拟键码，是因为对端键盘布局/输入法与主控端不一致时，VK 会打错字符。
/// 绝对坐标按"被控端采集区域"归一化到 0..65535，并启用 VIRTUALDESK 以支持多显示器。
/// </summary>
internal static partial class InputInjector
{
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;

    private const uint MouseEventMove = 0x0001;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventRightDown = 0x0008;
    private const uint MouseEventRightUp = 0x0010;
    private const uint MouseEventMiddleDown = 0x0020;
    private const uint MouseEventMiddleUp = 0x0040;
    private const uint MouseEventWheel = 0x0800;
    private const uint MouseEventAbsolute = 0x8000;
    private const uint MouseEventVirtualDesk = 0x4000;

    private const uint KeyEventExtendedKey = 0x0001;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventScanCode = 0x0008;

    private static int _lastX = -1;
    private static int _lastY = -1;

    /// <summary>把归一化坐标（0..1）注入到指定屏幕矩形内。</summary>
    public static void MoveMouse(int screenX, int screenY, int screenWidth, int screenHeight, float nx, float ny)
    {
        if (screenWidth <= 0 || screenHeight <= 0)
        {
            return;
        }

        // SendInput 的绝对坐标是 0..65535 映射到整个虚拟桌面，因此要把目标点换算成虚拟桌面比例。
        var virtualX = GetSystemMetrics(SystemMetricXVirtualScreen);
        var virtualY = GetSystemMetrics(SystemMetricYVirtualScreen);
        var virtualW = GetSystemMetrics(SystemMetricCxVirtualScreen);
        var virtualH = GetSystemMetrics(SystemMetricCyVirtualScreen);
        if (virtualW <= 0 || virtualH <= 0)
        {
            return;
        }

        var px = screenX + (int)(Math.Clamp(nx, 0f, 1f) * screenWidth);
        var py = screenY + (int)(Math.Clamp(ny, 0f, 1f) * screenHeight);
        px = Math.Clamp(px, virtualX, virtualX + virtualW - 1);
        py = Math.Clamp(py, virtualY, virtualY + virtualH - 1);

        var ax = (int)((px - virtualX) * 65535.0 / Math.Max(virtualW - 1, 1));
        var ay = (int)((py - virtualY) * 65535.0 / Math.Max(virtualH - 1, 1));

        if (ax == _lastX && ay == _lastY)
        {
            return;   // 同一像素重复移动没有意义，直接丢掉（高频事件能省一半带宽）
        }
        _lastX = ax;
        _lastY = ay;

        var input = new Input
        {
            Type = InputMouse,
            Data = new InputUnion
            {
                Mouse = new MouseInput
                {
                    Dx = ax,
                    Dy = ay,
                    Flags = MouseEventMove | MouseEventAbsolute | MouseEventVirtualDesk,
                },
            },
        };
        SendInput(1, [input], Marshal.SizeOf<Input>());
    }

    public static void MouseButton(int button, bool down)
    {
        uint flag = button switch
        {
            1 => down ? MouseEventRightDown : MouseEventRightUp,
            2 => down ? MouseEventMiddleDown : MouseEventMiddleUp,
            _ => down ? MouseEventLeftDown : MouseEventLeftUp,
        };

        var input = new Input
        {
            Type = InputMouse,
            Data = new InputUnion { Mouse = new MouseInput { Flags = flag } },
        };
        SendInput(1, [input], Marshal.SizeOf<Input>());
    }

    public static void MouseWheel(int delta)
    {
        var input = new Input
        {
            Type = InputMouse,
            Data = new InputUnion
            {
                Mouse = new MouseInput { MouseData = unchecked((uint)delta), Flags = MouseEventWheel },
            },
        };
        SendInput(1, [input], Marshal.SizeOf<Input>());
    }

    public static void Key(ushort scanCode, bool down, bool extended)
    {
        var flags = KeyEventScanCode;
        if (!down)
        {
            flags |= KeyEventKeyUp;
        }
        if (extended)
        {
            flags |= KeyEventExtendedKey;
        }

        var input = new Input
        {
            Type = InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInput { ScanCode = scanCode, Flags = flags },
            },
        };
        SendInput(1, [input], Marshal.SizeOf<Input>());
    }

    private const int SystemMetricXVirtualScreen = 76;
    private const int SystemMetricYVirtualScreen = 77;
    private const int SystemMetricCxVirtualScreen = 78;
    private const int SystemMetricCyVirtualScreen = 79;

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint count, Input[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }
}
