using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace KeyPilot.Platform.Windows.Audio;

/// <summary>
/// Reads RC003 battery percent from the standard BLE Battery Service. Fail-closed: missing
/// service or characteristic leaves Percent null and does not throw to the UI.
/// </summary>
public sealed class Rc003BatteryReader : IDisposable
{
    public static readonly Guid BatteryService = Guid.Parse("0000180F-0000-1000-8000-00805F9B34FB");
    public static readonly Guid BatteryLevel = Guid.Parse("00002A19-0000-1000-8000-00805F9B34FB");

    private readonly object _gate = new();
    private CancellationTokenSource? _run;
    private Task? _task;
    private bool _disposed;

    public event Action<byte?>? BatteryChanged;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _task is { IsCompleted: false };
            }
        }
    }

    public byte? Percent { get; private set; }

    public string Status { get; private set; } = "未启动";

    public async Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Stop();
        _run = new CancellationTokenSource();
        Status = "正在读取遥控器电量…";
        _task = RunAsync(_run.Token);
        await Task.CompletedTask;
    }

    public void Stop()
    {
        _run?.Cancel();
        try
        {
            _task?.GetAwaiter().GetResult();
        }
        catch (Exception)
        {
        }

        _run?.Dispose();
        _run = null;
        _task = null;
        Percent = null;
        Status = "已停止";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }

    public static byte? ParseLevel(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
        {
            return null;
        }

        var percent = value[0];
        return percent > 100 ? null : percent;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        BluetoothLEDevice? device = null;
        GattDeviceService? service = null;
        try
        {
            device = await Rc003BleDevice.OpenPairedAsync(cancellationToken).ConfigureAwait(false);
            var services = await device.GetGattServicesForUuidAsync(BatteryService)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
            if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
            {
                Status = "遥控器没有电量服务";
                Publish(null);
                await WaitUntilCancelled(cancellationToken).ConfigureAwait(false);
                return;
            }

            service = services.Services[0];
            var characteristics = await service.GetCharacteristicsForUuidAsync(BatteryLevel)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
            if (characteristics.Status != GattCommunicationStatus.Success ||
                characteristics.Characteristics.Count == 0)
            {
                Status = "遥控器没有电量特征";
                Publish(null);
                await WaitUntilCancelled(cancellationToken).ConfigureAwait(false);
                return;
            }

            var characteristic = characteristics.Characteristics[0];
            var subscribed = false;
            characteristic.ValueChanged += (_, args) => Publish(ParseLevel(ReadBuffer(args.CharacteristicValue)));
            try
            {
                var notify = await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.Notify)
                    .AsTask(cancellationToken)
                    .ConfigureAwait(false);
                subscribed = notify == GattCommunicationStatus.Success;
            }
            catch (Exception)
            {
                subscribed = false;
            }

            await ReadLevelAsync(characteristic, cancellationToken).ConfigureAwait(false);
            Status = Percent is { } percent ? $"电量 {percent}%" : "电量未知";

            if (subscribed)
            {
                await WaitUntilCancelled(cancellationToken).ConfigureAwait(false);
                return;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);
                await ReadLevelAsync(characteristic, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            Status = "已停止";
        }
        catch (Exception exception)
        {
            Status = exception.Message;
            Publish(null);
        }
        finally
        {
            service?.Dispose();
            device?.Dispose();
        }
    }

    private async Task ReadLevelAsync(GattCharacteristic characteristic, CancellationToken cancellationToken)
    {
        var result = await characteristic.ReadValueAsync().AsTask(cancellationToken).ConfigureAwait(false);
        if (result.Status != GattCommunicationStatus.Success)
        {
            return;
        }

        Publish(ParseLevel(ReadBuffer(result.Value)));
        if (Percent is { } percent)
        {
            Status = $"电量 {percent}%";
        }
    }

    private void Publish(byte? percent)
    {
        Percent = percent;
        BatteryChanged?.Invoke(percent);
    }

    private static async Task WaitUntilCancelled(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static byte[] ReadBuffer(IBuffer buffer)
    {
        var reader = DataReader.FromBuffer(buffer);
        var data = new byte[buffer.Length];
        reader.ReadBytes(data);
        return data;
    }
}
