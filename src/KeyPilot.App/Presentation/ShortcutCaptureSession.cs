using KeyPilot.Core.Input;
using KeyPilot.Platform.Windows.Input;

namespace KeyPilot.App.Presentation;

internal enum ShortcutCaptureState
{
    Idle,
    Armed,
    Completed,
    Rejected,
    Cancelled
}

internal enum ShortcutCaptureFailure
{
    None,
    NoKeys,
    PureModifier,
    TooManyDistinctKeys,
    TooManyEdges
}

/// <summary>
/// Explicit two-second keyboard shortcut recorder. Raw-input callers must discard packets marked
/// by <see cref="InputInjectionMarker"/> before observing them; normalized injected events are
/// rejected defensively here.
/// </summary>
internal sealed class ShortcutCaptureSession
{
    public const int MaximumDistinctKeys = 8;
    public const int MaximumAcceptedEdges = 64;

    public static readonly TimeSpan CaptureDuration = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly HashSet<string> _downKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CapturedKey> _capturedTokens =
        new(StringComparer.OrdinalIgnoreCase);

    private ShortcutCaptureState _state;
    private ShortcutCaptureFailure _failure;
    private DateTimeOffset? _armedAtUtc;
    private DateTimeOffset? _deadlineUtc;
    private string? _resultText;
    private int _acceptedEdgeCount;
    private int _captureOrder;

    public ShortcutCaptureState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public ShortcutCaptureFailure Failure
    {
        get
        {
            lock (_gate)
            {
                return _failure;
            }
        }
    }

    public DateTimeOffset? DeadlineUtc
    {
        get
        {
            lock (_gate)
            {
                return _deadlineUtc;
            }
        }
    }

    public string? CompletedText
    {
        get
        {
            lock (_gate)
            {
                return _resultText;
            }
        }
    }

    public string? Error
    {
        get
        {
            lock (_gate)
            {
                return _failure switch
                {
                    ShortcutCaptureFailure.None => null,
                    ShortcutCaptureFailure.NoKeys => "2 秒内没有录制到键盘按键。",
                    ShortcutCaptureFailure.PureModifier => "快捷键不能只包含 Ctrl、Alt、Shift 或 Win。",
                    ShortcutCaptureFailure.TooManyDistinctKeys =>
                        $"快捷键最多包含 {MaximumDistinctKeys} 个不同按键。",
                    ShortcutCaptureFailure.TooManyEdges =>
                        $"录制期间的按键边沿超过 {MaximumAcceptedEdges} 个，请重试。",
                    _ => "快捷键录制失败。"
                };
            }
        }
    }

    public string CurrentText
    {
        get
        {
            lock (_gate)
            {
                return FormatCapturedLocked();
            }
        }
    }

    public int AcceptedEdgeCount
    {
        get
        {
            lock (_gate)
            {
                return _acceptedEdgeCount;
            }
        }
    }

    public void Arm(DateTimeOffset armedAtUtc)
    {
        lock (_gate)
        {
            _downKeys.Clear();
            _capturedTokens.Clear();
            _acceptedEdgeCount = 0;
            _captureOrder = 0;
            _resultText = null;
            _failure = ShortcutCaptureFailure.None;
            _armedAtUtc = armedAtUtc.ToUniversalTime();
            _deadlineUtc = _armedAtUtc.Value + CaptureDuration;
            _state = ShortcutCaptureState.Armed;
        }
    }

    /// <summary>
    /// Observes one physical Raw Input edge. The caller owns process-marker filtering because the
    /// session deliberately has no knowledge of the current process injection marker.
    /// </summary>
    public ShortcutCaptureState Observe(RawKeyboardEvent input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var stableId = KeyboardKeyResolver.Resolve(input);
        if (stableId is null)
        {
            return State;
        }

        lock (_gate)
        {
            return ObserveResolvedLocked(stableId, input.IsKeyDown, input.Timestamp.ToUniversalTime());
        }
    }

