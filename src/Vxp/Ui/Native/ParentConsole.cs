using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Vxp.Ui.Native;

/// <summary>
/// Gives the Windows build, a GUI program, the console of whatever started it.
/// </summary>
/// <remarks>
/// <para>
/// vxp.exe is built for the Windows GUI subsystem so that a double click or a game launcher
/// opens the player and nothing else; a console program would get a console window of its
/// own first. The price is that Windows gives a GUI program no console at all, so the
/// command line would print nowhere. <see cref="Attach"/> puts that back by joining the
/// console of the parent process, when it has one, which is how mGBA does the same thing.
/// </para>
/// <para>
/// Handles the parent redirected (a pipe, or a file) are inherited as usual and kept, so
/// <c>vxp info disc.zip &gt; out.txt</c> and <c>| findstr</c> work; only the ones that
/// would otherwise lead nowhere are pointed at the console.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class ParentConsole
{
    private const int StdInput = -10;
    private const int StdOutput = -11;
    private const int StdError = -12;
    private const uint AttachParentProcess = unchecked((uint)-1);

    private const uint FileTypeDisk = 1;
    private const uint FileTypeChar = 2;
    private const uint FileTypePipe = 3;

    private const uint GenericRead = 0x8000_0000;
    private const uint GenericWrite = 0x4000_0000;
    private const uint FileShareReadWrite = 0x3;
    private const uint OpenExisting = 3;

    /// <summary>Whether this process is sharing its parent's console.</summary>
    public static bool Attached { get; private set; }

    /// <summary>Whether standard error goes anywhere someone might read it.</summary>
    public static bool ErrorsAreVisible { get; private set; }

    /// <summary>
    /// Joins the parent's console, if it has one, and points the standard handles that were
    /// not redirected at it. Must run before anything touches <see cref="Console"/>, which
    /// reads the standard handles once, on first use.
    /// </summary>
    public static void Attach()
    {
        var input = GetStdHandle(StdInput);
        var output = GetStdHandle(StdOutput);
        var error = GetStdHandle(StdError);

        // Started from Explorer or a GUI launcher there is no console to join, and nothing
        // to do: the redirected handles, if any, are already in place.
        Attached = AttachConsole(AttachParentProcess);

        // Asked after attaching, so that a console handle the parent passed down is
        // recognised as one rather than mistaken for a redirection.
        var outputRedirected = IsRedirected(output);
        var errorRedirected = IsRedirected(error);
        var inputRedirected = IsRedirected(input);

        ErrorsAreVisible = errorRedirected || Attached;
        if (!Attached) return;

        // Attaching can reset the standard handles, so every one is set explicitly: back to
        // the inherited handle where the parent redirected it, to the console otherwise.
        var consoleOut = outputRedirected && errorRedirected ? 0 : OpenConsole("CONOUT$", GenericRead | GenericWrite);
        SetStdHandle(StdOutput, outputRedirected ? output : consoleOut);
        SetStdHandle(StdError, errorRedirected ? error : consoleOut);
        SetStdHandle(StdInput, inputRedirected ? input : OpenConsole("CONIN$", GenericRead | GenericWrite));

        // The shell has not waited for a GUI program, so it has already printed its prompt,
        // and the first line written would run on from the end of it.
        if (consoleOut != 0)
        {
            var start = new LineStart(consoleOut);
            if (!outputRedirected) Console.SetOut(new PromptAwareWriter(Console.Out, start));
            if (!errorRedirected) Console.SetError(new PromptAwareWriter(Console.Error, start));
        }
    }

    /// <summary>
    /// Whether a standard handle was redirected by the parent: a file, a pipe, or a
    /// character device such as NUL that is not a console.
    /// </summary>
    private static bool IsRedirected(nint handle)
    {
        if (handle == 0 || handle == -1) return false;

        return GetFileType(handle) switch
        {
            FileTypeDisk or FileTypePipe => true,
            FileTypeChar => !GetConsoleMode(handle, out _),
            _ => false,
        };
    }

    private static nint OpenConsole(string name, uint access)
    {
        var handle = CreateFile(name, access, FileShareReadWrite, 0, OpenExisting, 0, 0);
        return handle == -1 ? 0 : handle;
    }

    /// <summary>
    /// Starts the first line written on a fresh line, if the console's cursor is not
    /// already at the start of one.
    /// </summary>
    private sealed class LineStart(nint console)
    {
        private bool _checked;

        public void Before(TextWriter writer)
        {
            if (_checked) return;
            _checked = true;

            // A shell that did wait (a batch file, or a pipeline) leaves the cursor in the
            // first column, and then there is nothing to do.
            if (GetConsoleScreenBufferInfo(console, out var info) && info.CursorX != 0)
                writer.Write(Environment.NewLine);
        }
    }

    /// <summary>A console writer that calls <see cref="LineStart"/> before its first write.</summary>
    private sealed class PromptAwareWriter(TextWriter inner, LineStart start) : TextWriter
    {
        public override Encoding Encoding => inner.Encoding;

        public override IFormatProvider FormatProvider => inner.FormatProvider;

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string NewLine
        {
            get => inner.NewLine;
            set => inner.NewLine = value;
        }

        public override void Write(char value)
        {
            start.Before(inner);
            inner.Write(value);
        }

        public override void Write(string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            start.Before(inner);
            inner.Write(value);
        }

        public override void Write(char[] buffer, int index, int count)
        {
            if (count == 0) return;
            start.Before(inner);
            inner.Write(buffer, index, count);
        }

        public override void WriteLine(string? value)
        {
            start.Before(inner);
            inner.WriteLine(value);
        }

        public override void Flush() => inner.Flush();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenBufferInfo
    {
        public short SizeX;
        public short SizeY;
        public short CursorX;
        public short CursorY;
        public ushort Attributes;
        public short Left;
        public short Top;
        public short Right;
        public short Bottom;
        public short MaxX;
        public short MaxY;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int which);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetStdHandle(int which, nint handle);

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(nint handle, out uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleScreenBufferInfo(nint console, out ScreenBufferInfo info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFileW", SetLastError = true)]
    private static extern nint CreateFile(
        string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);
}
