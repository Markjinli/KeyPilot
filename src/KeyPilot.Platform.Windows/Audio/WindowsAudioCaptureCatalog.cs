using System.Runtime.InteropServices;

namespace KeyPilot.Platform.Windows.Audio;

/// <summary>
/// Enumerates Windows capture endpoints and can set the default recording device.
/// DualSense USB headset microphones already appear here. The Xiaomi remote is listed as a
/// connected-device microphone; CABLE Output is only the endpoint used when ATVV needs a bridge.
/// </summary>
public static class WindowsAudioCaptureCatalog
{
    private const uint DeviceStateActive = 0x1;
    private const uint StorageAccessRead = 0;

    public static IReadOnlyList<WindowsCaptureEndpoint> ListActive()
    {
        object? enumeratorObject = null;
        var collection = IntPtr.Zero;
        try
        {
            var enumerator = CreateEnumerator(out enumeratorObject);
            ThrowForHResult(
                enumerator.EnumAudioEndpoints(EDataFlow.Capture, DeviceStateActive, out collection),
                "无法列出录音设备。");
            ThrowForHResult(CollectionGetCount(collection, out var count), "无法读取录音设备数量。");
            string? defaultMultimediaId = null;
            string? defaultCommunicationsId = null;
            if (enumerator.GetDefaultAudioEndpoint(EDataFlow.Capture, ERole.Multimedia, out var defaultMultimedia) >= 0)
            {
                defaultMultimediaId = ReadId(defaultMultimedia);
                Release(defaultMultimedia);
            }

            if (enumerator.GetDefaultAudioEndpoint(EDataFlow.Capture, ERole.Communications, out var defaultCommunications) >= 0)
            {
                defaultCommunicationsId = ReadId(defaultCommunications);
                Release(defaultCommunications);
            }

            var endpoints = new List<WindowsCaptureEndpoint>((int)count);
            for (uint index = 0; index < count; index++)
            {
                ThrowForHResult(CollectionItem(collection, index, out var devicePtr), "无法打开录音设备。");
                var device = WrapDevice(devicePtr);
                try
                {
                    ThrowForHResult(device.GetId(out var id), "录音设备缺少标识。");
                    var name = ReadFriendlyName(device) ?? id;
                    var instanceId = ReadInstanceId(device);
                    endpoints.Add(new WindowsCaptureEndpoint(
                        id,
                        name,
                        instanceId,
                        WindowsCaptureEndpointClassifier.Classify(name, instanceId),
                        string.Equals(id, defaultMultimediaId, StringComparison.Ordinal),
                        string.Equals(id, defaultCommunicationsId, StringComparison.Ordinal)));
                }
                finally
                {
                    Release(device);
                }
            }

            return endpoints;
        }
        finally
        {
            ReleasePtr(collection);
            Release(enumeratorObject);
        }
    }

    public static IReadOnlyList<WindowsRenderEndpoint> ListActiveRender()
    {
        object? enumeratorObject = null;
        var collection = IntPtr.Zero;
        try
        {
            var enumerator = CreateEnumerator(out enumeratorObject);
            ThrowForHResult(
                enumerator.EnumAudioEndpoints(EDataFlow.Render, DeviceStateActive, out collection),
                "无法列出播放设备。");
            ThrowForHResult(CollectionGetCount(collection, out var count), "无法读取播放设备数量。");
            var endpoints = new List<WindowsRenderEndpoint>((int)count);
            for (uint index = 0; index < count; index++)
            {
                ThrowForHResult(CollectionItem(collection, index, out var devicePtr), "无法打开播放设备。");
                var device = WrapDevice(devicePtr);
                try
                {
                    ThrowForHResult(device.GetId(out var id), "播放设备缺少标识。");
                    var name = ReadFriendlyName(device) ?? id;
                    endpoints.Add(new WindowsRenderEndpoint(id, name));
                }
                finally
                {
                    Release(device);
                }
            }

            return endpoints;
        }
        finally
        {
            ReleasePtr(collection);
            Release(enumeratorObject);
        }
    }

    public static void SetDefaultCapture(string endpointId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        var clientType = Type.GetTypeFromCLSID(PolicyConfigClientId, throwOnError: true)
            ?? throw new InvalidOperationException("Windows 音频策略客户端不可用。");
        var client = Activator.CreateInstance(clientType)
            ?? throw new InvalidOperationException("无法创建 Windows 音频策略客户端。");
        try
        {
            var policy = (IPolicyConfig)client;
            ThrowForHResult(policy.SetDefaultEndpoint(endpointId, ERole.Console), "无法设为默认控制台录音设备。");
            ThrowForHResult(policy.SetDefaultEndpoint(endpointId, ERole.Multimedia), "无法设为默认多媒体录音设备。");
            ThrowForHResult(policy.SetDefaultEndpoint(endpointId, ERole.Communications), "无法设为默认通讯录音设备。");
        }
        finally
        {
            Release(client);
        }
    }

    public static WindowsCaptureEndpoint? FindPreferredSonyPadMic(
        IReadOnlyList<WindowsCaptureEndpoint> endpoints) =>
        endpoints.FirstOrDefault(endpoint => endpoint.Kind == WindowsCaptureKind.DualSense)
        ?? endpoints.FirstOrDefault(endpoint => endpoint.Kind == WindowsCaptureKind.DualShockHeadset);

    public static WindowsCaptureEndpoint? FindCableOutput(
        IReadOnlyList<WindowsCaptureEndpoint> endpoints) =>
        endpoints.FirstOrDefault(endpoint => endpoint.Kind == WindowsCaptureKind.CableOutput);

