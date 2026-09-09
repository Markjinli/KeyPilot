using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace KeyPilot.DriverClient;

/// <summary>
/// Resolves a Raw Input device-interface name to the same stable device hash
/// that the keyboard filter derives from DEVPKEY_Device_InstanceId.
/// </summary>
public static partial class KeyPilotDeviceHash
{
    private const uint CrSuccess = 0x00000000;
    private const uint CrBufferSmall = 0x0000001A;
    private const uint DevPropTypeString = 0x00000012;
    private const uint MaximumPropertyBytes = 64 * 1024;

    private static readonly DevPropKey DeviceInstanceIdKey = new(
        new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"),
        256);

    /// <summary>
    /// Resolves a RIDI_DEVICENAME device-interface path without ever widening
    /// a failed exact-device lookup to an all-keyboards rule.
    /// </summary>
    public static byte[] ResolveDeviceInterfaceHash(string deviceInterfacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceInterfacePath);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Keyboard device-interface resolution requires Windows.");
        }

        uint propertyType;
        uint byteCount = 0;
        var result = NativeMethods.CM_Get_Device_Interface_PropertyW(
            deviceInterfacePath,
            in DeviceInstanceIdKey,
            out propertyType,
            null,
            ref byteCount,
            0);
        if (result != CrBufferSmall && result != CrSuccess)
        {
            throw ResolutionException(deviceInterfacePath, result);
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            ValidatePropertySize(byteCount);
            var property = new byte[checked((int)byteCount)];
            var returnedBytes = byteCount;
            result = NativeMethods.CM_Get_Device_Interface_PropertyW(
                deviceInterfacePath,
                in DeviceInstanceIdKey,
                out propertyType,
                property,
                ref returnedBytes,
                0);
            if (result == CrBufferSmall)
            {
                byteCount = returnedBytes;
                continue;
            }
            if (result != CrSuccess)
            {
                throw ResolutionException(deviceInterfacePath, result);
            }
            if (propertyType != DevPropTypeString)
            {
                throw new InvalidDataException(
                    $"Device interface returned property type 0x{propertyType:X8}, not DEVPROP_TYPE_STRING.");
            }

            ValidatePropertySize(returnedBytes);
            if (returnedBytes > property.Length)
            {
                throw new InvalidDataException("Device interface returned more property data than requested.");
            }

            var bytes = property.AsSpan(0, checked((int)returnedBytes));
            ValidateTerminatedString(bytes);
            return ComputeHash(bytes);
        }

        throw new InvalidDataException("The device instance ID changed size repeatedly during resolution.");
    }

    /// <summary>
    /// Computes the protocol hash for a known device instance ID. The UTF-16
    /// terminating null is included because the kernel property buffer includes it.
    /// </summary>
    public static byte[] ComputeDeviceInstanceIdHash(string deviceInstanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceInstanceId);
        if (deviceInstanceId.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("A device instance ID cannot contain an embedded null.", nameof(deviceInstanceId));
        }

        return ComputeHash(Encoding.Unicode.GetBytes(deviceInstanceId + '\0'));
    }

    private static byte[] ComputeHash(ReadOnlySpan<byte> bytes)
    {
        ulong first = 14695981039346656037;
        ulong second = 7809847782465536322;
        foreach (var value in bytes)
        {
            first = unchecked((first ^ value) * 1099511628211);
            second = unchecked((second ^ value) * 14029467366897019727);
        }

        var hash = new byte[KeyPilotDriverClient.DeviceHashLength];
        BinaryPrimitives.WriteUInt64LittleEndian(hash, first);
        BinaryPrimitives.WriteUInt64LittleEndian(hash.AsSpan(sizeof(ulong)), second);
        return hash;
    }

    private static void ValidatePropertySize(uint byteCount)
    {
        if (byteCount < sizeof(char) || byteCount > MaximumPropertyBytes || (byteCount & 1) != 0)
        {
            throw new InvalidDataException($"Invalid device instance ID byte count: {byteCount}.");
        }
    }

    private static void ValidateTerminatedString(ReadOnlySpan<byte> bytes)
    {
        if (bytes[^2] != 0 || bytes[^1] != 0)
        {
            throw new InvalidDataException("The device instance ID is not null-terminated UTF-16.");
        }
        for (var index = 0; index < bytes.Length - sizeof(char); index += sizeof(char))
        {
            if (bytes[index] == 0 && bytes[index + 1] == 0)
            {
                throw new InvalidDataException("The device instance ID contains an embedded null.");
            }
        }
    }

    private static InvalidOperationException ResolutionException(string path, uint configRet) =>
        new($"Unable to resolve the keyboard device interface '{path}' (CONFIGRET 0x{configRet:X8}).");

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct DevPropKey(Guid formatId, uint propertyId)
    {
        public readonly Guid FormatId = formatId;
        public readonly uint PropertyId = propertyId;
    }

    private static partial class NativeMethods
    {
        [LibraryImport("CfgMgr32.dll", EntryPoint = "CM_Get_Device_Interface_PropertyW", StringMarshalling = StringMarshalling.Utf16)]
        internal static partial uint CM_Get_Device_Interface_PropertyW(
            string deviceInterface,
            in DevPropKey propertyKey,
            out uint propertyType,
            [Out] byte[]? propertyBuffer,
            ref uint propertyBufferSize,
            uint flags);
    }
}
