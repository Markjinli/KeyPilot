using System.Globalization;
using KeyPilot.Core.Input;

namespace KeyPilot.Platform.Windows.Input;

/// <summary>Creates a stable, direction-independent Core identity for one confirmed opaque HID transition.</summary>
public static class OpaqueHidInputSourceFactory
{
    public const int MaximumChangedByteCount = 64;

    public static InputSource Create(
        RawHidDeviceDescriptor device,
        UnknownHidReportChange change)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(change);
        if (!StringComparer.OrdinalIgnoreCase.Equals(device.DevicePath, change.Identity.DevicePath) ||
            device.UsagePage != change.Identity.UsagePage ||
            device.Usage != change.Identity.Usage)
        {
            throw new ArgumentException(
                "The HID descriptor and report change must describe the same top-level collection.",
                nameof(change));
        }

        if (change.ByteChanges.Count is 0 or > MaximumChangedByteCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(change),
                change.ByteChanges.Count,
                $"An opaque HID source must change between 1 and {MaximumChangedByteCount} bytes.");
        }

        return new InputSource
        {
            Device = new InputDeviceSelector
            {
                Kind = InputDeviceKind.Hid,
                MatchMode = DeviceMatchMode.ExactDevice,
                DeviceId = device.DevicePath,
                VendorId = device.VendorId <= ushort.MaxValue ? (ushort)device.VendorId : null,
                ProductId = device.ProductId <= ushort.MaxValue ? (ushort)device.ProductId : null
            },
            Control = new InputControlId
            {
                Kind = InputControlKind.RawCode,
                Code = change.ByteChanges.Min(item => item.Offset),
                UsagePage = device.UsagePage,
                Usage = device.Usage,
                // A zero in UnknownHidReportIdentity is only an internal "not parsed" sentinel.
                ReportId = null,
                RawQualifier = BuildQualifier(change),
                ActivationQualifier = BuildActivationQualifier(change)
            }
        };
    }

    public static string BuildQualifier(UnknownHidReportChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.ByteChanges.Count is 0 or > MaximumChangedByteCount)
        {
            throw new ArgumentOutOfRangeException(nameof(change), change.ByteChanges.Count, "The HID transition is not capturable.");
        }

        var leadingA = change.PreviousReport[0];
        var leadingB = change.CurrentReport[0];
        var deltas = change.ByteChanges
            .OrderBy(item => item.Offset)
            .Select(item => string.Join(
                ':',
                item.Offset.ToString("X4", CultureInfo.InvariantCulture),
                (item.PreviousValue ^ item.CurrentValue).ToString("X2", CultureInfo.InvariantCulture),
                $"{Math.Min(item.PreviousValue, item.CurrentValue).ToString("X2", CultureInfo.InvariantCulture)}~{Math.Max(item.PreviousValue, item.CurrentValue).ToString("X2", CultureInfo.InvariantCulture)}"));
        return string.Create(
            CultureInfo.InvariantCulture,
            $"OPAQUE-HID-V1;LEN={change.Identity.ReportLength};LEAD={Math.Min(leadingA, leadingB):X2}~{Math.Max(leadingA, leadingB):X2};DELTA={string.Join(',', deltas)}");
    }

    public static string BuildActivationQualifier(UnknownHidReportChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.ByteChanges.Count is 0 or > MaximumChangedByteCount)
        {
            throw new ArgumentOutOfRangeException(nameof(change), change.ByteChanges.Count, "The HID transition is not capturable.");
        }

        var baseline = change.ByteChanges
            .OrderBy(item => item.Offset)
            .Select(item => $"{item.Offset.ToString("X4", CultureInfo.InvariantCulture)}:{item.PreviousValue.ToString("X2", CultureInfo.InvariantCulture)}");
        var active = change.ByteChanges
            .OrderBy(item => item.Offset)
            .Select(item => $"{item.Offset.ToString("X4", CultureInfo.InvariantCulture)}:{item.CurrentValue.ToString("X2", CultureInfo.InvariantCulture)}");
        return $"OPAQUE-HID-ACTIVATION-V1;BASE={string.Join(',', baseline)};ACTIVE={string.Join(',', active)}";
    }
}
