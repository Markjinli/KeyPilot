using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

[assembly: SupportedOSPlatform("windows10.0.18362")]

namespace KeyPilot.DriverClient;

[Flags]
public enum DriverStateFlags : uint
{
    FailOpen = 1 << 0,
    LeaseActive = 1 << 1,
    RulesActive = 1 << 2,
    QueueOverflowed = 1 << 3,
    InputTrackingLost = 1 << 4,
    ProgressTimeout = 1 << 5,
    EmergencyBypassActive = 1 << 6,
}

[Flags]
public enum KeyboardRuleFlags : uint
{
    None = 0,
    SuppressOriginal = 1 << 0,
}

public enum DriverInputPhase : ushort
{
    Down = 1,
    Up = 2,
    Repeat = 3,
}

public enum KeyboardScanPrefix
{
    None,
    E0,
    E1,
}

public sealed record DriverCapabilities(
    uint DriverVersionMajor,
    uint DriverVersionMinor,
    uint MaximumRules,
    DriverStateFlags State,
    ulong ActiveGeneration);

public sealed record KeyboardRule(
    ReadOnlyMemory<byte> DeviceHash,
    ushort MakeCode,
    ushort RequiredFlags,
    ushort IgnoredFlags,
    KeyboardRuleFlags Flags,
    ulong RuleId)
{
    public static KeyboardRule ForEveryKeyboard(
        ushort makeCode,
        KeyboardScanPrefix prefix,
        ulong ruleId)
    {
        var (required, ignored) = ExactPrefixFlags(prefix);
        return new(new byte[KeyPilotDriverClient.DeviceHashLength], makeCode, required, ignored,
            KeyboardRuleFlags.SuppressOriginal, ruleId);
    }

    public static KeyboardRule ForDevice(
        ReadOnlyMemory<byte> deviceHash,
        ushort makeCode,
        KeyboardScanPrefix prefix,
        ulong ruleId)
    {
        var (required, ignored) = ExactPrefixFlags(prefix);
        return new(deviceHash, makeCode, required, ignored, KeyboardRuleFlags.SuppressOriginal, ruleId);
    }

    private static (ushort Required, ushort Ignored) ExactPrefixFlags(KeyboardScanPrefix prefix) => prefix switch
    {
        KeyboardScanPrefix.None => (0x0000, 0x0006),
        KeyboardScanPrefix.E0 => (0x0002, 0x0004),
        KeyboardScanPrefix.E1 => (0x0004, 0x0002),
        _ => throw new ArgumentOutOfRangeException(nameof(prefix)),
    };
}

public sealed record DriverInputEvent(
    ulong Generation,
    ulong RuleId,
    ulong Sequence,
    byte[] DeviceHash,
    ushort MakeCode,
    ushort Flags,
    ushort VirtualKey,
    DriverInputPhase Phase,
    ushort UnitId);

/// <summary>
/// Strict user-mode boundary for the KeyPilot kernel protocol. This type sends
/// only fixed binary policy records; it cannot send paths, programs or scripts.
/// A process crash is safe because the kernel lease expires independently.
/// </summary>
public sealed class KeyPilotDriverClient : IDisposable
{
    public const int ProtocolVersion = 2;
    public const uint DriverVersionMajor = 0;
    public const uint DriverVersionMinor = 2;
    public const int DeviceHashLength = 16;
    public const int MaximumRules = 512;

    private const int CapabilitiesSize = 32;
    private const int LeaseSize = 24;
    private const int HeartbeatSize = 32;
    private const int ReleaseSize = 16;
    private const int RulesetHeaderSize = 32;
    private const int RuleSize = 36;
    private const int EventSize = 60;
    private const ushort MatchFlagMask = 0x0006;

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;
    private const uint DeviceType = 0x8000;
    private const uint MethodBuffered = 0;
    private const uint FileReadData = 0x0001;
    private const uint FileWriteData = 0x0002;

