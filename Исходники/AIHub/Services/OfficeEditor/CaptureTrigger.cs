namespace AIHub.Services.OfficeEditor;

/// <summary>One physical left-Shift press, without another key or autorepeat.</summary>
internal sealed class CaptureTrigger
{
    private bool _down, _alone;
    internal bool Accept(uint key, bool down, bool injected, bool modifiers)
    {
        if (injected) return false;
        if (key != 0xA0) { if (down) _alone = false; return false; }
        if (down)
        {
            if (!_down) { _down = true; _alone = !modifiers; }
            return false;
        }
        var capture = _down && _alone && !modifiers;
        _down = _alone = false; return capture;
    }
}
