namespace Vxp;

/// <summary>
/// SDL could not give the player something it cannot run without: video, a window, a
/// renderer or an audio device. Reported as a one-line error rather than a stack trace,
/// because the fix is on the machine (no display, no sound device) and not in vxp.
/// </summary>
public sealed class SdlException(string message) : InvalidOperationException(message);
