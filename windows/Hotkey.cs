using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace ShowTell;

/// <summary>
/// Pure state machine for "press KeyA+KeyB together, twice in a row".
/// It has no Win32 dependencies so it can be self-tested with a fake clock.
/// </summary>
public sealed class ChordDetector
{
    public int KeyA { get; set; } = 0x70;
    public int KeyB { get; set; } = 0x71;
    public int WindowMs { get; set; } = 500;
    /// <summary>How long a lone key press is held back while waiting for its partner.</summary>
    public int GraceMs { get; set; } = 120;
    public bool Swallow { get; set; } = true;

    public event Action? Toggled;
    /// <summary>Re-inject a key we held back: (virtual key, isKeyUp).</summary>
    public Action<int, bool>? Replay;
    /// <summary>Ask the host to call <see cref="OnGraceElapsed"/> after the given number of ms.</summary>
    public Action<int>? StartGraceTimer;

    bool _aDown, _bDown, _chordActive;
    int? _pending;
    long _lastChordAt = long.MinValue / 2;
    readonly HashSet<int> _passedDown = new();

    bool SwallowAllowed => Swallow && !IsModifier(KeyA) && !IsModifier(KeyB);

    static bool IsModifier(int vk) => vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or (>= 0xA0 and <= 0xA5);

    public void Reset()
    {
        _aDown = _bDown = _chordActive = false;
        _pending = null;
        _passedDown.Clear();
        _lastChordAt = long.MinValue / 2;
    }

    /// <summary>Feed one key event. Returns true when the event should be swallowed.</summary>
    public bool OnKey(int vk, bool up, long nowMs)
    {
        if (KeyA == KeyB || (vk != KeyA && vk != KeyB)) return false;
        bool isA = vk == KeyA;

        if (!up)
        {
            bool wasDown = isA ? _aDown : _bDown;
            if (isA) _aDown = true; else _bDown = true;

            if (wasDown) // auto-repeat
                return !_passedDown.Contains(vk) && SwallowAllowed && (_chordActive || _pending == vk);

            if (_aDown && _bDown)
            {
                _pending = null; // the held-back partner key is consumed by the chord, never replayed
                _chordActive = true;
                if (nowMs - _lastChordAt <= WindowMs)
                {
                    _lastChordAt = long.MinValue / 2;
                    Toggled?.Invoke();
                }
                else
                {
                    _lastChordAt = nowMs;
                }
                if (SwallowAllowed) return true;
                _passedDown.Add(vk);
                return false;
            }

            if (SwallowAllowed)
            {
                _pending = vk;
                StartGraceTimer?.Invoke(GraceMs);
                return true;
            }
            _passedDown.Add(vk);
            return false;
        }

        // key up
        if (isA) _aDown = false; else _bDown = false;
        if (!_aDown && !_bDown) _chordActive = false;

        if (_passedDown.Remove(vk)) return false; // the system saw the down, so it must see the up

        if (_pending == vk)
        {
            // Quick lone tap: no partner arrived, so deliver it untouched.
            _pending = null;
            Replay?.Invoke(vk, false);
            Replay?.Invoke(vk, true);
            return true;
        }
        return SwallowAllowed;
    }

    /// <summary>Partner key never came: release the held-back key to the system.</summary>
    public void OnGraceElapsed()
    {
        if (_pending is int vk)
        {
            _pending = null;
            _passedDown.Add(vk);
            Replay?.Invoke(vk, false);
        }
    }
}

/// <summary>Global low-level keyboard hook that drives a <see cref="ChordDetector"/>.</summary>
public sealed class KeyboardHook : IDisposable
{
    public ChordDetector Detector { get; } = new();
    /// <summary>When set, the next key press is handed to this callback (and swallowed) instead of the detector.</summary>
    public Action<int>? CaptureNextKey { get; set; }
    public event Action? Toggled;

    readonly Native.LowLevelKeyboardProc _proc; // keep delegate alive
    readonly DispatcherTimer _grace;
    readonly Dispatcher _dispatcher;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    IntPtr _hook;

    public KeyboardHook()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _proc = HookProc;
        _grace = new DispatcherTimer(DispatcherPriority.Send, _dispatcher);
        _grace.Tick += (_, _) => { _grace.Stop(); Detector.OnGraceElapsed(); };

        Detector.StartGraceTimer = ms => { _grace.Stop(); _grace.Interval = TimeSpan.FromMilliseconds(ms); _grace.Start(); };
        Detector.Replay = (vk, up) => Native.SendKeys(((ushort)vk, up));
        Detector.Toggled += () => _dispatcher.BeginInvoke(() => Toggled?.Invoke());

        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            throw new InvalidOperationException("Could not install keyboard hook (error " + Marshal.GetLastWin32Error() + ")");
    }

    public void Configure(Settings s)
    {
        Detector.KeyA = s.KeyA;
        Detector.KeyB = s.KeyB;
        Detector.WindowMs = s.DoubleTapWindowMs;
        Detector.Swallow = s.SwallowChordKeys;
        Detector.Reset();
    }

    IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            if (info.dwExtraInfo != Native.ShowTellInjected)
            {
                int msg = (int)wParam;
                bool down = msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
                bool up = msg is Native.WM_KEYUP or Native.WM_SYSKEYUP;
                try
                {
                    if (CaptureNextKey is { } cap)
                    {
                        if (down)
                        {
                            CaptureNextKey = null;
                            int vk = (int)info.vkCode;
                            _dispatcher.BeginInvoke(() => cap(vk));
                        }
                        return (IntPtr)1;
                    }
                    if ((down || up) && Detector.OnKey((int)info.vkCode, up, _clock.ElapsedMilliseconds))
                        return (IntPtr)1;
                }
                catch (Exception ex)
                {
                    Log.Write("Hook error: " + ex);
                }
            }
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    public static string KeyName(int vk)
    {
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x6F);
        if (vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A) return ((char)vk).ToString();
        return vk switch
        {
            0x10 => "Shift", 0x11 => "Ctrl", 0x12 => "Alt",
            0xA0 => "Left Shift", 0xA1 => "Right Shift", 0xA2 => "Left Ctrl", 0xA3 => "Right Ctrl",
            0xA4 => "Left Alt", 0xA5 => "Right Alt", 0x5B => "Left Win", 0x5C => "Right Win",
            0x14 => "Caps Lock", 0x20 => "Space", 0x1B => "Esc", 0x09 => "Tab", 0x0D => "Enter",
            0x2D => "Insert", 0x2E => "Delete", 0x24 => "Home", 0x23 => "End", 0x21 => "Page Up", 0x22 => "Page Down",
            0x25 => "Left", 0x26 => "Up", 0x27 => "Right", 0x28 => "Down",
            0x91 => "Scroll Lock", 0x13 => "Pause", 0x2C => "Print Screen", 0x5D => "Menu",
            0xC0 => "`", 0xBD => "-", 0xBB => "=", 0xDB => "[", 0xDD => "]", 0xDC => "\\", 0xBA => ";", 0xDE => "'",
            0xBC => ",", 0xBE => ".", 0xBF => "/",
            _ when vk is >= 0x60 and <= 0x69 => "Num " + (vk - 0x60),
            _ => $"Key 0x{vk:X2}",
        };
    }
}
