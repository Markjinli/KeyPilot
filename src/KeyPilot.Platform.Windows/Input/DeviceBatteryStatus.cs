namespace KeyPilot.Platform.Windows.Input;

public enum DeviceBatteryTone
{
    Unknown,
    Ok,
    Low,
    Critical
}

public static class DeviceBatteryStatus
{
    public static string Format(string connectedLabel, byte? percent, bool? charging)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectedLabel);
        if (percent is null)
        {
            return connectedLabel;
        }

        return charging == true
            ? $"{connectedLabel} · 充电 {percent}%"
            : $"{connectedLabel} · {percent}%";
    }

    public static DeviceBatteryTone Tone(byte? percent) => percent switch
    {
        null => DeviceBatteryTone.Unknown,
        < 15 => DeviceBatteryTone.Critical,
        < 50 => DeviceBatteryTone.Low,
        _ => DeviceBatteryTone.Ok
    };
}
