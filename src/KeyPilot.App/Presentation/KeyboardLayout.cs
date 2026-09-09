namespace KeyPilot.App.Presentation;

internal sealed record KeyDefinition(string Label, string Id, double Units = 1);

internal static class KeyboardLayout
{
    public static IReadOnlyList<KeyDefinition?> FunctionRow { get; } =
    [
        new("Esc", "Escape"), null,
        new("F1", "F1"), new("F2", "F2"), new("F3", "F3"), new("F4", "F4"), null,
        new("F5", "F5"), new("F6", "F6"), new("F7", "F7"), new("F8", "F8"), null,
        new("F9", "F9"), new("F10", "F10"), new("F11", "F11"), new("F12", "F12"), null,
        new("PrtSc", "PrintScreen"), new("ScrLk", "ScrollLock"), new("Pause", "Pause")
    ];

    public static IReadOnlyList<IReadOnlyList<KeyDefinition>> MainRows { get; } =
    [
        [new("`", "Backquote"), new("1", "Digit1"), new("2", "Digit2"), new("3", "Digit3"), new("4", "Digit4"), new("5", "Digit5"), new("6", "Digit6"), new("7", "Digit7"), new("8", "Digit8"), new("9", "Digit9"), new("0", "Digit0"), new("-", "Minus"), new("=", "Equal"), new("Backspace", "Backspace", 2)],
        [new("Tab", "Tab", 1.5), new("Q", "KeyQ"), new("W", "KeyW"), new("E", "KeyE"), new("R", "KeyR"), new("T", "KeyT"), new("Y", "KeyY"), new("U", "KeyU"), new("I", "KeyI"), new("O", "KeyO"), new("P", "KeyP"), new("[", "BracketLeft"), new("]", "BracketRight"), new("\\", "Backslash", 1.5)],
        [new("Caps", "CapsLock", 1.8), new("A", "KeyA"), new("S", "KeyS"), new("D", "KeyD"), new("F", "KeyF"), new("G", "KeyG"), new("H", "KeyH"), new("J", "KeyJ"), new("K", "KeyK"), new("L", "KeyL"), new(";", "Semicolon"), new("'", "Quote"), new("Enter", "Enter", 2.2)],
        [new("Shift", "ShiftLeft", 2.25), new("Z", "KeyZ"), new("X", "KeyX"), new("C", "KeyC"), new("V", "KeyV"), new("B", "KeyB"), new("N", "KeyN"), new("M", "KeyM"), new(",", "Comma"), new(".", "Period"), new("/", "Slash"), new("Shift", "ShiftRight", 2.75)],
        [new("Ctrl", "ControlLeft", 1.35), new("Win", "MetaLeft", 1.25), new("Alt", "AltLeft", 1.25), new("Space", "Space", 6.15), new("Alt", "AltRight", 1.25), new("Win", "MetaRight", 1.25), new("Menu", "ContextMenu", 1.25), new("Ctrl", "ControlRight", 1.35)]
    ];

    public static IReadOnlyList<KeyDefinition> Navigation { get; } =
    [
        new("Ins", "Insert"), new("Home", "Home"), new("PgUp", "PageUp"),
        new("Del", "Delete"), new("End", "End"), new("PgDn", "PageDown"),
        new("↑", "ArrowUp"), new("←", "ArrowLeft"), new("↓", "ArrowDown"), new("→", "ArrowRight")
    ];

    public static IReadOnlyList<KeyDefinition> Numpad { get; } =
    [
        new("Num", "NumLock"), new("/", "NumpadDivide"), new("*", "NumpadMultiply"), new("-", "NumpadSubtract"),
        new("7", "Numpad7"), new("8", "Numpad8"), new("9", "Numpad9"), new("+", "NumpadAdd"),
        new("4", "Numpad4"), new("5", "Numpad5"), new("6", "Numpad6"),
        new("1", "Numpad1"), new("2", "Numpad2"), new("3", "Numpad3"), new("Enter", "NumpadEnter"),
        new("0", "Numpad0", 2), new(".", "NumpadDecimal")
    ];
}