    /// <summary>Observes one normalized keyboard edge; repeats and injected events are ignored.</summary>
    public ShortcutCaptureState Observe(InputEvent inputEvent)
    {
        ArgumentNullException.ThrowIfNull(inputEvent);
        if (inputEvent.IsInjected ||
            inputEvent.Phase == InputEventPhase.Repeated ||
            inputEvent.Source?.Device?.Kind != InputDeviceKind.Keyboard ||
            inputEvent.Source.Control is null ||
            !TryResolveControl(inputEvent.Source.Control, out var stableId))
        {
            return State;
        }

        lock (_gate)
        {
            return ObserveResolvedLocked(
                stableId,
                inputEvent.Phase == InputEventPhase.Pressed,
                inputEvent.TimestampUtc.ToUniversalTime());
        }
    }

    /// <summary>Completes or rejects the recording once the fixed two-second deadline is reached.</summary>
    public bool TryComplete(DateTimeOffset timestampUtc)
    {
        lock (_gate)
        {
            if (_state != ShortcutCaptureState.Armed ||
                !_deadlineUtc.HasValue ||
                timestampUtc.ToUniversalTime() < _deadlineUtc.Value)
            {
                return false;
            }

            CompleteLocked();
            return true;
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            if (_state != ShortcutCaptureState.Armed)
            {
                return;
            }

            _downKeys.Clear();
            _deadlineUtc = null;
            _state = ShortcutCaptureState.Cancelled;
        }
    }

    private ShortcutCaptureState ObserveResolvedLocked(
        string stableId,
        bool isKeyDown,
        DateTimeOffset timestampUtc)
    {
        if (_state != ShortcutCaptureState.Armed)
        {
            return _state;
        }

        if (_armedAtUtc.HasValue && timestampUtc < _armedAtUtc.Value)
        {
            return _state;
        }

        if (_deadlineUtc.HasValue && timestampUtc >= _deadlineUtc.Value)
        {
            CompleteLocked();
            return _state;
        }

        var changed = isKeyDown
            ? _downKeys.Add(stableId)
            : _downKeys.Remove(stableId);
        if (!changed)
        {
            return _state;
        }

        _acceptedEdgeCount++;
        if (_acceptedEdgeCount > MaximumAcceptedEdges)
        {
            RejectLocked(ShortcutCaptureFailure.TooManyEdges);
            return _state;
        }

        if (!isKeyDown)
        {
            return _state;
        }

        var captured = FormatStableId(stableId, _captureOrder);
        if (_capturedTokens.ContainsKey(captured.Token))
        {
            return _state;
        }

        if (_capturedTokens.Count >= MaximumDistinctKeys)
        {
            RejectLocked(ShortcutCaptureFailure.TooManyDistinctKeys);
            return _state;
        }

        _capturedTokens.Add(captured.Token, captured);
        _captureOrder++;
        return _state;
    }

    private void CompleteLocked()
    {
        _downKeys.Clear();
        _deadlineUtc = null;
        if (_capturedTokens.Count == 0)
        {
            RejectLocked(ShortcutCaptureFailure.NoKeys);
            return;
        }

        if (_capturedTokens.Values.All(static key => key.IsModifier))
        {
            RejectLocked(ShortcutCaptureFailure.PureModifier);
            return;
        }

        _resultText = FormatCapturedLocked();
        _failure = ShortcutCaptureFailure.None;
        _state = ShortcutCaptureState.Completed;
    }

    private void RejectLocked(ShortcutCaptureFailure failure)
    {
        _downKeys.Clear();
        _deadlineUtc = null;
        _resultText = null;
        _failure = failure;
        _state = ShortcutCaptureState.Rejected;
    }

    private string FormatCapturedLocked() => string.Join(
        " + ",
        _capturedTokens.Values
            .OrderBy(static key => key.SortGroup)
            .ThenBy(static key => key.CaptureOrder)
            .Select(static key => key.Token));

