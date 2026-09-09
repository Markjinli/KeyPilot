using System.Globalization;
using KeyPilot.Core.Input;

namespace KeyPilot.Platform.Windows.Input;

/// <summary>
/// Matches live opaque HID report edges against the direction captured by
/// <see cref="OpaqueHidInputSourceFactory"/>. It never infers semantics from unknown bytes.
/// </summary>
public static class OpaqueHidInputMatcher
{
    public static bool TryMatch(
        InputSource? capturedSource,
        RawHidDeviceDescriptor device,
        UnknownHidReportChange change,
        out InputEventPhase phase)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(change);
        phase = default;

        if (capturedSource?.Device is not { } selector || capturedSource.Control is not { } control ||
            selector.Kind != InputDeviceKind.Hid ||
            selector.MatchMode != DeviceMatchMode.ExactDevice ||
            !StringComparer.OrdinalIgnoreCase.Equals(selector.DeviceId, device.DevicePath) ||
            !StringComparer.OrdinalIgnoreCase.Equals(change.Identity.DevicePath, device.DevicePath) ||
            selector.VendorId is ushort vendorId && vendorId != device.VendorId ||
            selector.ProductId is ushort productId && productId != device.ProductId ||
            control.Kind != InputControlKind.RawCode ||
            control.UsagePage != device.UsagePage ||
            control.Usage != device.Usage ||
            change.Identity.UsagePage != device.UsagePage ||
            change.Identity.Usage != device.Usage ||
            !TryReadReportLength(control.RawQualifier, out var reportLength) ||
            reportLength != change.Identity.ReportLength ||
            !TryReadActivation(control.ActivationQualifier, out var baseline, out var active))
        {
            return false;
        }

        if (baseline.Keys.Any(offset => offset < 0 || offset >= change.CurrentReport.Count) ||
            !change.ByteChanges.Any(item => baseline.ContainsKey(item.Offset)))
        {
            return false;
        }

        if (Matches(change.PreviousReport, baseline) && Matches(change.CurrentReport, active))
        {
            phase = InputEventPhase.Pressed;
            return true;
        }

        if (Matches(change.PreviousReport, active) && Matches(change.CurrentReport, baseline))
        {
            phase = InputEventPhase.Released;
            return true;
        }

        return false;
    }

    private static bool Matches(IReadOnlyList<byte> report, IReadOnlyDictionary<int, byte> values) =>
        values.All(item => item.Key < report.Count && report[item.Key] == item.Value);

    private static bool TryReadReportLength(string? qualifier, out int reportLength)
    {
        reportLength = 0;
        if (string.IsNullOrWhiteSpace(qualifier) ||
            !qualifier.StartsWith("OPAQUE-HID-V1;", StringComparison.Ordinal))
        {
            return false;
        }

        var lengthParts = qualifier.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => part.StartsWith("LEN=", StringComparison.Ordinal))
            .Select(part => part[4..])
            .ToArray();
        return lengthParts.Length == 1 &&
            int.TryParse(lengthParts[0], NumberStyles.None, CultureInfo.InvariantCulture, out reportLength) &&
            reportLength > 0;
    }

    private static bool TryReadActivation(
        string? qualifier,
        out IReadOnlyDictionary<int, byte> baseline,
        out IReadOnlyDictionary<int, byte> active)
    {
        baseline = new Dictionary<int, byte>();
        active = new Dictionary<int, byte>();
        if (string.IsNullOrWhiteSpace(qualifier))
        {
            return false;
        }

        var parts = qualifier.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3 || parts[0] != "OPAQUE-HID-ACTIVATION-V1" ||
            !parts[1].StartsWith("BASE=", StringComparison.Ordinal) ||
            !parts[2].StartsWith("ACTIVE=", StringComparison.Ordinal) ||
            !TryReadValues(parts[1][5..], out var parsedBaseline) ||
            !TryReadValues(parts[2][7..], out var parsedActive) ||
            parsedBaseline.Count != parsedActive.Count ||
            parsedBaseline.Keys.Any(offset =>
                !parsedActive.TryGetValue(offset, out var activeValue) || activeValue == parsedBaseline[offset]))
        {
            return false;
        }

        baseline = parsedBaseline;
        active = parsedActive;
        return true;
    }

    private static bool TryReadValues(string value, out Dictionary<int, byte> result)
    {
        result = new Dictionary<int, byte>();
        var entries = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (entries.Length is 0 or > OpaqueHidInputSourceFactory.MaximumChangedByteCount)
        {
            return false;
        }

        foreach (var entry in entries)
        {
            var pair = entry.Split(':', StringSplitOptions.TrimEntries);
            if (pair.Length != 2 || pair[0].Length != 4 || pair[1].Length != 2 ||
                !int.TryParse(pair[0], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var offset) ||
                !byte.TryParse(pair[1], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var byteValue) ||
                !result.TryAdd(offset, byteValue))
            {
                result.Clear();
                return false;
            }
        }

        return true;
    }
}
