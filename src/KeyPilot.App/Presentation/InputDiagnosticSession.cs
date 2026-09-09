using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KeyPilot.Platform.Windows.Input;

namespace KeyPilot.App.Presentation;

/// <summary>
/// A short, explicitly started and size-bounded input trace. It records physical codes and
/// redacted device identities, never translated characters, window titles, or complete HID reports.
/// </summary>
internal sealed class InputDiagnosticSession
{
    internal const int MaximumEventCount = 256;
    internal const int MaximumUtf8Bytes = 64 * 1024;
    internal static readonly TimeSpan Duration = TimeSpan.FromSeconds(10);
    private const int ReservedSummaryBytes = 1_024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly Regex VidPidPattern = new(
        @"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly object _gate = new();
    private readonly string _logPath;
    private readonly List<string> _lines = [];
    private readonly Dictionary<string, string> _deviceAliases = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reportedHidDescriptors = new(StringComparer.Ordinal);
    private readonly HashSet<KeyboardEdgeKey> _pressedKeyboardControls = [];
    private readonly Dictionary<int, XInputTraceState> _xInputStates = [];
    private DateTimeOffset _startedUtc;
    private DateTimeOffset _deadlineUtc;
    private int _utf8Bytes;
    private bool _active;
    private bool _truncated;

    public InputDiagnosticSession(string? logPath = null)
    {
        _logPath = logPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KeyPilot",
            "input-diagnostic.jsonl");
    }

    public string LogPath => _logPath;

    public bool IsActive
    {
        get
        {
            lock (_gate)
            {
                return _active;
            }
        }
    }

    public DateTimeOffset DeadlineUtc
    {
        get
        {
            lock (_gate)
            {
                return _deadlineUtc;
            }
        }
    }

    public void Start(DateTimeOffset nowUtc, string build)
    {
        lock (_gate)
        {
            _lines.Clear();
            _deviceAliases.Clear();
            _reportedHidDescriptors.Clear();
            _pressedKeyboardControls.Clear();
            _xInputStates.Clear();
            _utf8Bytes = 0;
            _truncated = false;
            _startedUtc = nowUtc.ToUniversalTime();
            _deadlineUtc = _startedUtc + Duration;
            _active = true;
            AddLocked(new
            {
                v = 1,
                type = "session",
                durationMs = (int)Duration.TotalMilliseconds,
                pathPolicy = "redacted",
                build = Normalize(build)
            });
        }
    }

    public void ObserveKeyboard(RawKeyboardEvent input)
    {
        ArgumentNullException.ThrowIfNull(input);
        lock (_gate)
        {
            if (!CanObserveLocked(input.Timestamp))
            {
                return;
            }

            var device = DeviceTokenLocked(input.DevicePath, input.HasPhysicalDevice);
            var key = new KeyboardEdgeKey(device.Alias, input.MakeCode, (ushort)(input.Flags & 0x0006));
            if (input.IsKeyDown)
            {
                if (!_pressedKeyboardControls.Add(key))
                {
                    return;
                }
            }
            else
            {
                _pressedKeyboardControls.Remove(key);
            }

            AddLocked(new
            {
                t = ElapsedMillisecondsLocked(input.Timestamp),
                src = "rawkbd",
                dev = device.Alias,
                pathKind = device.PathKind,
                pathHash = device.PathHash,
                vid = device.Vid,
                pid = device.Pid,
                scan = input.MakeCode.ToString("X4"),
                vk = input.VirtualKey.ToString("X2"),
                flags = input.Flags.ToString("X4"),
                message = input.Message.ToString("X4"),
                extra = input.ExtraInformation.ToString("X8"),
                edge = input.IsKeyDown ? "down" : "up"
            });
        }
    }

    public void ObserveHidDescriptor(
        RawHidDeviceDescriptor device,
        int reportLength,
        DateTimeOffset timestampUtc)
    {
        ArgumentNullException.ThrowIfNull(device);
        lock (_gate)
        {
            if (!CanObserveLocked(timestampUtc))
            {
                return;
            }

            var token = DeviceTokenLocked(device.DevicePath, hasPhysicalDevice: true);
            var descriptorKey = string.Join(
                "/",
                token.Alias,
                device.UsagePage.ToString("X4"),
                device.Usage.ToString("X4"),
                reportLength);
            if (!_reportedHidDescriptors.Add(descriptorKey))
            {
                return;
            }

            AddLocked(new
            {
                t = ElapsedMillisecondsLocked(timestampUtc),
                src = "rawhid",
                @event = "descriptor",
                dev = token.Alias,
                pathHash = token.PathHash,
                vid = device.VendorId.ToString("X4"),
                pid = device.ProductId.ToString("X4"),
                version = device.VersionNumber.ToString("X4"),
                usagePage = device.UsagePage.ToString("X4"),
                usage = device.Usage.ToString("X4"),
                reportLength
            });
        }
    }

