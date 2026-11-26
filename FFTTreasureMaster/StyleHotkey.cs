using System;
using System.Runtime.InteropServices;

namespace FFTTreasureMaster;

/// <summary>
/// The top row number key that steps the tile look. Saved by name, so the names never change.
/// </summary>
public enum StyleCycleKey { Off, Key4, Key5, Key6, Key7, Key8, Key9 }

internal interface IKeySource
{
    bool IsDown(int virtualKey);
    bool GameHasFocus();
}

internal sealed class LiveKeySource : IKeySource
{
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    public bool IsDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    public bool GameHasFocus()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
        return pid == (uint)Environment.ProcessId;
    }
}

internal sealed class StyleHotkey
{
    private static readonly int[] Modifiers = { 0x10, 0x11, 0x12, 0x5B, 0x5C };

    private readonly StyleCycleKey _key;
    private readonly IKeySource _keys;
    private bool _wasDown;

    internal StyleHotkey(StyleCycleKey key, IKeySource keys)
    {
        _key = key;
        _keys = keys;
    }

    // Every poll remembers the raw state, so a press begun behind a closed gate never fires later.
    internal bool Poll(bool allowed)
    {
        if (_key == StyleCycleKey.Off) return false;
        bool down = _keys.IsDown(VirtualKey(_key));
        bool pressed = down && !_wasDown;
        _wasDown = down;
        if (!pressed || !allowed || !_keys.GameHasFocus()) return false;
        foreach (int modifier in Modifiers)
            if (_keys.IsDown(modifier)) return false;
        return true;
    }

    internal static int VirtualKey(StyleCycleKey key) => key == StyleCycleKey.Off ? 0 : 0x30 + (int)key + 3;

    internal static string Digit(StyleCycleKey key) => key == StyleCycleKey.Off ? "" : ((int)key + 3).ToString();
}
