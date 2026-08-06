using System.Runtime.InteropServices;

namespace Cda.Revit.Addin.TimeTracking;

/// <summary>
/// Decides when the user has stepped away, and when they came back.
///
/// TWO SIGNALS, because either one alone gets it wrong:
///
///   1. <c>GetLastInputInfo</c> — mouse and keyboard, system-wide. Catches the coffee
///      break, the meeting, the machine left locked overnight.
///   2. Is Revit the foreground window — because signal 1 is SYSTEM-wide. An hour spent
///      writing an email with Revit open behind it is an hour of continuous input, and
///      without this test every minute of it is billed to the last active view.
///
/// The two collapse into one number: <see cref="_lastActiveUtc"/>, the last moment the user
/// was demonstrably working IN REVIT. Idle time is measured from there, so the answer is
/// the same whether they walked away or just switched applications.
///
/// This class holds no timer of its own. <see cref="Tick"/> is driven by Revit's Idling
/// event, which needs no dispatcher, cannot fire on the wrong thread, and — being the
/// event that means "Revit has nothing to do" — is firing reliably in precisely the
/// situation being detected.
/// </summary>
internal sealed class IdleMonitor(TimeTrackingSettings settings)
{
    private DateTime _lastActiveUtc = DateTime.UtcNow;
    private DateTime _lastPollUtc = DateTime.MinValue;

    private bool _idle;
    private DateTime _idleSinceUtc;

    /// <summary>Raised once when the threshold is crossed. Argument: the last active moment.</summary>
    public event Action<DateTime>? WentIdle;

    /// <summary>Raised once when input returns. Arguments: the start and end of the idle span.</summary>
    public event Action<DateTime, DateTime>? CameBack;

    /// <summary>Live idle duration, for the status line in the UI.</summary>
    public TimeSpan IdleFor => DateTime.UtcNow - _lastActiveUtc;

    public bool IsIdle => _idle;

    /// <summary>
    /// Treat the user as active right now — used when they interact with the add-in's own
    /// windows, which do not register as Revit being the foreground application.
    /// </summary>
    public void MarkActive() => _lastActiveUtc = DateTime.UtcNow;

    public void Tick()
    {
        var now = DateTime.UtcNow;

        // Idling fires many times a second while Revit waits. Throttling here is what keeps
        // the cost of this feature at two P/Invokes every ten seconds.
        if (now - _lastPollUtc < TimeSpan.FromSeconds(Math.Max(1, settings.PollSeconds))) return;
        _lastPollUtc = now;

        var revitHasFocus = IsRevitForeground();
        var countsAsRevitWork = revitHasFocus || !settings.TreatRevitNotForegroundAsIdle;

        if (countsAsRevitWork)
        {
            var lastInput = now - SystemIdleTime();
            if (lastInput > _lastActiveUtc) _lastActiveUtc = lastInput;
        }

        var idleFor = now - _lastActiveUtc;

        if (!_idle && idleFor >= settings.IdleThreshold)
        {
            _idle = true;
            _idleSinceUtc = _lastActiveUtc;
            WentIdle?.Invoke(_lastActiveUtc);
            return;
        }

        if (_idle && idleFor < settings.IdleThreshold)
        {
            _idle = false;
            CameBack?.Invoke(_idleSinceUtc, _lastActiveUtc);
        }
    }

    // ------------------------------------------------------------------ Win32

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo plii);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    /// <summary>
    /// Time since the last mouse or keyboard input anywhere in this Windows session.
    /// </summary>
    private static TimeSpan SystemIdleTime()
    {
        var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };

        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;

        // dwTime is a 32-bit tick count, so it wraps roughly every 49.7 days. UNSIGNED
        // subtraction is what makes the wrap harmless: a machine that has been up for 50
        // days would otherwise report the user as idle since before the Flood, pause the
        // timer permanently, and look exactly like the feature being broken.
        var nowTicks = unchecked((uint)Environment.TickCount);
        var elapsed = unchecked(nowTicks - info.dwTime);

        return TimeSpan.FromMilliseconds(elapsed);
    }

    private static bool IsRevitForeground()
    {
        try
        {
            var window = GetForegroundWindow();
            if (window == IntPtr.Zero) return false;

            GetWindowThreadProcessId(window, out var processId);
            return processId == (uint)Environment.ProcessId;
        }
        catch
        {
            // If the question cannot be answered, do not let it be the reason time stops
            // being recorded.
            return true;
        }
    }
}