    public void ObserveHidChange(
        RawHidDeviceDescriptor device,
        UnknownHidReportChange change)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            if (!CanObserveLocked(change.TimestampUtc))
            {
                return;
            }

            const int maximumShownChanges = 16;
            var token = DeviceTokenLocked(device.DevicePath, hasPhysicalDevice: true);
            var delta = change.ByteChanges
                .Take(maximumShownChanges)
                .Select(item => new
                {
                    offset = item.Offset,
                    from = item.PreviousValue.ToString("X2"),
                    to = item.CurrentValue.ToString("X2")
                })
                .ToArray();
            AddLocked(new
            {
                t = ElapsedMillisecondsLocked(change.TimestampUtc),
                src = "rawhid",
                @event = "delta",
                dev = token.Alias,
                sequence = change.SequenceNumber,
                reportId = (byte?)null,
                changedCount = change.ByteChanges.Count,
                truncatedDelta = change.ByteChanges.Count > maximumShownChanges,
                delta
            });
        }
    }

    public void ObserveXInputState(
        int userIndex,
        bool isConnected,
        uint packetNumber,
        ushort buttons,
        byte leftTrigger,
        byte rightTrigger,
        short thumbLX,
        short thumbLY,
        short thumbRX,
        short thumbRY,
        DateTimeOffset timestampUtc)
    {
        lock (_gate)
        {
            if (!CanObserveLocked(timestampUtc))
            {
                return;
            }

            _xInputStates.TryGetValue(userIndex, out var previous);
            if (previous.IsConnected != isConnected)
            {
                AddLocked(new
                {
                    t = ElapsedMillisecondsLocked(timestampUtc),
                    src = "xinput",
                    slot = userIndex,
                    packet = packetNumber,
                    @event = isConnected ? "connected" : "disconnected"
                });
            }

            if (!isConnected)
            {
                _xInputStates[userIndex] = default;
                return;
            }

            var changedButtons = (ushort)((previous.IsConnected ? previous.Buttons : 0) ^ buttons);
            for (var bit = 0; bit < 16; bit++)
            {
                var mask = (ushort)(1 << bit);
                if ((changedButtons & mask) == 0)
                {
                    continue;
                }

                AddLocked(new
                {
                    t = ElapsedMillisecondsLocked(timestampUtc),
                    src = "xinput",
                    slot = userIndex,
                    packet = packetNumber,
                    control = XInputButtonName(mask),
                    mask = mask.ToString("X4"),
                    edge = (buttons & mask) != 0 ? "down" : "up"
                });
            }

            var next = previous with
            {
                IsConnected = true,
                Buttons = buttons,
                LeftTriggerPressed = TraceTriggerLocked(
                    userIndex,
                    packetNumber,
                    "LT",
                    previous.LeftTriggerPressed,
                    leftTrigger,
                    timestampUtc),
                RightTriggerPressed = TraceTriggerLocked(
                    userIndex,
                    packetNumber,
                    "RT",
                    previous.RightTriggerPressed,
                    rightTrigger,
                    timestampUtc)
            };
            next = next with
            {
                LeftUp = TraceAxisLocked(userIndex, packetNumber, "L_UP", previous.LeftUp, thumbLY, positive: true, XInputVirtualControlThresholds.LeftStickPress, XInputVirtualControlThresholds.LeftStickRelease, timestampUtc),
                LeftDown = TraceAxisLocked(userIndex, packetNumber, "L_DOWN", previous.LeftDown, thumbLY, positive: false, XInputVirtualControlThresholds.LeftStickPress, XInputVirtualControlThresholds.LeftStickRelease, timestampUtc),
                LeftLeft = TraceAxisLocked(userIndex, packetNumber, "L_LEFT", previous.LeftLeft, thumbLX, positive: false, XInputVirtualControlThresholds.LeftStickPress, XInputVirtualControlThresholds.LeftStickRelease, timestampUtc),
                LeftRight = TraceAxisLocked(userIndex, packetNumber, "L_RIGHT", previous.LeftRight, thumbLX, positive: true, XInputVirtualControlThresholds.LeftStickPress, XInputVirtualControlThresholds.LeftStickRelease, timestampUtc),
                RightUp = TraceAxisLocked(userIndex, packetNumber, "R_UP", previous.RightUp, thumbRY, positive: true, XInputVirtualControlThresholds.RightStickPress, XInputVirtualControlThresholds.RightStickRelease, timestampUtc),
                RightDown = TraceAxisLocked(userIndex, packetNumber, "R_DOWN", previous.RightDown, thumbRY, positive: false, XInputVirtualControlThresholds.RightStickPress, XInputVirtualControlThresholds.RightStickRelease, timestampUtc),
                RightLeft = TraceAxisLocked(userIndex, packetNumber, "R_LEFT", previous.RightLeft, thumbRX, positive: false, XInputVirtualControlThresholds.RightStickPress, XInputVirtualControlThresholds.RightStickRelease, timestampUtc),
                RightRight = TraceAxisLocked(userIndex, packetNumber, "R_RIGHT", previous.RightRight, thumbRX, positive: true, XInputVirtualControlThresholds.RightStickPress, XInputVirtualControlThresholds.RightStickRelease, timestampUtc)
            };
            _xInputStates[userIndex] = next;
        }
    }

    public InputDiagnosticSaveResult StopAndSave(string reason, DateTimeOffset nowUtc)
    {
        string[] lines;
        bool truncated;
        lock (_gate)
        {
            if (!_active)
            {
                return new InputDiagnosticSaveResult(_logPath, 0, false, false, null);
            }

            AddLocked(new
            {
                v = 1,
                type = "summary",
                elapsedMs = ElapsedMillisecondsLocked(nowUtc),
                reason = Normalize(reason),
                truncated = _truncated,
                eventCount = Math.Max(0, _lines.Count - 1)
            }, allowWhenTruncated: true);
            _active = false;
            lines = _lines.ToArray();
            truncated = _truncated;
        }

        try
        {
            var directory = Path.GetDirectoryName(_logPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new InvalidOperationException("Input diagnostic path has no parent directory.");
            }

            Directory.CreateDirectory(directory);
            var temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(_logPath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllLines(temporaryPath, lines, new UTF8Encoding(false));
                if (File.Exists(_logPath))
                {
                    File.Move(_logPath, _logPath + ".1", overwrite: true);
                }

                File.Move(temporaryPath, _logPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }

            return new InputDiagnosticSaveResult(
                _logPath,
                Math.Max(0, lines.Length - 2),
                truncated,
                true,
                null);
        }
        catch (Exception exception)
        {
            return new InputDiagnosticSaveResult(
                _logPath,
                Math.Max(0, lines.Length - 2),
                truncated,
                false,
                exception.Message);
        }
    }

    private bool TraceTriggerLocked(
        int userIndex,
        uint packetNumber,
        string control,
        bool wasPressed,
        byte value,
        DateTimeOffset timestampUtc)
    {
        var isPressed = wasPressed
            ? value > XInputVirtualControlThresholds.TriggerRelease
            : value >= XInputVirtualControlThresholds.TriggerPress;
        if (isPressed != wasPressed)
        {
            AddLocked(new
            {
                t = ElapsedMillisecondsLocked(timestampUtc),
                src = "xinput",
                slot = userIndex,
                packet = packetNumber,
                control,
                edge = isPressed ? "down" : "up",
                valuePercent = (int)Math.Round(value * 100d / byte.MaxValue)
            });
        }

        return isPressed;
    }

    private bool TraceAxisLocked(
        int userIndex,
        uint packetNumber,
        string control,
        bool wasPressed,
        short value,
        bool positive,
        short pressThreshold,
        short releaseThreshold,
        DateTimeOffset timestampUtc)
    {
        var signed = positive ? (int)value : -(int)value;
        var isPressed = wasPressed
            ? signed > releaseThreshold
            : signed >= pressThreshold;
        if (isPressed != wasPressed)
        {
            AddLocked(new
            {
                t = ElapsedMillisecondsLocked(timestampUtc),
                src = "xinput",
                slot = userIndex,
                packet = packetNumber,
                control,
                edge = isPressed ? "down" : "up",
                valuePercent = (int)Math.Round(value * 100d / short.MaxValue)
            });
        }

        return isPressed;
    }

    private bool CanObserveLocked(DateTimeOffset timestampUtc) =>
        _active && timestampUtc.ToUniversalTime() <= _deadlineUtc;

    private int ElapsedMillisecondsLocked(DateTimeOffset timestampUtc)
    {
        var elapsed = timestampUtc.ToUniversalTime() - _startedUtc;
        return (int)Math.Clamp(elapsed.TotalMilliseconds, 0, int.MaxValue);
    }

    private DeviceToken DeviceTokenLocked(string? devicePath, bool hasPhysicalDevice)
    {
        var normalizedPath = string.IsNullOrWhiteSpace(devicePath)
            ? "RAWINPUT:SYNTHETIC-KEYBOARD:NO-PHYSICAL-DEVICE"
            : devicePath.Trim();
        if (!_deviceAliases.TryGetValue(normalizedPath, out var alias))
        {
            alias = $"dev{_deviceAliases.Count + 1}";
            _deviceAliases.Add(normalizedPath, alias);
        }

        var match = VidPidPattern.Match(normalizedPath);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)))[..12];
        return new DeviceToken(
            alias,
            hasPhysicalDevice ? "physical" : "synthetic",
            hash,
            match.Success ? match.Groups[1].Value.ToUpperInvariant() : null,
            match.Success ? match.Groups[2].Value.ToUpperInvariant() : null);
    }

    private void AddLocked(object value, bool allowWhenTruncated = false)
    {
        if (_truncated && !allowWhenTruncated)
        {
            return;
        }

        var line = JsonSerializer.Serialize(value, JsonOptions);
        var lineBytes = Encoding.UTF8.GetByteCount(line) +
            Encoding.UTF8.GetByteCount(Environment.NewLine);
        if (!allowWhenTruncated &&
            (_lines.Count >= MaximumEventCount ||
             _utf8Bytes + lineBytes > MaximumUtf8Bytes - ReservedSummaryBytes))
        {
            _truncated = true;
            return;
        }

        if (allowWhenTruncated && _utf8Bytes + lineBytes > MaximumUtf8Bytes)
        {
            return;
        }

        _lines.Add(line);
        _utf8Bytes += lineBytes;
    }

    private static string XInputButtonName(ushort mask) => mask switch
    {
        (ushort)XInputButton.DPadUp => nameof(XInputButton.DPadUp),
        (ushort)XInputButton.DPadDown => nameof(XInputButton.DPadDown),
        (ushort)XInputButton.DPadLeft => nameof(XInputButton.DPadLeft),
        (ushort)XInputButton.DPadRight => nameof(XInputButton.DPadRight),
        (ushort)XInputButton.Menu => nameof(XInputButton.Menu),
        (ushort)XInputButton.View => nameof(XInputButton.View),
        (ushort)XInputButton.LStick => nameof(XInputButton.LStick),
        (ushort)XInputButton.RStick => nameof(XInputButton.RStick),
        (ushort)XInputButton.LB => nameof(XInputButton.LB),
        (ushort)XInputButton.RB => nameof(XInputButton.RB),
        (ushort)XInputButton.A => nameof(XInputButton.A),
        (ushort)XInputButton.B => nameof(XInputButton.B),
        (ushort)XInputButton.X => nameof(XInputButton.X),
        (ushort)XInputButton.Y => nameof(XInputButton.Y),
        _ => $"BIT_{mask:X4}"
    };

    private static string Normalize(string? value) =>
        (value ?? string.Empty).Replace('\0', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();

    private readonly record struct DeviceToken(
        string Alias,
        string PathKind,
        string PathHash,
        string? Vid,
        string? Pid);

    private readonly record struct KeyboardEdgeKey(string DeviceAlias, ushort MakeCode, ushort Prefix);

    private readonly record struct XInputTraceState(
        bool IsConnected,
        ushort Buttons,
        bool LeftTriggerPressed,
        bool RightTriggerPressed,
        bool LeftUp,
        bool LeftDown,
        bool LeftLeft,
        bool LeftRight,
        bool RightUp,
        bool RightDown,
        bool RightLeft,
        bool RightRight);
}

internal sealed record InputDiagnosticSaveResult(
    string Path,
    int EventCount,
    bool Truncated,
    bool Saved,
    string? Error);