    public static WindowsRenderEndpoint? FindCableInput(
        IReadOnlyList<WindowsRenderEndpoint> endpoints) =>
        endpoints.FirstOrDefault(endpoint => WindowsCaptureEndpointClassifier.IsCableInput(endpoint.Name));

    private static readonly Guid DeviceEnumeratorClassId = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid PolicyConfigClientId = new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");
    private static readonly PropertyKey FriendlyNameKey = new(
        new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        14);
    private static readonly PropertyKey InstanceIdKey = new(
        new Guid("78C34FC8-104A-4ACA-9EA4-524D52996E57"),
        256);

    private static IMMDeviceEnumerator CreateEnumerator(out object enumeratorObject)
    {
        var enumeratorType = Type.GetTypeFromCLSID(DeviceEnumeratorClassId, throwOnError: true)
            ?? throw new InvalidOperationException("Windows 多媒体设备枚举器不可用。");
        enumeratorObject = Activator.CreateInstance(enumeratorType)
            ?? throw new InvalidOperationException("无法创建 Windows 多媒体设备枚举器。");
        return (IMMDeviceEnumerator)enumeratorObject;
    }

    private static string? ReadId(IMMDevice? device)
    {
        if (device is null)
        {
            return null;
        }

        return device.GetId(out var id) >= 0 ? id : null;
    }

    private static string? ReadFriendlyName(IMMDevice device) => ReadPropertyString(device, FriendlyNameKey);

    private static string? ReadInstanceId(IMMDevice device) => ReadPropertyString(device, InstanceIdKey);

    private static string? ReadPropertyString(IMMDevice device, PropertyKey key)
    {
        if (device.OpenPropertyStore(StorageAccessRead, out var storePtr) < 0 || storePtr == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var variant = new PropVariant();
            if (PropertyStoreGetValue(storePtr, ref key, out variant) < 0)
            {
                return null;
            }

            try
            {
                return variant.Vt == 31 && variant.PointerValue != IntPtr.Zero
                    ? Marshal.PtrToStringUni(variant.PointerValue)
                    : null;
            }
            finally
            {
                PropVariantClear(ref variant);
            }
        }
        finally
        {
            ReleasePtr(storePtr);
        }
    }

    private static IMMDevice WrapDevice(IntPtr devicePtr)
    {
        if (devicePtr == IntPtr.Zero)
        {
            throw new COMException("录音设备句柄为空。", unchecked((int)0x80004003));
        }

        var device = (IMMDevice)Marshal.GetObjectForIUnknown(devicePtr);
        Marshal.Release(devicePtr);
        return device;
    }

    private static int CollectionGetCount(IntPtr collection, out uint count)
    {
        count = 0;
        if (collection == IntPtr.Zero)
        {
            return unchecked((int)0x80004003);
        }

        var fn = Marshal.GetDelegateForFunctionPointer<GetCountDelegate>(VTable(collection, 3));
        return fn(collection, out count);
    }

    private static int CollectionItem(IntPtr collection, uint index, out IntPtr device)
    {
        device = IntPtr.Zero;
        if (collection == IntPtr.Zero)
        {
            return unchecked((int)0x80004003);
        }

        var fn = Marshal.GetDelegateForFunctionPointer<ItemDelegate>(VTable(collection, 4));
        return fn(collection, index, out device);
    }

    private static int PropertyStoreGetValue(IntPtr store, ref PropertyKey key, out PropVariant value)
    {
        value = default;
        if (store == IntPtr.Zero)
        {
            return unchecked((int)0x80004003);
        }

        var fn = Marshal.GetDelegateForFunctionPointer<GetValueDelegate>(VTable(store, 5));
        return fn(store, ref key, out value);
    }

    private static IntPtr VTable(IntPtr comObject, int slot) =>
        Marshal.ReadIntPtr(Marshal.ReadIntPtr(comObject), slot * IntPtr.Size);

    private static void ThrowForHResult(int result, string message)
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

    private static void ReleasePtr(IntPtr value)
    {
        if (value != IntPtr.Zero)
        {
            Marshal.Release(value);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant variant);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetCountDelegate(IntPtr self, out uint count);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ItemDelegate(IntPtr self, uint index, out IntPtr device);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetValueDelegate(IntPtr self, ref PropertyKey key, out PropVariant value);

    private enum EDataFlow
    {
        Render,
        Capture,
        All
    }

    private enum ERole
    {
        Console,
        Multimedia,
        Communications
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey(Guid formatId, uint propertyId)
    {
        public readonly Guid FormatId = formatId;
        public readonly uint PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort Vt;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public IntPtr PointerValue;
        public IntPtr Extra;
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IntPtr devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IntPtr callback);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IntPtr callback);
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

        [PreserveSig]
        int OpenPropertyStore(uint accessMode, out IntPtr properties);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig]
        int GetState(out uint state);
    }

    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int Unused1();
        [PreserveSig] int Unused2();
        [PreserveSig] int Unused3();
        [PreserveSig] int Unused4();
        [PreserveSig] int Unused5();
        [PreserveSig] int Unused6();
        [PreserveSig] int Unused7();
        [PreserveSig] int Unused8();
        [PreserveSig] int Unused9();
        [PreserveSig] int Unused10();

        [PreserveSig]
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);

        [PreserveSig] int Unused12();
    }
}

public sealed record WindowsRenderEndpoint(string Id, string Name);
