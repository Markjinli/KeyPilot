using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace KeyPilot.Platform.Windows.Audio;

/// <summary>
/// Plays RC003 ATVV PCM into VB-CABLE Input so CABLE Output becomes a Windows recording device.
/// Audio only flows while the remote's physical mic button is held (firmware press-to-talk).
/// DualSense USB microphones are already Core Audio capture endpoints and do not use this bridge.
/// </summary>
public sealed class Rc003VoiceBridge : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _run;
    private Task? _task;
    private WasapiPcmPlayer? _player;
    private bool _disposed;

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

    public string Status { get; private set; } = "未启动";

    public async Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Stop();
        var render = WindowsAudioCaptureCatalog.FindCableInput(WindowsAudioCaptureCatalog.ListActiveRender())
            ?? throw new InvalidOperationException(
                "没有虚拟声卡。请安装 VB-CABLE，小米遥控器 2 Pro 的麦克风才能出现在 Windows 录音设备里。");
        _player = new WasapiPcmPlayer(render.Id, AtvvProtocol.SampleRateHz, 1);
        _run = new CancellationTokenSource();
        Status = "正在连接小米遥控器…";
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
        _player?.Dispose();
        _player = null;
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

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        BluetoothLEDevice? device = null;
        GattDeviceService? service = null;
        GattCharacteristic? tx = null;
        GattCharacteristic? audio = null;
        GattCharacteristic? control = null;
        var session = new AtvvSession();
        try
        {
            device = await Rc003BleDevice.OpenPairedAsync(cancellationToken).ConfigureAwait(false);
            Status = "已打开蓝牙，正在订阅 ATVV…";
            var services = await device.GetGattServicesForUuidAsync(AtvvProtocol.VoiceService)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
            if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
            {
                throw new InvalidOperationException("配对的遥控器没有 ATVV 语音服务。");
            }

            service = services.Services[0];
            tx = await GetCharacteristicAsync(service, AtvvProtocol.VoiceTx, cancellationToken)
                .ConfigureAwait(false);
            audio = await GetCharacteristicAsync(service, AtvvProtocol.VoiceAudio, cancellationToken)
                .ConfigureAwait(false);
            control = await GetCharacteristicAsync(service, AtvvProtocol.VoiceControl, cancellationToken)
                .ConfigureAwait(false);

            audio.ValueChanged += (_, args) =>
            {
                var samples = session.HandleAudio(ReadBuffer(args.CharacteristicValue));
                if (samples.Length > 0)
                {
                    _player?.Write(samples);
                }
            };
            control.ValueChanged += (_, args) =>
            {
                var evt = session.HandleControl(ReadBuffer(args.CharacteristicValue));
                if (evt == AtvvControlEvent.MicButton)
                {
#pragma warning disable CS4014
                    WriteGattAsync(tx, session.MicOpenCommand());
#pragma warning restore CS4014
                    Status = "按住语音键，正在开麦…";
                    return;
                }

                if (evt == AtvvControlEvent.AudioStart)
                {
                    _player?.Clear();
                    Status = "正在收音。松开语音键结束。";
                    return;
                }

                if (evt == AtvvControlEvent.AudioStop)
                {
                    Status = "语音已接通。按住遥控器语音键说话。";
                }
            };

            ThrowGatt(await audio.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify)
                .AsTask(cancellationToken)
                .ConfigureAwait(false));
            ThrowGatt(await control.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify)
                .AsTask(cancellationToken)
                .ConfigureAwait(false));
            ThrowGatt(await WriteGattAsync(tx, AtvvProtocol.GetCapabilitiesV10).ConfigureAwait(false));
            Status = "语音已接通。按住遥控器语音键说话。";

            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            if (session.MicOpen)
            {
                _ = await WriteGattAsync(tx, session.MicCloseCommand()).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            Status = "已停止";
        }
        catch (Exception exception)
        {
            Status = exception.Message;
            throw;
        }
        finally
        {
            service?.Dispose();
            device?.Dispose();
        }
    }

    private static async Task<GattCharacteristic> GetCharacteristicAsync(
        GattDeviceService service,
        Guid uuid,
        CancellationToken cancellationToken)
    {
        var result = await service.GetCharacteristicsForUuidAsync(uuid)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        if (result.Status != GattCommunicationStatus.Success || result.Characteristics.Count == 0)
        {
            throw new InvalidOperationException($"遥控器缺少 ATVV 特征 {uuid}。");
        }

        return result.Characteristics[0];
    }

    private static async Task<GattCommunicationStatus> WriteGattAsync(GattCharacteristic characteristic, byte[] value)
    {
        var writer = new DataWriter();
        writer.WriteBytes(value);
        var buffer = writer.DetachBuffer();
        var result = await characteristic.WriteValueWithResultAsync(buffer);
        return result.Status;
    }

    private static byte[] ReadBuffer(IBuffer buffer)
    {
        var reader = DataReader.FromBuffer(buffer);
        var data = new byte[buffer.Length];
        reader.ReadBytes(data);
        return data;
    }

    private static void ThrowGatt(GattCommunicationStatus status)
    {
        if (status != GattCommunicationStatus.Success)
        {
            throw new InvalidOperationException($"ATVV GATT 失败：{status}");
        }
    }
}
