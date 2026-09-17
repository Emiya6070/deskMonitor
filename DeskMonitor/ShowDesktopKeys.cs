namespace DeskMonitor;

// Recognize one Win+D press, including when the widget has no keyboard focus.
// Only modifier state and D's repeat state are retained.
internal sealed class ShowDesktopKeys
{
    private int _modifiers;
    private bool _dDown;

    public bool Update(ushort key, bool released, bool extended = false, ushort scanCode = 0)
    {
        var bit = key switch
        {
            0x5B => 1, 0x5C => 2,
            0x11 => extended ? 8 : 4, 0xA2 => 4, 0xA3 => 8,
            0x12 => extended ? 32 : 16, 0xA4 => 16, 0xA5 => 32,
            0x10 => scanCode == 0x36 ? 128 : 64, 0xA0 => 64, 0xA1 => 128,
            _ => 0
        };
        if (bit != 0) _modifiers = released ? _modifiers & ~bit : _modifiers | bit;
        if (key != 0x44) return false;
        var repeat = _dDown;
        _dDown = !released;
        return !released && !repeat && (_modifiers & 3) != 0 && (_modifiers & ~3) == 0;
    }

    public void Reset() { _modifiers = 0; _dDown = false; }
}
