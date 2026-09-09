using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace KeyPilot.Platform.Windows.Input;

/// <summary>
/// Uses one HWND and one Raw Input callback for keyboard, mouse buttons, plus read-only HID capture.
/// It never uses RIDEV_NOLEGACY and never sends HID output, so this discovery host cannot suppress
/// original input.
/// </summary>
public sealed class RawInputKeyboardSource : IDisposable
{
    private const uint WmNcDestroy = 0x0082;
    private const uint WmInput = 0x00FF;
    private const uint WmInputDeviceChange = 0x00FE;
    private const uint RidInput = 0x10000003;
    private const uint RidiDeviceName = 0x20000007;
    private const uint RidiDeviceInfo = 0x2000000B;
    private const uint RidevRemove = 0x00000001;
    private const uint RidevInputSink = 0x00000100;
    private const uint RidevDevNotify = 0x00002000;
    private const uint RimTypeMouse = 0;
    private const uint RimTypeKeyboard = 1;
    private const uint RimTypeHid = 2;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorDeviceNotConnected = 1167;
    private const int MaximumEnumeratedDeviceCount = 16_384;

    private static readonly SubclassProc SharedSubclassProc = WindowSubclass;

    private readonly nint _windowHandle;
    private readonly uint _ownerThreadId;
    private readonly nuint _subclassId;
    private readonly Dictionary<nint, string> _devicePathCache = new();
    private readonly Dictionary<nint, RawHidDeviceDescriptor> _hidDeviceCache = new();
    private readonly List<RawInputUsageRegistration> _registeredUsages = new();
    private GCHandle _selfHandle;
    private bool _subclassAttached;
    private bool _disposeRequested;

    public RawInputKeyboardSource(nint windowHandle)
    {
        if (windowHandle == 0)
        {
            throw new ArgumentException("A valid top-level window handle is required.", nameof(windowHandle));
        }

        _windowHandle = windowHandle;
        _ownerThreadId = GetCurrentThreadId();
        _selfHandle = GCHandle.Alloc(this, GCHandleType.Normal);
        _subclassId = unchecked((nuint)GCHandle.ToIntPtr(_selfHandle));

        if (!SetWindowSubclass(_windowHandle, SharedSubclassProc, _subclassId, _subclassId))
        {
            ReleaseSelfHandle();
            throw new InvalidOperationException("Unable to attach the Raw Input window subclass.");
        }

        _subclassAttached = true;
        try
        {
            RefreshDeviceDescriptionsAndRegistrations();
        }
        catch
        {
            _disposeRequested = true;
            TryRollbackConstruction();
            throw;
        }
    }

    public event EventHandler<RawKeyboardEvent>? InputReceived;

    public event EventHandler<RawMouseEvent>? MouseButtonReceived;

    public event EventHandler<RawHidReportBatch>? HidReportsReceived;

    public event EventHandler? DevicesChanged;

    public event EventHandler<Exception>? CaptureFaulted;

