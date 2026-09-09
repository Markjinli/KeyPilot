using System.Diagnostics;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using KeyPilot.DriverBroker.Protocol;
using KeyPilot.DriverClient;

namespace KeyPilot.DriverBroker;

internal sealed class StreamBrokerTransport(Stream stream) : IBrokerTransport
{
    public ValueTask<BrokerFrame> ReadAsync(CancellationToken cancellationToken) =>
        BrokerFrameCodec.ReadFrameAsync(stream, cancellationToken);

    public ValueTask WriteAsync(BrokerFrame frame, CancellationToken cancellationToken) =>
        BrokerFrameCodec.WriteFrameAsync(stream, frame, cancellationToken);

    public ValueTask DisposeAsync() => stream.DisposeAsync();
}

internal sealed class DriverClientAdapter : IBrokerDriverClient
{
    private readonly KeyPilotDriverClient _inner = new();

    public DriverCapabilities GetCapabilities() => _inner.GetCapabilities();

    public TimeSpan AcquireLease(TimeSpan requested) => _inner.AcquireLease(requested);

    public void ReplaceRules(IReadOnlyList<KeyboardRule> rules, ulong generation) =>
        _inner.ReplaceRules(rules, generation);

    public void Heartbeat(ulong lastSuccessfullyDispatchedSequence) =>
        _inner.Heartbeat(lastSuccessfullyDispatchedSequence);

    public IReadOnlyList<DriverInputEvent> ReadEvents(int maximumEvents) =>
        _inner.ReadEvents(maximumEvents);

    public void Dispose() => _inner.Dispose();
}

internal sealed class ProcessParentLifetime : IParentLifetime
{
    private readonly Process _process;

    private ProcessParentLifetime(Process process)
    {
        _process = process;
    }

    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public static ProcessParentLifetime Open(uint processId, long expectedStartTimeUtcTicks)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(checked((int)processId));
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException("The parent UI process no longer exists.", exception);
        }
        try
        {
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != expectedStartTimeUtcTicks)
            {
                throw new InvalidOperationException("The parent UI process identity does not match.");
            }
            return new ProcessParentLifetime(process);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    public void Dispose() => _process.Dispose();
}

internal sealed class WindowsPipeClientProcessIdProvider : IPipeClientProcessIdProvider
{
    public uint GetClientProcessId(NamedPipeServerStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        if (!NativeMethods.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var processId) ||
            processId == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Unable to identify the process connected to the broker pipe.");
        }
        return processId;
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetNamedPipeClientProcessId(
            SafePipeHandle pipe,
            out uint clientProcessId);
    }
}