    private static readonly uint IoctlGetCapabilities = CtlCode(0x800, FileReadData);
    private static readonly uint IoctlAcquireLease = CtlCode(0x801, FileReadData | FileWriteData);
    private static readonly uint IoctlHeartbeat = CtlCode(0x802, FileWriteData);
    private static readonly uint IoctlReplaceRules = CtlCode(0x803, FileWriteData);
    private static readonly uint IoctlReadEvents = CtlCode(0x804, FileReadData);
    private static readonly uint IoctlReleaseLease = CtlCode(0x805, FileWriteData);

    private readonly SafeFileHandle _handle;
    private readonly object _ioGate = new();
    private ulong _leaseId;
    private ulong _generation;
    private ulong _lastDeliveredSequence;
    private ulong _lastAcknowledgedSequence;
    private bool _disposed;

    public KeyPilotDriverClient()
    {
        _handle = NativeMethods.CreateFile(
            @"\\.\KeyPilot",
            GenericRead | GenericWrite,
            0,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);
        if (_handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), @"Unable to open \\.\KeyPilot.");
        }
    }

    public bool HasLease
    {
        get
        {
            lock (_ioGate)
            {
                return _leaseId != 0;
            }
        }
    }

    public ulong ActiveGeneration
    {
        get
        {
            lock (_ioGate)
            {
                return _generation;
            }
        }
    }

    public ulong LastAcknowledgedSequence
    {
        get
        {
            lock (_ioGate)
            {
                return _lastAcknowledgedSequence;
            }
        }
    }

    /// <summary>
    /// Validates the complete protocol-v2 rule contract without opening the
    /// driver. UI and policy code can use this before acquiring a lease.
    /// </summary>
    public static void ValidateRules(IReadOnlyList<KeyboardRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (rules.Count > MaximumRules)
        {
            throw new ArgumentOutOfRangeException(nameof(rules), "The ruleset exceeds the protocol limit.");
        }

        var seenIds = new HashSet<ulong>();
        for (var index = 0; index < rules.Count; index++)
        {
            var rule = rules[index] ?? throw new ArgumentException("Rules cannot contain null.", nameof(rules));
            ValidateRule(rule, seenIds);
            for (var prior = 0; prior < index; prior++)
            {
                if (RulesOverlap(rules[prior], rule))
                {
                    throw new ArgumentException("Rules must not have overlapping match predicates.", nameof(rules));
                }
            }
        }
    }

    public DriverCapabilities GetCapabilities()
    {
        lock (_ioGate)
        {
            var output = Invoke(IoctlGetCapabilities, Array.Empty<byte>(), CapabilitiesSize);
            ValidateHeader(output, CapabilitiesSize);
            return new DriverCapabilities(
                ReadUInt32(output, 8),
                ReadUInt32(output, 12),
                ReadUInt32(output, 16),
                (DriverStateFlags)ReadUInt32(output, 20),
                ReadUInt64(output, 24));
        }
    }

    public TimeSpan AcquireLease(TimeSpan requested)
    {
        lock (_ioGate)
        {
            ThrowIfDisposed();
            _leaseId = 0;
            _generation = 0;
            _lastDeliveredSequence = 0;
            _lastAcknowledgedSequence = 0;
            var milliseconds = requested == TimeSpan.Zero ? 0u : checked((uint)requested.TotalMilliseconds);
            Span<byte> nonceBytes = stackalloc byte[8];
            RandomNumberGenerator.Fill(nonceBytes);
            var input = new byte[LeaseSize];
            WriteHeader(input, LeaseSize);
            WriteUInt32(input, 8, milliseconds);
            WriteUInt64(input, 16, BinaryPrimitives.ReadUInt64LittleEndian(nonceBytes));

            var output = Invoke(IoctlAcquireLease, input, LeaseSize);
            ValidateHeader(output, LeaseSize);
            _leaseId = ReadUInt64(output, 16);
            if (_leaseId == 0)
            {
                throw new InvalidDataException("The driver returned an invalid zero lease identifier.");
            }
            return TimeSpan.FromMilliseconds(ReadUInt32(output, 8));
        }
    }

    public void ReplaceRules(IReadOnlyList<KeyboardRule> rules, ulong generation)
    {
        lock (_ioGate)
        {
            ThrowIfDisposed();
            EnsureLease();
            ValidateRules(rules);
            if (generation == 0 || generation <= _generation)
            {
                throw new ArgumentOutOfRangeException(nameof(generation), "Generation must increase.");
            }

            var input = new byte[checked(RulesetHeaderSize + (rules.Count * RuleSize))];
            WriteHeader(input, input.Length);
            WriteUInt64(input, 8, _leaseId);
            WriteUInt64(input, 16, generation);
            WriteUInt32(input, 24, checked((uint)rules.Count));
            WriteUInt32(input, 28, RuleSize);

            for (var index = 0; index < rules.Count; index++)
            {
                var rule = rules[index];
                var offset = RulesetHeaderSize + (index * RuleSize);
                rule.DeviceHash.Span.CopyTo(input.AsSpan(offset, DeviceHashLength));
                WriteUInt16(input, offset + 16, rule.MakeCode);
                WriteUInt16(input, offset + 18, rule.RequiredFlags);
                WriteUInt16(input, offset + 20, rule.IgnoredFlags);
                WriteUInt32(input, offset + 24, (uint)rule.Flags);
                WriteUInt64(input, offset + 28, rule.RuleId);
            }

            try
            {
                _ = Invoke(IoctlReplaceRules, input, 0);
                _generation = generation;
            }
            catch
            {
                InvalidateLocalLease();
                throw;
            }
        }
    }

    public void Heartbeat()
    {
        lock (_ioGate)
        {
            SendHeartbeatLocked(_lastAcknowledgedSequence);
        }
    }

    public void Heartbeat(ulong lastSuccessfullyDispatchedSequence)
    {
        lock (_ioGate)
        {
            SendHeartbeatLocked(lastSuccessfullyDispatchedSequence);
        }
    }

    private void SendHeartbeatLocked(ulong lastSuccessfullyDispatchedSequence)
    {
        ThrowIfDisposed();
        EnsureLease();
        if (lastSuccessfullyDispatchedSequence > _lastDeliveredSequence)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lastSuccessfullyDispatchedSequence),
                "The ACK cannot exceed the last event read from the driver.");
        }
        if (lastSuccessfullyDispatchedSequence < _lastAcknowledgedSequence)
        {
            // A timer may have captured an older value before another thread
            // successfully advanced the ACK. Never send a regression.
            lastSuccessfullyDispatchedSequence = _lastAcknowledgedSequence;
        }
        try
        {
            _ = Invoke(IoctlHeartbeat, CreateLeaseMessage(lastSuccessfullyDispatchedSequence), 0);
            _lastAcknowledgedSequence = lastSuccessfullyDispatchedSequence;
        }
        catch
        {
            InvalidateLocalLease();
            throw;
        }
    }

    public IReadOnlyList<DriverInputEvent> ReadEvents(int maximumEvents = 64)
    {
        lock (_ioGate)
        {
            ThrowIfDisposed();
            EnsureLease();
            if (maximumEvents is < 1 or > 64)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumEvents));
            }

            try
            {
                var output = Invoke(IoctlReadEvents, Array.Empty<byte>(), checked(maximumEvents * EventSize), allowShortOutput: true);
                if (output.Length % EventSize != 0)
                {
                    throw new InvalidDataException("The driver returned a partial event record.");
                }

                var events = new List<DriverInputEvent>(output.Length / EventSize);
                var deliveredSequence = _lastDeliveredSequence;
                for (var offset = 0; offset < output.Length; offset += EventSize)
                {
                    ValidateHeader(output.AsSpan(offset, EventSize), EventSize);
                    var sequence = ReadUInt64(output, offset + 24);
                    if (sequence != deliveredSequence + 1)
                    {
                        throw new InvalidDataException("The driver returned a non-contiguous event sequence.");
                    }
                    deliveredSequence = sequence;
                    events.Add(new DriverInputEvent(
                        ReadUInt64(output, offset + 8),
                        ReadUInt64(output, offset + 16),
                        sequence,
                        output.AsSpan(offset + 32, DeviceHashLength).ToArray(),
                        ReadUInt16(output, offset + 48),
                        ReadUInt16(output, offset + 50),
                        ReadUInt16(output, offset + 52),
                        (DriverInputPhase)ReadUInt16(output, offset + 54),
                        checked((ushort)ReadUInt32(output, offset + 56))));
                }
                _lastDeliveredSequence = deliveredSequence;
                return events;
            }
            catch
            {
                InvalidateLocalLease();
                throw;
            }
        }
    }

    public void ReleaseLease()
    {
        lock (_ioGate)
        {
            ThrowIfDisposed();
            if (_leaseId == 0)
            {
                return;
            }
            try
            {
                var input = new byte[ReleaseSize];
                WriteHeader(input, ReleaseSize);
                WriteUInt64(input, 8, _leaseId);
                _ = Invoke(IoctlReleaseLease, input, 0);
            }
            finally
            {
                InvalidateLocalLease();
            }
        }
    }

    public void Dispose()
    {
        lock (_ioGate)
        {
            if (_disposed)
            {
                return;
            }
            try
            {
                ReleaseLease();
            }
            catch (Win32Exception)
            {
                // Closing the exclusive handle still invalidates the kernel lease.
            }
            finally
            {
                _disposed = true;
                _leaseId = 0;
                _generation = 0;
                _lastDeliveredSequence = 0;
                _lastAcknowledgedSequence = 0;
                _handle.Dispose();
            }
        }
    }

    private byte[] CreateLeaseMessage(ulong lastSuccessfullyDispatchedSequence)
    {
        var input = new byte[HeartbeatSize];
        WriteHeader(input, HeartbeatSize);
        WriteUInt64(input, 8, _leaseId);
        WriteUInt64(input, 16, _generation);
        WriteUInt64(input, 24, lastSuccessfullyDispatchedSequence);
        return input;
    }

    private byte[] Invoke(uint code, byte[] input, int outputSize, bool allowShortOutput = false)
    {
        lock (_ioGate)
        {
            ThrowIfDisposed();
            var output = new byte[outputSize];
            if (!NativeMethods.DeviceIoControl(
                    _handle,
                    code,
                    input.Length == 0 ? null : input,
                    checked((uint)input.Length),
                    outputSize == 0 ? null : output,
                    checked((uint)outputSize),
                    out var returned,
                    IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (returned > output.Length || (!allowShortOutput && returned != output.Length))
            {
                throw new InvalidDataException("The driver returned an unexpected buffer length.");
            }
            return returned == output.Length ? output : output.AsSpan(0, checked((int)returned)).ToArray();
        }
    }

    private static void ValidateRule(KeyboardRule rule, HashSet<ulong> seenIds)
    {
        if (rule.DeviceHash.Length != DeviceHashLength || rule.MakeCode == 0 || rule.RuleId == 0 ||
            !seenIds.Add(rule.RuleId) || (rule.RequiredFlags & ~MatchFlagMask) != 0 ||
            (rule.IgnoredFlags & ~MatchFlagMask) != 0 ||
            (rule.RequiredFlags & rule.IgnoredFlags) != 0 ||
            rule.Flags != KeyboardRuleFlags.SuppressOriginal ||
            !HasExactPrefix(rule) ||
            WouldBlockEmergencyKeyboardPath(rule))
        {
            throw new ArgumentException("The keyboard rule is not representable by protocol v2.", nameof(rule));
        }
    }

    private static bool HasExactPrefix(KeyboardRule rule) =>
        rule.RequiredFlags == 0 && rule.IgnoredFlags == MatchFlagMask ||
        rule.RequiredFlags == 0x0002 && rule.IgnoredFlags == 0x0004 ||
        rule.RequiredFlags == 0x0004 && rule.IgnoredFlags == 0x0002;

    private static bool WouldBlockEmergencyKeyboardPath(KeyboardRule rule) =>
        (rule.Flags & KeyboardRuleFlags.SuppressOriginal) != 0 &&
        (((rule.MakeCode == 0x001d || rule.MakeCode == 0x0038) && RuleMatchesFlags(rule, 0)) ||
         ((rule.MakeCode == 0x002a || rule.MakeCode == 0x0058) && RuleMatchesFlags(rule, 0)) ||
         (rule.MakeCode == 0x0053 && RuleMatchesFlags(rule, 0x0002)));

    private static bool RuleMatchesFlags(KeyboardRule rule, ushort flags) =>
        (flags & rule.RequiredFlags) == rule.RequiredFlags &&
        (flags & rule.IgnoredFlags) == 0;

    private static bool RulesOverlap(KeyboardRule left, KeyboardRule right)
    {
        var devicesOverlap = IsZeroHash(left.DeviceHash.Span) || IsZeroHash(right.DeviceHash.Span) ||
                             left.DeviceHash.Span.SequenceEqual(right.DeviceHash.Span);
        return devicesOverlap && left.MakeCode == right.MakeCode &&
               (left.RequiredFlags & right.IgnoredFlags) == 0 &&
               (right.RequiredFlags & left.IgnoredFlags) == 0;
    }

    private static bool IsZeroHash(ReadOnlySpan<byte> hash)
    {
        byte combined = 0;
        foreach (var value in hash)
        {
            combined |= value;
        }
        return combined == 0;
    }

    private void EnsureLease()
    {
        if (_leaseId == 0)
        {
            throw new InvalidOperationException("Acquire a lease before changing or heartbeating policy.");
        }
    }

    private void InvalidateLocalLease()
    {
        _leaseId = 0;
        _generation = 0;
        _lastDeliveredSequence = 0;
        _lastAcknowledgedSequence = 0;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static uint CtlCode(uint function, uint access) =>
        (DeviceType << 16) | (access << 14) | (function << 2) | MethodBuffered;

    private static void WriteHeader(Span<byte> buffer, int size)
    {
        WriteUInt32(buffer, 0, checked((uint)size));
        WriteUInt32(buffer, 4, ProtocolVersion);
    }

    private static void ValidateHeader(ReadOnlySpan<byte> buffer, int expectedSize)
    {
        if (buffer.Length < expectedSize || ReadUInt32(buffer, 0) != expectedSize ||
            ReadUInt32(buffer, 4) != ProtocolVersion)
        {
            throw new InvalidDataException("Kernel protocol version or record size mismatch.");
        }
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> value, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(value[offset..]);
    private static uint ReadUInt32(ReadOnlySpan<byte> value, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(value[offset..]);
    private static ulong ReadUInt64(ReadOnlySpan<byte> value, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(value[offset..]);
    private static void WriteUInt16(Span<byte> value, int offset, ushort data) => BinaryPrimitives.WriteUInt16LittleEndian(value[offset..], data);
    private static void WriteUInt32(Span<byte> value, int offset, uint data) => BinaryPrimitives.WriteUInt32LittleEndian(value[offset..], data);
    private static void WriteUInt64(Span<byte> value, int offset, ulong data) => BinaryPrimitives.WriteUInt64LittleEndian(value[offset..], data);

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeviceIoControl(
            SafeFileHandle device,
            uint controlCode,
            byte[]? inputBuffer,
            uint inputBufferSize,
            byte[]? outputBuffer,
            uint outputBufferSize,
            out uint bytesReturned,
            IntPtr overlapped);
    }
}
