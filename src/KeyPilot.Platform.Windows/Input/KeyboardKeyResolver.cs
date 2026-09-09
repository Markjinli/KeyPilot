namespace KeyPilot.Platform.Windows.Input;

/// <summary>Maps Set-1 keyboard scan codes to the stable IDs used by the HTML prototype.</summary>
public static class KeyboardKeyResolver
{
    public readonly record struct Set1KeyCode(ushort MakeCode, ushort Prefix);

    private static readonly IReadOnlyDictionary<ushort, string> BaseCodes =
        new Dictionary<ushort, string>
        {
            [0x01] = "Escape",
            [0x02] = "Digit1", [0x03] = "Digit2", [0x04] = "Digit3",
            [0x05] = "Digit4", [0x06] = "Digit5", [0x07] = "Digit6",
            [0x08] = "Digit7", [0x09] = "Digit8", [0x0A] = "Digit9",
            [0x0B] = "Digit0", [0x0C] = "Minus", [0x0D] = "Equal",
            [0x0E] = "Backspace", [0x0F] = "Tab",
            [0x10] = "KeyQ", [0x11] = "KeyW", [0x12] = "KeyE",
            [0x13] = "KeyR", [0x14] = "KeyT", [0x15] = "KeyY",
            [0x16] = "KeyU", [0x17] = "KeyI", [0x18] = "KeyO",
            [0x19] = "KeyP", [0x1A] = "BracketLeft", [0x1B] = "BracketRight",
            [0x1C] = "Enter", [0x1D] = "ControlLeft",
            [0x1E] = "KeyA", [0x1F] = "KeyS", [0x20] = "KeyD",
            [0x21] = "KeyF", [0x22] = "KeyG", [0x23] = "KeyH",
            [0x24] = "KeyJ", [0x25] = "KeyK", [0x26] = "KeyL",
            [0x27] = "Semicolon", [0x28] = "Quote", [0x29] = "Backquote",
            [0x2A] = "ShiftLeft", [0x2B] = "Backslash",
            [0x2C] = "KeyZ", [0x2D] = "KeyX", [0x2E] = "KeyC",
            [0x2F] = "KeyV", [0x30] = "KeyB", [0x31] = "KeyN",
            [0x32] = "KeyM", [0x33] = "Comma", [0x34] = "Period",
            [0x35] = "Slash", [0x36] = "ShiftRight", [0x37] = "NumpadMultiply",
            [0x38] = "AltLeft", [0x39] = "Space", [0x3A] = "CapsLock",
            [0x3B] = "F1", [0x3C] = "F2", [0x3D] = "F3", [0x3E] = "F4",
            [0x3F] = "F5", [0x40] = "F6", [0x41] = "F7", [0x42] = "F8",
            [0x43] = "F9", [0x44] = "F10", [0x45] = "NumLock",
            [0x46] = "ScrollLock", [0x47] = "Numpad7", [0x48] = "Numpad8",
            [0x49] = "Numpad9", [0x4A] = "NumpadSubtract", [0x4B] = "Numpad4",
            [0x4C] = "Numpad5", [0x4D] = "Numpad6", [0x4E] = "NumpadAdd",
            [0x4F] = "Numpad1", [0x50] = "Numpad2", [0x51] = "Numpad3",
            [0x52] = "Numpad0", [0x53] = "NumpadDecimal",
            [0x57] = "F11", [0x58] = "F12"
        };

    private static readonly IReadOnlyDictionary<ushort, string> ExtendedCodes =
        new Dictionary<ushort, string>
        {
            [0x1C] = "NumpadEnter", [0x1D] = "ControlRight",
            [0x35] = "NumpadDivide", [0x37] = "PrintScreen", [0x38] = "AltRight",
            [0x47] = "Home", [0x48] = "ArrowUp", [0x49] = "PageUp",
            [0x4B] = "ArrowLeft", [0x4D] = "ArrowRight",
            [0x4F] = "End", [0x50] = "ArrowDown", [0x51] = "PageDown",
            [0x52] = "Insert", [0x53] = "Delete",
            [0x5B] = "MetaLeft", [0x5C] = "MetaRight", [0x5D] = "ContextMenu"
        };

    private static readonly IReadOnlyDictionary<string, Set1KeyCode> StableCodes = BuildStableCodes();

    public static string? Resolve(RawKeyboardEvent input)
    {
        if (input.IsExtendedE0)
        {
            return ExtendedCodes.GetValueOrDefault(input.MakeCode);
        }

        if (input.IsExtendedE1)
        {
            return input.VirtualKey == 0x13 ? "Pause" : null;
        }

        return BaseCodes.GetValueOrDefault(input.MakeCode);
    }

    /// <summary>Resolves a prototype key ID back to its device-independent Set-1 code.</summary>
    public static bool TryGetSet1Code(string stableId, out Set1KeyCode code)
    {
        ArgumentNullException.ThrowIfNull(stableId);
        return StableCodes.TryGetValue(stableId, out code);
    }

    /// <summary>Resolves a persisted Set-1 code back to the prototype key ID without a device.</summary>
    public static bool TryResolveSet1Code(ushort makeCode, ushort prefix, out string stableId)
    {
        string? resolved = prefix switch
        {
            0 => BaseCodes.GetValueOrDefault(makeCode),
            0x0002 => ExtendedCodes.GetValueOrDefault(makeCode),
            0x0004 when makeCode == 0x45 => "Pause",
            _ => null
        };

        stableId = resolved ?? string.Empty;
        return resolved is not null;
    }

    private static IReadOnlyDictionary<string, Set1KeyCode> BuildStableCodes()
    {
        var result = new Dictionary<string, Set1KeyCode>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in BaseCodes)
        {
            result.Add(pair.Value, new Set1KeyCode(pair.Key, Prefix: 0));
        }

        foreach (var pair in ExtendedCodes)
        {
            result.Add(pair.Value, new Set1KeyCode(pair.Key, Prefix: 0x0002));
        }

        result.Add("Pause", new Set1KeyCode(MakeCode: 0x45, Prefix: 0x0004));
        return result;
    }
}
