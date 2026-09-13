using System.Runtime.InteropServices;

namespace Vxp.Ui.Native;

/// <summary>
/// Watches the messages arriving at a window, without changing what happens to them.
/// </summary>
/// <remarks>
/// <para>
/// Menu messages are only useful if they are seen the moment they arrive. An open Windows
/// menu runs its own modal message loop, so anything queued for the application's own
/// loop is not read until the menu has closed again — by which time it is too late to
/// keep the picture and sound going, and too late to fill a popup in before it is drawn.
/// </para>
/// <para>
/// SDL offers <c>SDL_SetWindowsMessageHook</c> for exactly this, but it does not fire
/// with the SDL that ships here, so the window procedure is chained directly instead.
/// Every message is passed along untouched.
/// </para>
/// </remarks>
internal sealed class Win32WindowHook : IDisposable
{
    private readonly nint _window;
    private readonly Action<uint, nuint, nint> _observer;

    // Rooted for as long as the hook is installed: the field is the only thing keeping
    // the delegate behind the native function pointer alive.
    private readonly Win32.WindowProc _thunk;

    private nint _previous;
    private bool _disposed;

    /// <summary>Installs the hook. Returns with it in place, or does nothing if it fails.</summary>
    public Win32WindowHook(nint window, Action<uint, nuint, nint> observer)
    {
        _window = window;
        _observer = observer;
        _thunk = Forward;

        _previous = Win32.SetWindowProc(window, Marshal.GetFunctionPointerForDelegate(_thunk));
    }

    /// <summary>True if the window procedure was actually chained.</summary>
    public bool Installed => _previous != 0;

    private nint Forward(nint window, uint message, nuint wParam, nint lParam)
    {
        // An exception thrown out of a native callback takes the process with it, so
        // nothing is allowed to escape a menu handler.
        try
        {
            _observer(message, wParam, lParam);
        }
        catch (Exception)
        {
            // Swallowed on purpose: a menu that misbehaves must not stop playback.
        }

        return Win32.CallWindowProc(_previous, window, message, wParam, lParam);
    }

    /// <summary>Puts the original window procedure back.</summary>
    public void Dispose()
    {
        if (_disposed || _previous == 0) return;
        _disposed = true;

        Win32.SetWindowProc(_window, _previous);
        _previous = 0;
    }
}
