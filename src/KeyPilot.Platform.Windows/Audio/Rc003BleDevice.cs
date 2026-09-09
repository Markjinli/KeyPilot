using KeyPilot.Platform.Windows.Input;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace KeyPilot.Platform.Windows.Audio;

internal static class Rc003BleDevice
{
    public static async Task<BluetoothLEDevice> OpenPairedAsync(CancellationToken cancellationToken)
    {
        var selector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
        var found = await DeviceInformation.FindAllAsync(selector).AsTask(cancellationToken).ConfigureAwait(false);
        BluetoothLEDevice? match = null;
        foreach (var info in found)
        {
            if (!Rc003DeviceIdentity.IsAdvertisedName(info.Name))
            {
                continue;
            }

            var device = await BluetoothLEDevice.FromIdAsync(info.Id).AsTask(cancellationToken).ConfigureAwait(false);
            if (device is null)
            {
                continue;
            }

            if (match is not null)
            {
                device.Dispose();
                match.Dispose();
                throw new InvalidOperationException("找到多台已配对的小米遥控器，无法自动选择。请只保留一台配对。");
            }

            match = device;
        }

        return match ?? throw new InvalidOperationException(
            "没有已配对的小米遥控器 2 Pro。请先在 Windows 蓝牙设置里配对，名称需为 mi rc / 小米蓝牙语音遥控器。");
    }
}