    /// <summary>Returns a snapshot of the currently enumerated HID top-level collections.</summary>
    public IReadOnlyList<RawHidDeviceDescriptor> HidDevices =>
        _hidDeviceCache.Values
            .OrderBy(device => device.DevicePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(device => device.UsagePage)
            .ThenBy(device => device.Usage)
            .ToArray();

    public bool HasDevicePath(Func<string, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        foreach (var device in EnumerateRawInputDevices())
        {
            if (device.Device == 0)
            {
                continue;
            }

            try
            {
                var path = GetDevicePath(device.Device);
                if (!string.IsNullOrWhiteSpace(path) && predicate(path))
                {
                    return true;
                }
            }
            catch (Exception)
            {
            }
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposeRequested && !_subclassAttached && _registeredUsages.Count == 0)
        {
            return;
        }

        RawInputKeyboardGuards.EnsureOwnerThread(_ownerThreadId, GetCurrentThreadId());
        _disposeRequested = true;

        if (_registeredUsages.Count > 0)
        {
            try
            {
                UnregisterUsages(_registeredUsages.ToArray());
                _registeredUsages.Clear();
            }
            catch (Exception exception)
            {
                ReportCaptureFault(exception);
            }
        }

        // Keep the callback and its GCHandle root alive if unregistration failed. This permits a
        // later Dispose retry and guarantees WM_NCDESTROY can finish cleanup safely.
        if (_registeredUsages.Count == 0 && _subclassAttached)
        {
            if (RemoveWindowSubclass(_windowHandle, SharedSubclassProc, _subclassId))
            {
                _subclassAttached = false;
                ReleaseSelfHandle();
            }
            else
            {
                ReportCaptureFault(
                    new InvalidOperationException(
                        "RemoveWindowSubclass failed; the callback will remain rooted until WM_NCDESTROY."));
            }
        }

        ClearDeviceCaches();
        GC.KeepAlive(this);
    }

    private static nint WindowSubclass(
        nint window,
        uint message,
        nuint wParam,
        nint lParam,
        nuint subclassId,
        nuint referenceData)
    {
        RawInputKeyboardSource? source = null;
        try
        {
            if (referenceData != 0)
            {
                var handle = GCHandle.FromIntPtr(unchecked((nint)referenceData));
                source = handle.Target as RawInputKeyboardSource;
            }

            source?.ProcessWindowMessage(message, lParam);
        }
        catch (Exception exception)
        {
            source?.ReportCaptureFault(exception);
        }

        nint result;
        try
        {
            result = DefSubclassProc(window, message, wParam, lParam);
        }
        catch (Exception exception)
        {
            source?.ReportCaptureFault(exception);
            result = 0;
        }

        if (message == WmNcDestroy && source is not null)
        {
            try
            {
                source.CompleteWindowDestruction();
            }
            catch (Exception exception)
            {
                source.ReportCaptureFault(exception);
            }
        }

        GC.KeepAlive(source);
        return result;
    }

    private void ProcessWindowMessage(uint message, nint lParam)
    {
        if (message == WmNcDestroy)
        {
            PrepareForWindowDestruction();
            return;
        }

        if (_disposeRequested)
        {
            return;
        }

        if (message == WmInput)
        {
            ReadRawInputPacket(lParam);
        }
        else if (message == WmInputDeviceChange)
        {
            RefreshDeviceDescriptionsAndRegistrations();
            DevicesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void PrepareForWindowDestruction()
    {
        _disposeRequested = true;

        if (_registeredUsages.Count > 0)
        {
            try
            {
                UnregisterUsages(_registeredUsages.ToArray());
                _registeredUsages.Clear();
            }
            catch (Exception exception)
            {
                ReportCaptureFault(exception);
            }
        }

        if (_subclassAttached)
        {
            if (!RemoveWindowSubclass(_windowHandle, SharedSubclassProc, _subclassId))
            {
                ReportCaptureFault(
                    new InvalidOperationException("RemoveWindowSubclass failed during WM_NCDESTROY."));
            }

            // WM_NCDESTROY is final even when the helper cannot explicitly remove itself.
            _subclassAttached = false;
        }

        ClearDeviceCaches();
    }

    private void CompleteWindowDestruction()
    {
        _disposeRequested = true;
        _subclassAttached = false;
        _registeredUsages.Clear();
        ClearDeviceCaches();
        ReleaseSelfHandle();
    }

    private void TryRollbackConstruction()
    {
        if (_registeredUsages.Count > 0)
        {
            try
            {
                UnregisterUsages(_registeredUsages.ToArray());
                _registeredUsages.Clear();
            }
            catch
            {
                // If removal fails, the callback must remain rooted until its HWND is destroyed.
            }
        }

        if (_registeredUsages.Count == 0 && _subclassAttached &&
            RemoveWindowSubclass(_windowHandle, SharedSubclassProc, _subclassId))
        {
            _subclassAttached = false;
            ReleaseSelfHandle();
        }
    }

    private void ReportCaptureFault(Exception exception) =>
        RawInputKeyboardGuards.InvokeCaptureFaultedSafely(this, CaptureFaulted, exception);

    private void ReleaseSelfHandle()
    {
        if (_selfHandle.IsAllocated)
        {
            _selfHandle.Free();
        }
    }

    private void ReadRawInputPacket(nint rawInputHandle)
    {
        uint size = 0;
        var headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
        var sizeResult = GetRawInputData(rawInputHandle, RidInput, 0, ref size, headerSize);
        if (sizeResult == uint.MaxValue)
        {
            throw NativeFailure("GetRawInputData(size query)");
        }

        if (sizeResult != 0)
        {
            throw new InvalidDataException(
                $"GetRawInputData returned {sizeResult} for a size-only query; zero was expected.");
        }

        RawInputKeyboardGuards.ValidateRequestedSize(size, headerSize);
        var buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            var reportedSize = size;
            var copied = GetRawInputData(rawInputHandle, RidInput, buffer, ref reportedSize, headerSize);
            if (copied == uint.MaxValue)
            {
                throw NativeFailure("GetRawInputData(packet copy)");
            }

            RawInputKeyboardGuards.ValidateCopiedPacket(size, reportedSize, copied, headerSize);

            var header = Marshal.PtrToStructure<RawInputHeader>(buffer);
            var payloadSize = header.Type switch
            {
                RimTypeMouse => (uint)RawInputMouseGuards.RawMouseStructSize,
                RimTypeKeyboard => (uint)Marshal.SizeOf<RawKeyboard>(),
                RimTypeHid => (uint)(sizeof(uint) * 2),
                _ => 0U
            };
            RawInputKeyboardGuards.ValidatePacketLayout(
                copied,
                header.Size,
                headerSize,
                payloadSize);

            switch (header.Type)
            {
                case RimTypeMouse:
                    PublishMousePacket(buffer, header);
                    break;
                case RimTypeKeyboard:
                    PublishKeyboardPacket(buffer, header);
                    break;
                case RimTypeHid:
                    PublishHidPacket(buffer, copied, headerSize, header);
                    break;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void PublishMousePacket(nint buffer, RawInputHeader header)
    {
        if (header.Device == 0)
        {
            return;
        }

        var dataPointer = IntPtr.Add(buffer, Marshal.SizeOf<RawInputHeader>());
        var mouse = Marshal.PtrToStructure<RawInputMouseGuards.RawMouseLayout>(dataPointer);
        var buttons = RawInputMouseGuards.TranslateButtonFlags(mouse.usButtonFlags);
        if (buttons.Count == 0)
        {
            return;
        }

        var path = GetDevicePath(header.Device);
        var timestamp = DateTimeOffset.UtcNow;
        foreach (var (virtualKey, isPressed) in buttons)
        {
            MouseButtonReceived?.Invoke(
                this,
                new RawMouseEvent(
                    header.Device,
                    path,
                    mouse.usButtonFlags,
                    virtualKey,
                    isPressed,
                    mouse.ulExtraInformation,
                    timestamp));
        }
    }

    private void PublishKeyboardPacket(nint buffer, RawInputHeader header)
    {
        var dataPointer = IntPtr.Add(buffer, Marshal.SizeOf<RawInputHeader>());
        var keyboard = Marshal.PtrToStructure<RawKeyboard>(dataPointer);
        if (keyboard.Reserved != 0)
        {
            throw new InvalidDataException("RAWKEYBOARD.Reserved must be zero.");
        }

        if (!RawInputKeyboardGuards.ShouldPublishKeyboardPacket(
                keyboard.MakeCode,
                keyboard.VirtualKey))
        {
            return;
        }

        InputReceived?.Invoke(
            this,
            RawInputKeyboardGuards.CreateKeyboardEvent(
                header.Device,
                keyboard.MakeCode,
                keyboard.VirtualKey,
                keyboard.Flags,
                keyboard.Message,
                keyboard.ExtraInformation,
                DateTimeOffset.UtcNow,
                GetDevicePath));
    }

    private void PublishHidPacket(
        nint buffer,
        uint copiedSize,
        uint headerSize,
        RawInputHeader header)
    {
        RawInputKeyboardGuards.RequirePhysicalDeviceHandle(header.Device);

        var headerOffset = checked((int)headerSize);
        var reportLength = unchecked((uint)Marshal.ReadInt32(buffer, headerOffset));
        var reportCount = unchecked((uint)Marshal.ReadInt32(buffer, headerOffset + sizeof(uint)));
        var layout = RawInputHidGuards.ValidateHidBatchLayout(
            copiedSize,
            header.Size,
            headerSize,
            reportLength,
            reportCount);

        var reports = new byte[layout.ReportCount][];
        for (var index = 0; index < reports.Length; index++)
        {
            var report = new byte[layout.ReportLength];
            var offset = checked(layout.DataOffset + (index * layout.ReportLength));
            Marshal.Copy(IntPtr.Add(buffer, offset), report, 0, report.Length);
            reports[index] = report;
        }

        var descriptor = GetHidDeviceDescriptor(header.Device);
        HidReportsReceived?.Invoke(
            this,
            new RawHidReportBatch(
                descriptor,
                layout.ReportLength,
                reports,
                DateTimeOffset.UtcNow));
    }

    private void RefreshDeviceDescriptionsAndRegistrations()
    {
        ClearDeviceCaches();
        var descriptors = EnumerateHidDevices();
        var desiredRegistrations = RawInputHidGuards.SelectCaptureRegistrations(
            descriptors.Select(device =>
                new RawInputUsageRegistration(device.UsagePage, device.Usage)));

        ApplyRegistrationSet(desiredRegistrations);

        foreach (var descriptor in descriptors)
        {
            _devicePathCache[descriptor.DeviceHandle] = descriptor.DevicePath;
            _hidDeviceCache[descriptor.DeviceHandle] = descriptor;
        }
    }

    private IReadOnlyList<RawHidDeviceDescriptor> EnumerateHidDevices()
    {
        var devices = EnumerateRawInputDevices();
        var descriptors = new List<RawHidDeviceDescriptor>();
        foreach (var device in devices)
        {
            if (device.Type != RimTypeHid || device.Device == 0)
            {
                continue;
            }

            try
            {
                descriptors.Add(ReadHidDeviceDescriptor(device.Device));
            }
            catch (Win32Exception exception)
                when (exception.NativeErrorCode is ErrorDeviceNotConnected or 6)
            {
                // A device can disappear between the list and detail calls. The next device
                // notification/refresh will observe the stable set.
            }
        }

        return descriptors;
    }

    private static RawInputDeviceListEntry[] EnumerateRawInputDevices()
    {
        var itemSize = (uint)Marshal.SizeOf<RawInputDeviceListEntry>();
        for (var attempt = 0; attempt < 8; attempt++)
        {
            uint deviceCount = 0;
            var queryResult = GetRawInputDeviceList(null, ref deviceCount, itemSize);
            if (queryResult == uint.MaxValue)
            {
                throw NativeFailure("GetRawInputDeviceList(count query)");
            }

            if (queryResult != 0)
            {
                throw new InvalidDataException(
                    $"GetRawInputDeviceList returned {queryResult} for a count-only query; zero was expected.");
            }

            if (deviceCount == 0)
            {
                return Array.Empty<RawInputDeviceListEntry>();
            }

            if (deviceCount > MaximumEnumeratedDeviceCount)
            {
                throw new InvalidDataException(
                    $"Raw Input reported {deviceCount} devices; the safety limit is {MaximumEnumeratedDeviceCount}.");
            }

            var devices = new RawInputDeviceListEntry[checked((int)deviceCount)];
            var capacity = deviceCount;
            var copied = GetRawInputDeviceList(devices, ref capacity, itemSize);
            if (copied == uint.MaxValue)
            {
                if (Marshal.GetLastPInvokeError() == ErrorInsufficientBuffer)
                {
                    continue;
                }

                throw NativeFailure("GetRawInputDeviceList(device copy)");
            }

            if (copied > devices.Length || capacity > devices.Length)
            {
                continue;
            }

            return devices.Take(checked((int)copied)).ToArray();
        }

        throw new InvalidOperationException("Raw Input device enumeration did not stabilize after eight attempts.");
    }

    private RawHidDeviceDescriptor GetHidDeviceDescriptor(nint device)
    {
        RawInputKeyboardGuards.RequirePhysicalDeviceHandle(device);
        if (_hidDeviceCache.TryGetValue(device, out var cached))
        {
            return cached;
        }

        var descriptor = ReadHidDeviceDescriptor(device);
        _devicePathCache[device] = descriptor.DevicePath;
        _hidDeviceCache[device] = descriptor;
        return descriptor;
    }

    private RawHidDeviceDescriptor ReadHidDeviceDescriptor(nint device)
    {
        var infoSize = (uint)Marshal.SizeOf<RidDeviceInfo>();
        var info = new RidDeviceInfo { CbSize = infoSize };
        var copiedSize = infoSize;
        var result = GetRawInputDeviceInfo(device, RidiDeviceInfo, ref info, ref copiedSize);
        if (result == uint.MaxValue)
        {
            throw NativeFailure("GetRawInputDeviceInfo(HID device info)");
        }

        if (info.Type != RimTypeHid || result < 24 || copiedSize < 24)
        {
            throw new InvalidDataException(
                $"Raw Input device 0x{unchecked((nuint)device):X} did not return a complete RID_DEVICE_INFO_HID.");
        }

        return new RawHidDeviceDescriptor(
            device,
            GetDevicePath(device),
            info.Data.Hid.VendorId,
            info.Data.Hid.ProductId,
            info.Data.Hid.VersionNumber,
            info.Data.Hid.UsagePage,
            info.Data.Hid.Usage);
    }

    private string GetDevicePath(nint device)
    {
        RawInputKeyboardGuards.RequirePhysicalDeviceHandle(device);
        if (_devicePathCache.TryGetValue(device, out var cached))
        {
            return cached;
        }

        uint characterCount = 0;
        var sizeResult = GetRawInputDeviceName(device, RidiDeviceName, null, ref characterCount);
        if (sizeResult == uint.MaxValue)
        {
            throw NativeFailure("GetRawInputDeviceInfo(device path size query)");
        }

        if (sizeResult != 0 || characterCount == 0 ||
            characterCount > UnknownHidReportIdentity.MaximumDevicePathLength)
        {
            throw new InvalidDataException(
                $"Raw Input device 0x{unchecked((nuint)device):X} reported an invalid device path size.");
        }

        var name = new StringBuilder(checked((int)characterCount));
        var returned = GetRawInputDeviceName(device, RidiDeviceName, name, ref characterCount);
        if (returned == uint.MaxValue)
        {
            throw NativeFailure("GetRawInputDeviceInfo(device path)");
        }

        if (returned == 0 || returned > characterCount)
        {
            throw new InvalidDataException(
                $"Raw Input device 0x{unchecked((nuint)device):X} returned an invalid device path length.");
        }

        var value = RawInputKeyboardGuards.RequireStableDevicePath(device, name.ToString());
        _devicePathCache[device] = value;
        return value;
    }

    private void ApplyRegistrationSet(IReadOnlyList<RawInputUsageRegistration> desired)
    {
        var desiredSet = desired.ToHashSet();
        var obsolete = _registeredUsages.Where(usage => !desiredSet.Contains(usage)).ToArray();
        if (obsolete.Length > 0)
        {
            UnregisterUsages(obsolete);
            _registeredUsages.RemoveAll(usage => obsolete.Contains(usage));
        }

        var alreadyRegistered = _registeredUsages.ToHashSet();
        foreach (var registration in desired.Where(usage => !alreadyRegistered.Contains(usage)))
        {
            try
            {
                // Register each optional vendor TLC independently. One malformed or newly
                // rejected OEM collection must never take down keyboard/consumer capture.
                RegisterUsages(
                    [registration],
                    RidevInputSink | RidevDevNotify,
                    _windowHandle);
                _registeredUsages.Add(registration);
            }
            catch (Win32Exception exception)
                when (exception.NativeErrorCode == ErrorInvalidParameter &&
                      RawInputHidGuards.CanSkipRejectedOptionalRegistration(
                          registration,
                          exception.NativeErrorCode))
            {
                // Exact vendor TLC capture is optional. Usage-zero collections are filtered
                // before this point; Windows may still reject an OEM-specific pair.
            }
        }
    }

    private static void RegisterUsages(
        IReadOnlyList<RawInputUsageRegistration> usages,
        uint flags,
        nint target)
    {
        if (usages.Count == 0)
        {
            return;
        }

        RawInputHidGuards.ValidateRegistrationRequest(usages, flags, target);

        var devices = usages.Select(usage => new RawInputDevice
        {
            UsagePage = usage.UsagePage,
            Usage = usage.Usage,
            Flags = flags,
            Target = target
        }).ToArray();

        if (!RegisterRawInputDevices(
                devices,
                checked((uint)devices.Length),
                (uint)Marshal.SizeOf<RawInputDevice>()))
        {
            var usageList = string.Join(
                ", ",
                usages.Select(usage => $"{usage.UsagePage:X4}/{usage.Usage:X4}"));
            throw NativeFailure($"RegisterRawInputDevices(capture: {usageList}, flags=0x{flags:X8})");
        }
    }

    private static void UnregisterUsages(IReadOnlyList<RawInputUsageRegistration> usages) =>
        RegisterUsages(usages, RidevRemove, 0);

    private void ClearDeviceCaches()
    {
        _devicePathCache.Clear();
        _hidDeviceCache.Clear();
    }

    private static Win32Exception NativeFailure(string operation) =>
        new(Marshal.GetLastPInvokeError(), $"{operation} failed.");

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public nint Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDeviceListEntry
    {
        public nint Device;
        public uint Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public nint Device;
        public nuint WParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawKeyboard
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VirtualKey;
        public uint Message;
        public uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RidDeviceInfo
    {
        public uint CbSize;
        public uint Type;
        public RidDeviceInfoUnion Data;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct RidDeviceInfoUnion
    {
        [FieldOffset(0)]
        public RidDeviceInfoHid Hid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RidDeviceInfoHid
    {
        public uint VendorId;
        public uint ProductId;
        public uint VersionNumber;
        public ushort UsagePage;
        public ushort Usage;
    }

    private delegate nint SubclassProc(
        nint window,
        uint message,
        nuint wParam,
        nint lParam,
        nuint subclassId,
        nuint referenceData);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(
        [In] RawInputDevice[] devices,
        uint deviceCount,
        uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(
        nint rawInput,
        uint command,
        nint data,
        ref uint size,
        uint headerSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceList(
        [Out] RawInputDeviceListEntry[]? devices,
        ref uint deviceCount,
        uint size);

    [DllImport(
        "user32.dll",
        EntryPoint = "GetRawInputDeviceInfoW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern uint GetRawInputDeviceName(
        nint device,
        uint command,
        StringBuilder? data,
        ref uint size);

    [DllImport(
        "user32.dll",
        EntryPoint = "GetRawInputDeviceInfoW",
        SetLastError = true)]
    private static extern uint GetRawInputDeviceInfo(
        nint device,
        uint command,
        ref RidDeviceInfo data,
        ref uint size);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        nint window,
        SubclassProc callback,
        nuint subclassId,
        nuint referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        nint window,
        SubclassProc callback,
        nuint subclassId);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
