using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace KeyPilot.Platform.Windows.Audio;

/// <summary>Shared-mode WASAPI renderer with AutoConvertPCM so 16 kHz mono ATVV can feed CABLE Input.</summary>
public sealed class WasapiPcmPlayer : IDisposable
{
    private const uint StreamFlagsAutoConvertPcm = 0x80000000;
    private const uint StreamFlagsSrcDefaultQuality = 0x08000000;
    private const uint ClsContextAll = 23;
    private static readonly Guid AudioClientInterfaceId = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid AudioRenderClientInterfaceId = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    private static readonly Guid DeviceEnumeratorClassId = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    private readonly ConcurrentQueue<short[]> _queue = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Thread _thread;
    private bool _disposed;

    public WasapiPcmPlayer(string endpointId, int sampleRateHz, int channels)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        _thread = new Thread(() => Pump(endpointId, sampleRateHz, channels, _lifetime.Token))
        {
            IsBackground = true,
            Name = "KeyPilot WASAPI PCM"
        };
        _thread.Start();
    }

    public void Write(short[] samples)
    {
        if (samples.Length == 0 || _disposed)
        {
            return;
        }

        // Keep about two seconds of 15 ms ATVV frames. Drop only a runaway backlog.
        while (_queue.Count > 128)
        {
            _ = _queue.TryDequeue(out _);
        }

        _queue.Enqueue(samples);
    }

    public void Clear()
    {
        while (_queue.TryDequeue(out _))
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        _thread.Join(TimeSpan.FromSeconds(2));
        _lifetime.Dispose();
    }

    private void Pump(string endpointId, int sampleRateHz, int channels, CancellationToken cancellationToken)
    {
        object? enumeratorObject = null;
        IMMDevice? device = null;
        object? clientObject = null;
        object? renderObject = null;
        try
        {
            var enumeratorType = Type.GetTypeFromCLSID(DeviceEnumeratorClassId, throwOnError: true)!;
            enumeratorObject = Activator.CreateInstance(enumeratorType)!;
            var enumerator = (IMMDeviceEnumerator)enumeratorObject;
            Throw(enumerator.GetDevice(endpointId, out device), "找不到选定的播放端点。");
            var audioClientId = AudioClientInterfaceId;
            Throw(
                device.Activate(ref audioClientId, ClsContextAll, IntPtr.Zero, out clientObject),
                "播放端点不支持 WASAPI。");
            var client = (IAudioClient)clientObject;
            var format = new WaveFormatEx
            {
                FormatTag = 1,
                Channels = (ushort)channels,
                SamplesPerSecond = (uint)sampleRateHz,
                BitsPerSample = 16,
                BlockAlign = (ushort)(channels * 2),
                AverageBytesPerSecond = (uint)(sampleRateHz * channels * 2)
            };
            Throw(
                client.Initialize(
                    0,
                    StreamFlagsAutoConvertPcm | StreamFlagsSrcDefaultQuality,
                    2000000,
                    0,
                    ref format,
                    IntPtr.Zero),
                "无法以 16 kHz PCM 打开播放端点。");
            Throw(client.GetBufferSize(out var bufferFrames), "无法读取 WASAPI 缓冲区。");
            var renderId = AudioRenderClientInterfaceId;
            Throw(client.GetService(ref renderId, out renderObject), "播放端点没有渲染服务。");
            var render = (IAudioRenderClient)renderObject;
            Throw(client.Start(), "无法开始播放。");
            var blockAlign = format.BlockAlign;
            var pending = new Queue<short>();

            while (!cancellationToken.IsCancellationRequested)
            {
                while (_queue.TryDequeue(out var chunk))
                {
                    foreach (var sample in chunk)
                    {
                        pending.Enqueue(sample);
                    }
                }

                Throw(client.GetCurrentPadding(out var padding), "无法读取 WASAPI 填充。");
                var available = bufferFrames - padding;
                var frames = Math.Min(available, (uint)(pending.Count / channels));
                if (frames == 0)
                {
                    Thread.Sleep(5);
                    continue;
                }

                Throw(render.GetBuffer(frames, out var pointer), "无法取得 WASAPI 缓冲。");
                var bytes = (int)(frames * blockAlign);
                for (var offset = 0; offset < bytes; offset += 2)
                {
                    var sample = pending.Count > 0 ? pending.Dequeue() : (short)0;
                    Marshal.WriteInt16(pointer, offset, sample);
                }

                Throw(render.ReleaseBuffer(frames, 0), "无法提交 WASAPI 缓冲。");
            }

            _ = client.Stop();
        }
        catch (Exception)
        {
            // The UI reports missing CABLE Input separately; a render fault must not crash the app.
        }
        finally
        {
            Release(renderObject);
            Release(clientObject);
            Release(device);
            Release(enumeratorObject);
        }
    }

    private static void Throw(int result, string message)
    {
        if (result < 0)
        {
            throw new COMException(message, result);
        }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            _ = Marshal.FinalReleaseComObject(value);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSecond;
        public uint AverageBytesPerSecond;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort ExtraSize;
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int Unused1();
        [PreserveSig] int Unused2();
        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int Unused4();
        [PreserveSig] int Unused5();
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(
            ref Guid interfaceId,
            uint classContext,
            IntPtr activationParameters,
            [MarshalAs(UnmanagedType.IUnknown)] out object interfaceObject);
        [PreserveSig] int Unused2();
        [PreserveSig] int Unused3();
        [PreserveSig] int Unused4();
    }

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig]
        int Initialize(
            int shareMode,
            uint streamFlags,
            long bufferDuration,
            long periodicity,
            ref WaveFormatEx format,
            IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint bufferFrames);
        [PreserveSig] int UnusedGetStreamLatency();
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int UnusedIsFormatSupported();
        [PreserveSig] int UnusedGetMixFormat();
        [PreserveSig] int UnusedGetDevicePeriod();
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int UnusedReset();
        [PreserveSig] int UnusedSetEventHandle();
        [PreserveSig]
        int GetService(ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport]
    [Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(uint frames, out IntPtr data);
        [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
    }
}
