using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KeyPilot.Core.Input;

namespace KeyPilot.Core.Actions;

/// <summary>The logical output family, deliberately excluding any physical device selector.</summary>
public enum LogicalOutputFamily
{
    Keyboard,
    Gamepad,
    Hid
}

/// <summary>
/// A versioned, device-independent output description. Multiple controls form one simultaneous
/// chord and are emitted in list order, then released in reverse order.
/// </summary>
public sealed record LogicalOutputTarget
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    public LogicalOutputFamily Family { get; init; }

    public List<InputControlId> Controls { get; init; } = new();
}

/// <summary>A bounded-format error suitable for showing directly beside a copy-output field.</summary>
public sealed class LogicalOutputTargetCodecException : FormatException
{
    public LogicalOutputTargetCodecException(string message)
        : base(message)
    {
    }

    public LogicalOutputTargetCodecException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Converts captured input identities into a safe logical-output token. Device selectors,
/// report coordinates, activation predicates, and vendor baselines never enter the token.
/// </summary>
public static class LogicalOutputTargetCodec
{
    public const string Prefix = "KPOT1.";
    public const int MaximumControlCount = 8;
    public const int MaximumEncodedLength = 16_384;

    private const int MaximumJsonBytes = 12_000;
    private const int MaximumKeyboardQualifierLength = 32;

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static LogicalOutputTarget FromInputSource(InputSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(source.Control);

        var controls = new List<InputControlId>();
        LogicalOutputFamily family;
        if (source.Control.Kind == InputControlKind.InputChord)
        {
            if (source.ChordMembers is null
                || source.ChordMembers.Count is < 2 or > MaximumControlCount)
            {
                throw new LogicalOutputTargetCodecException(
                    $"An input chord must contain 2 through {MaximumControlCount} controls.");
            }

            LogicalOutputFamily? chordFamily = null;
            foreach (var member in source.ChordMembers)
            {
                if (member is null || member.Control is null)
                {
                    throw new LogicalOutputTargetCodecException(
                        "An input chord contains a missing control.");
                }

                if (member.Control.Kind == InputControlKind.InputChord)
                {
                    throw new LogicalOutputTargetCodecException(
                        "Nested input chords cannot be copied to an output target.");
                }

                var memberFamily = FamilyOf(member.Control);
                if (chordFamily.HasValue && chordFamily.Value != memberFamily)
                {
                    throw new LogicalOutputTargetCodecException(
                        "A mixed keyboard/gamepad/HID chord has no safe logical output backend.");
                }

                chordFamily = memberFamily;
                controls.Add(CopyLogicalControl(member.Control));
            }

            family = chordFamily!.Value;
        }
        else
        {
            family = FamilyOf(source.Control);
            controls.Add(CopyLogicalControl(source.Control));
        }

        var target = new LogicalOutputTarget
        {
            Family = family,
            Controls = controls
        };
        Validate(target);
        return target;
    }

    public static bool TryFromInputSource(
        InputSource? source,
        out LogicalOutputTarget? target,
        out string error)
    {
        try
        {
            if (source is null)
            {
                throw new LogicalOutputTargetCodecException("The captured input source is missing.");
            }

            target = FromInputSource(source);
            error = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or LogicalOutputTargetCodecException)
        {
            target = null;
            error = exception.Message;
            return false;
        }
    }

    public static string Encode(LogicalOutputTarget target)
    {
        Validate(target);
        var json = JsonSerializer.SerializeToUtf8Bytes(target, JsonOptions);
        if (json.Length > MaximumJsonBytes)
        {
            throw new LogicalOutputTargetCodecException("The logical output target is too large.");
        }

        var payload = Convert.ToBase64String(json)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var encoded = Prefix + payload;
        if (encoded.Length > MaximumEncodedLength)
        {
            throw new LogicalOutputTargetCodecException("The encoded logical output target is too large.");
        }

        return encoded;
    }

    public static LogicalOutputTarget Decode(string encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded))
        {
            throw new LogicalOutputTargetCodecException("The logical output target is empty.");
        }

        if (encoded.Length > MaximumEncodedLength)
        {
            throw new LogicalOutputTargetCodecException("The encoded logical output target is too large.");
        }