    private static CapturedKey FormatStableId(string stableId, int captureOrder) => stableId switch
    {
        "ControlLeft" or "ControlRight" => new CapturedKey("Ctrl", true, 0, captureOrder),
        "AltLeft" or "AltRight" => new CapturedKey("Alt", true, 1, captureOrder),
        "ShiftLeft" or "ShiftRight" => new CapturedKey("Shift", true, 2, captureOrder),
        "MetaLeft" or "MetaRight" => new CapturedKey("Win", true, 3, captureOrder),
        "Escape" => NonModifier("Esc", captureOrder),
        "ArrowUp" => NonModifier("Up", captureOrder),
        "ArrowDown" => NonModifier("Down", captureOrder),
        "ArrowLeft" => NonModifier("Left", captureOrder),
        "ArrowRight" => NonModifier("Right", captureOrder),
        "Equal" => NonModifier("=", captureOrder),
        "Backquote" => NonModifier("`", captureOrder),
        "BracketLeft" => NonModifier("[", captureOrder),
        "BracketRight" => NonModifier("]", captureOrder),
        "Backslash" => NonModifier("\\", captureOrder),
        "Semicolon" => NonModifier(";", captureOrder),
        "Quote" => NonModifier("'", captureOrder),
        "Comma" => NonModifier(",", captureOrder),
        "Period" => NonModifier(".", captureOrder),
        "Slash" => NonModifier("/", captureOrder),
        "Minus" => NonModifier("-", captureOrder),
        _ when stableId.StartsWith("Key", StringComparison.Ordinal) =>
            NonModifier(stableId[3..], captureOrder),
        _ when stableId.StartsWith("Digit", StringComparison.Ordinal) =>
            NonModifier(stableId[5..], captureOrder),
        _ => NonModifier(stableId, captureOrder)
    };

    private static CapturedKey NonModifier(string token, int captureOrder) =>
        new(token, false, 4, captureOrder);

    private static bool TryResolveControl(InputControlId control, out string stableId)
    {
        stableId = string.Empty;
        if (control.Kind == InputControlKind.KeyboardScanCode &&
            control.Code is > 0 and <= ushort.MaxValue)
        {
            var prefix = control.RawQualifier?.Contains("PREFIX=0004", StringComparison.OrdinalIgnoreCase) == true
                ? (ushort)0x0004
                : control.IsExtended
                    ? (ushort)0x0002
                    : (ushort)0;
            return KeyboardKeyResolver.TryResolveSet1Code((ushort)control.Code, prefix, out stableId);
        }

        if (control.Kind != InputControlKind.VirtualKey || control.Code is <= 0 or > byte.MaxValue)
        {
            return false;
        }

        stableId = ResolveVirtualKey((byte)control.Code) ?? string.Empty;
        return stableId.Length != 0;
    }

    private static string? ResolveVirtualKey(byte virtualKey)
    {
        if (virtualKey is >= 0x41 and <= 0x5A)
        {
            return $"Key{(char)virtualKey}";
        }

        if (virtualKey is >= 0x30 and <= 0x39)
        {
            return $"Digit{(char)virtualKey}";
        }

        if (virtualKey is >= 0x70 and <= 0x87)
        {
            return $"F{virtualKey - 0x6F}";
        }

        return virtualKey switch
        {
            0x08 => "Backspace",
            0x09 => "Tab",
            0x0D => "Enter",
            0x10 => "ShiftLeft",
            0x11 => "ControlLeft",
            0x12 => "AltLeft",
            0x13 => "Pause",
            0x1B => "Escape",
            0x20 => "Space",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "ArrowLeft",
            0x26 => "ArrowUp",
            0x27 => "ArrowRight",
            0x28 => "ArrowDown",
            0x2C => "PrintScreen",
            0x2D => "Insert",
            0x2E => "Delete",
            0x5B => "MetaLeft",
            0x5C => "MetaRight",
            0x5D => "ContextMenu",
            0x60 => "Numpad0",
            0x61 => "Numpad1",
            0x62 => "Numpad2",
            0x63 => "Numpad3",
            0x64 => "Numpad4",
            0x65 => "Numpad5",
            0x66 => "Numpad6",
            0x67 => "Numpad7",
            0x68 => "Numpad8",
            0x69 => "Numpad9",
            0x6A => "NumpadMultiply",
            0x6B => "NumpadAdd",
            0x6D => "NumpadSubtract",
            0x6E => "NumpadDecimal",
            0x6F => "NumpadDivide",
            0x90 => "NumLock",
            0x91 => "ScrollLock",
            _ => null
        };
    }

    private sealed record CapturedKey(
        string Token,
        bool IsModifier,
        int SortGroup,
        int CaptureOrder);
}
