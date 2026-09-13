using System.Runtime.InteropServices;

namespace Vxp.Ui.Native;

/// <summary>
/// The slice of the Win32 menu and window API that <see cref="Win32MenuBar"/> needs.
/// </summary>
/// <remarks>
/// Everything here is guarded by <see cref="OperatingSystem.IsWindows"/> at the call site;
/// the project targets plain <c>net8.0</c> so that the rest of the emulator still builds
/// and runs on macOS and Linux, where the in-window menu remains the only one.
/// </remarks>
internal static class Win32
{
    // Menu item flags for AppendMenu.
    internal const uint MfString = 0x0000;
    internal const uint MfGrayed = 0x0001;
    internal const uint MfPopup = 0x0010;
    internal const uint MfSeparator = 0x0800;

    // MENUITEMINFO masks.
    internal const uint MiimState = 0x0001;
    internal const uint MiimString = 0x0040;
    internal const uint MiimFType = 0x0100;

    // MENUITEMINFO types and states.
    internal const uint MftString = 0x0000;
    internal const uint MftRadioCheck = 0x0200;
    internal const uint MftSeparator = 0x0800;
    internal const uint MfsGrayed = 0x0003;
    internal const uint MfsChecked = 0x0008;
    internal const uint MfsUnchecked = 0x0000;
    internal const uint MfsEnabled = 0x0000;

    // Window messages we care about.
    internal const uint WmCommand = 0x0111;
    internal const uint WmSysCommand = 0x0112;
    internal const uint WmTimer = 0x0113;
    internal const uint WmInitMenuPopup = 0x0117;
    internal const uint WmSysKeyDown = 0x0104;
    internal const uint WmSysKeyUp = 0x0105;
    internal const uint WmEnterMenuLoop = 0x0211;
    internal const uint WmExitMenuLoop = 0x0212;

    /// <summary>WM_SYSCOMMAND request that activates the menu bar from the keyboard.</summary>
    internal const nuint ScKeyMenu = 0xF100;

    /// <summary>Virtual key code for Alt.</summary>
    internal const nuint VkMenu = 0x12;

    /// <summary>Virtual key code for F10, the other way into a menu bar.</summary>
    internal const nuint VkF10 = 0x79;

    /// <summary>Bit 30 of a key message's lParam: the key was already down.</summary>
    internal const nint KeyWasDown = 1 << 30;

    // SetWindowPos flags: move/resize only, no z-order or activation change.
    internal const uint SwpNoZOrder = 0x0004;
    internal const uint SwpNoActivate = 0x0010;
    internal const uint SwpNoMove = 0x0002;

    // MessageBox styles.
    internal const uint MbOk = 0x0000;
    internal const uint MbIconInformation = 0x0040;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;

        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MenuItemInfo
    {
        public uint Size;
        public uint Mask;
        public uint Type;
        public uint State;
        public uint Id;
        public nint SubMenu;
        public nint CheckedBitmap;
        public nint UncheckedBitmap;
        public nuint ItemData;
        public nint TypeData;
        public uint Length;
        public nint ItemBitmap;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint CreateMenu();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint CreatePopupMenu();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "AppendMenuW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AppendMenu(nint menu, uint flags, nuint idOrSubmenu, string? item);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetMenu(nint window, nint menu);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint GetMenu(nint window);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DrawMenuBar(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SetMenuItemInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetMenuItemInfo(nint menu, uint item, [MarshalAs(UnmanagedType.Bool)] bool byPosition, ref MenuItemInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetClientRect(nint window, out Rect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint window, out Rect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nuint SetTimer(nint window, nuint id, uint milliseconds, nint callback);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool KillTimer(nint window, nuint id);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    internal static extern int MessageBox(nint window, string text, string caption, uint type);

    /// <summary>Index of a window's procedure in its extra data.</summary>
    private const int GwlpWndProc = -4;

    /// <summary>Signature of a window procedure.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate nint WindowProc(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(nint window, int index, int value);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    internal static extern nint CallWindowProc(nint previous, nint window, uint message, nuint wParam, nint lParam);

    /// <summary>
    /// Points a window at a new procedure and hands back the one it was using.
    /// </summary>
    /// <remarks>
    /// 32-bit Windows has no <c>SetWindowLongPtrW</c>, so the pointer-sized call is only
    /// made where pointers are that size.
    /// </remarks>
    internal static nint SetWindowProc(nint window, nint procedure)
        => nint.Size == 8
            ? SetWindowLongPtr(window, GwlpWndProc, procedure)
            : SetWindowLong(window, GwlpWndProc, (int)procedure);
}