        if (!encoded.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new LogicalOutputTargetCodecException(
                $"Unsupported logical output target version; expected prefix {Prefix}");
        }

        try
        {
            var payload = encoded[Prefix.Length..];
            if (payload.Length == 0 || payload.Any(character =>
                    !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            {
                throw new LogicalOutputTargetCodecException(
                    "The logical output target payload is not valid base64url.");
            }

            var padded = payload.Replace('-', '+').Replace('_', '/');
            padded += (padded.Length % 4) switch
            {
                0 => string.Empty,
                2 => "==",
                3 => "=",
                _ => throw new LogicalOutputTargetCodecException(
                    "The logical output target payload has an invalid length.")
            };
            var json = Convert.FromBase64String(padded);
            if (json.Length > MaximumJsonBytes)
            {
                throw new LogicalOutputTargetCodecException("The logical output target is too large.");
            }

            var target = JsonSerializer.Deserialize<LogicalOutputTarget>(json, JsonOptions)
                ?? throw new LogicalOutputTargetCodecException(
                    "The logical output target payload is empty.");
            Validate(target);
            return target;
        }
        catch (LogicalOutputTargetCodecException)
        {
            throw;
        }
        catch (Exception exception) when (exception is FormatException
                                              or JsonException
                                              or DecoderFallbackException)
        {
            throw new LogicalOutputTargetCodecException(
                $"The logical output target payload is invalid: {exception.Message}",
                exception);
        }
    }

    public static bool TryDecode(
        string? encoded,
        out LogicalOutputTarget? target,
        out string error)
    {
        try
        {
            target = Decode(encoded ?? string.Empty);
            error = string.Empty;
            return true;
        }
        catch (LogicalOutputTargetCodecException exception)
        {
            target = null;
            error = exception.Message;
            return false;
        }
    }

    public static void Validate(LogicalOutputTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Version != LogicalOutputTarget.CurrentVersion)
        {
            throw new LogicalOutputTargetCodecException(
                $"Logical output target version {target.Version} is unsupported.");
        }

        if (!Enum.IsDefined(target.Family))
        {
            throw new LogicalOutputTargetCodecException("The logical output family is unsupported.");
        }

        if (target.Controls is null
            || target.Controls.Count is < 1 or > MaximumControlCount)
        {
            throw new LogicalOutputTargetCodecException(
                $"A logical output target must contain 1 through {MaximumControlCount} controls.");
        }

        var identities = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < target.Controls.Count; index++)
        {
            var control = target.Controls[index]
                ?? throw new LogicalOutputTargetCodecException(
                    $"Logical output control {index} is missing.");
            ValidateControl(target.Family, control, index);
            if (!identities.Add(control.CanonicalKey))
            {
                throw new LogicalOutputTargetCodecException(
                    $"Logical output control {index} duplicates an earlier control.");
            }
        }
    }

    private static LogicalOutputFamily FamilyOf(InputControlId control) => control.Kind switch
    {
        InputControlKind.KeyboardScanCode or InputControlKind.VirtualKey =>
            LogicalOutputFamily.Keyboard,
        InputControlKind.GamepadButton
            or InputControlKind.GamepadAxisDirection
            or InputControlKind.GamepadRotation => LogicalOutputFamily.Gamepad,
        InputControlKind.HidUsage => LogicalOutputFamily.Hid,
        InputControlKind.RawCode => throw new LogicalOutputTargetCodecException(
            "Vendor raw input is physical-device data and cannot become a logical output target; " +
            "no virtual HID output backend is available."),
        _ => throw new LogicalOutputTargetCodecException(
            $"Input control {control.Kind} cannot become a logical output target.")
    };

    private static InputControlId CopyLogicalControl(InputControlId control)
    {
        return control.Kind switch
        {
            InputControlKind.KeyboardScanCode => new InputControlId
            {
                Kind = control.Kind,
                Code = control.Code,
                IsExtended = control.IsExtended,
                RawQualifier = ExtractKeyboardPrefix(control.RawQualifier)
            },
            InputControlKind.VirtualKey => new InputControlId
            {
                Kind = control.Kind,
                Code = control.Code,
                IsExtended = control.IsExtended
            },
            InputControlKind.GamepadButton
                or InputControlKind.GamepadAxisDirection
                or InputControlKind.GamepadRotation => new InputControlId
                {
                    Kind = control.Kind,
                    Code = control.Code
                },
            InputControlKind.HidUsage => new InputControlId
            {
                Kind = control.Kind,
                Code = control.Code,
                UsagePage = control.UsagePage,
                Usage = control.Usage
            },
            _ => throw new LogicalOutputTargetCodecException(
                $"Input control {control.Kind} cannot become a logical output target.")
        };
    }

    private static string? ExtractKeyboardPrefix(string? qualifier)
    {
        var prefixes = qualifier?.Split(
                ';',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Equals("PREFIX=0002", StringComparison.OrdinalIgnoreCase)
                || token.Equals("PREFIX=0004", StringComparison.OrdinalIgnoreCase))
            .Select(token => token.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return prefixes?.Length switch
        {
            null or 0 => null,
            1 => prefixes[0],
            _ => throw new LogicalOutputTargetCodecException(
                "A keyboard control cannot contain both E0 and E1 prefixes.")
        };
    }

    private static void ValidateControl(
        LogicalOutputFamily family,
        InputControlId control,
        int index)
    {
        if (control.ActivationQualifier is not null
            || control.ReportId is not null
            || control.DataIndex is not null
            || control.LinkCollection is not null)
        {
            throw new LogicalOutputTargetCodecException(
                $"Logical output control {index} contains physical input identity data.");
        }

        switch (family)
        {
            case LogicalOutputFamily.Keyboard:
                if (control.Kind is not InputControlKind.KeyboardScanCode
                    and not InputControlKind.VirtualKey)
                {
                    throw FamilyMismatch(index, family, control.Kind);
                }

                if (control.Kind == InputControlKind.KeyboardScanCode
                    && control.Code is < 1 or > byte.MaxValue)
                {
                    throw new LogicalOutputTargetCodecException(
                        $"Keyboard scan code {index} must be between 0x01 and 0xFF.");
                }

                if (control.Kind == InputControlKind.VirtualKey
                    && control.Code is < 1 or > 0xFE)
                {
                    throw new LogicalOutputTargetCodecException(
                        $"Virtual key {index} must be between 0x01 and 0xFE.");
                }

                if (control.UsagePage is not null || control.Usage is not null)
                {
                    throw new LogicalOutputTargetCodecException(
                        $"Keyboard output control {index} contains HID identity data.");
                }

                if (control.RawQualifier?.Length > MaximumKeyboardQualifierLength
                    || ExtractKeyboardPrefix(control.RawQualifier) != control.RawQualifier)
                {
                    throw new LogicalOutputTargetCodecException(
                        $"Keyboard output control {index} has an unsafe scan-code qualifier.");
                }
                break;

            case LogicalOutputFamily.Gamepad:
                if (control.Kind is not InputControlKind.GamepadButton
                    and not InputControlKind.GamepadAxisDirection
                    and not InputControlKind.GamepadRotation)
                {
                    throw FamilyMismatch(index, family, control.Kind);
                }

                RequireBareControl(control, index);
                break;

            case LogicalOutputFamily.Hid:
                if (control.Kind != InputControlKind.HidUsage
                    || control.UsagePage is null
                    || control.Usage is null)
                {
                    throw FamilyMismatch(index, family, control.Kind);
                }

                if (control.RawQualifier is not null || control.IsExtended)
                {
                    throw new LogicalOutputTargetCodecException(
                        $"HID output control {index} contains physical input qualifiers.");
                }
                break;

            default:
                throw new LogicalOutputTargetCodecException("The logical output family is unsupported.");
        }
    }

    private static void RequireBareControl(InputControlId control, int index)
    {
        if (control.UsagePage is not null
            || control.Usage is not null
            || control.RawQualifier is not null
            || control.IsExtended)
        {
            throw new LogicalOutputTargetCodecException(
                $"Gamepad output control {index} contains physical input qualifiers.");
        }
    }

    private static LogicalOutputTargetCodecException FamilyMismatch(
        int index,
        LogicalOutputFamily family,
        InputControlKind kind) =>
        new($"Logical output control {index} ({kind}) does not belong to {family}.");

    private static JsonSerializerOptions CreateJsonOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
        }
    };
}
