using System.IO.Pipes;
using System.Security.Cryptography;
using KeyPilot.DriverBroker.Protocol;
using KeyPilot.DriverClient;

namespace KeyPilot.DriverBroker;

internal sealed class BrokerServer(
    Func<IBrokerDriverClient>? driverFactory = null,
    TimeProvider? timeProvider = null,
    IPipeClientProcessIdProvider? pipeClientProcessIdProvider = null)
{
    internal const PipeOptions SecurePipeOptions =
        PipeOptions.Asynchronous |
        PipeOptions.CurrentUserOnly |
        PipeOptions.FirstPipeInstance;

    private readonly Func<IBrokerDriverClient> _driverFactory = driverFactory ?? (() => new DriverClientAdapter());
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly IPipeClientProcessIdProvider _pipeClientProcessIdProvider =
        pipeClientProcessIdProvider ?? new WindowsPipeClientProcessIdProvider();

    public async Task RunAsync(BrokerOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            await RunWithOwnedTokenAsync(options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(options.Launch.Token);
        }
    }

    private async Task RunWithOwnedTokenAsync(BrokerOptions options, CancellationToken cancellationToken)
    {
        options.Launch.Validate();
        using var parent = ProcessParentLifetime.Open(
            options.Launch.ParentProcessId,
            options.Launch.ParentStartTimeUtcTicks);

        await using var pipe = new NamedPipeServerStream(
            options.Launch.PipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            SecurePipeOptions,
            BrokerProtocol.MaximumPayloadSize + BrokerProtocol.HeaderSize,
            BrokerProtocol.MaximumPayloadSize + BrokerProtocol.HeaderSize);

        await WaitForConnectionAsync(pipe, parent, TimeSpan.FromSeconds(15), cancellationToken)
            .ConfigureAwait(false);
        var connectedProcessId = _pipeClientProcessIdProvider.GetClientProcessId(pipe);
        await using var transport = new StreamBrokerTransport(pipe);
        await AuthenticateAsync(
            transport,
            options.Launch,
            connectedProcessId,
            parent,
            cancellationToken).ConfigureAwait(false);

        IBrokerDriverClient? driver = null;
        try
        {
            driver = _driverFactory();
            var capabilities = driver.GetCapabilities();
            BrokerDriverCompatibility.Validate(capabilities);
            var lease = driver.AcquireLease(TimeSpan.FromSeconds(2));
            capabilities = driver.GetCapabilities();
            const DriverStateFlags unsafeStates =
                DriverStateFlags.QueueOverflowed |
                DriverStateFlags.InputTrackingLost |
                DriverStateFlags.ProgressTimeout |
                DriverStateFlags.EmergencyBypassActive;
            if ((capabilities.State & unsafeStates) != 0 ||
                (capabilities.State & DriverStateFlags.LeaseActive) == 0 ||
                (capabilities.State & DriverStateFlags.FailOpen) == 0 ||
                (capabilities.State & DriverStateFlags.RulesActive) != 0 ||
                capabilities.ActiveGeneration != 0)
            {
                throw new InvalidDataException("The driver did not enter a clean leased state.");
            }
            var timing = BrokerSessionTiming.FromLease(lease);
            await WriteStatusBestEffortAsync(
                transport,
                new BrokerStatus(
                    BrokerStatusCode.LeaseActive,
                    capabilities.State,
                    0,
                    capabilities.MaximumRules,
                    $"Exclusive driver lease active for {lease.TotalMilliseconds:0} ms."),
                timing.WriteTimeout,
                cancellationToken).ConfigureAwait(false);

            var session = new BrokerSession(
                transport,
                driver,
                parent,
                capabilities,
                timing,
                _timeProvider);
            await session.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Fail-open first. Diagnostics must never extend a bad lease while the pipe is slow.
            driver?.Dispose();
            driver = null;
            await WriteStatusBestEffortAsync(
                transport,
                new BrokerStatus(
                    BrokerStatusCode.FailOpen,
                    DriverStateFlags.FailOpen,
                    0,
                    0,
                    LimitMessage(exception.Message)),
                TimeSpan.FromMilliseconds(250),
                cancellationToken).ConfigureAwait(false);
            throw;
        }
        finally
        {
            // Dispose is deliberately synchronous and first: closing the exclusive handle releases
            // the kernel lease before any remaining IPC cleanup.
            driver?.Dispose();
        }
    }

    private static async Task AuthenticateAsync(
        IBrokerTransport transport,
        BrokerLaunchParameters launch,
        uint connectedProcessId,
        IParentLifetime parent,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var frame = await transport.ReadAsync(linked.Token).ConfigureAwait(false);
        var hello = BrokerFrameCodec.DecodeHello(frame);
        try
        {
            BrokerClientIdentity.Validate(hello, launch, connectedProcessId, parent.HasExited);
            await transport.WriteAsync(
                BrokerFrameCodec.EncodeAck(new BrokerAck(BrokerMessageType.Hello, 0), frame.CorrelationId),
                linked.Token).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hello.Token);
            CryptographicOperations.ZeroMemory(frame.Payload.AsSpan(12, BrokerProtocol.TokenSize));
        }
    }

    private static async Task WaitForConnectionAsync(
        NamedPipeServerStream pipe,
        IParentLifetime parent,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCancellation = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);
        var connection = pipe.WaitForConnectionAsync(linked.Token);
        while (!connection.IsCompleted)
        {
            if (parent.HasExited)
            {
                throw new EndOfStreamException("The parent UI exited before connecting.");
            }
            await Task.WhenAny(connection, Task.Delay(TimeSpan.FromMilliseconds(50), linked.Token))
                .ConfigureAwait(false);
        }
        try
        {
            await connection.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The parent UI did not connect to the broker.");
        }
    }

    private static async Task WriteStatusBestEffortAsync(
        IBrokerTransport transport,
        BrokerStatus status,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCancellation = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutCancellation.Token);
            await transport.WriteAsync(BrokerFrameCodec.EncodeStatus(status), linked.Token)
                .ConfigureAwait(false);
        }
        catch
        {
            // Status is diagnostic only. It can never delay releasing the lease.
        }
    }

    private static string LimitMessage(string message) =>
        string.IsNullOrWhiteSpace(message)
            ? "The broker stopped and released the driver lease."
            : message.Length <= 512 ? message : message[..512];
}

internal static class BrokerDriverCompatibility
{
    public static void Validate(DriverCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        if (capabilities.DriverVersionMajor != KeyPilotDriverClient.DriverVersionMajor ||
            capabilities.DriverVersionMinor != KeyPilotDriverClient.DriverVersionMinor ||
            capabilities.MaximumRules == 0 ||
            capabilities.MaximumRules > KeyPilotDriverClient.MaximumRules)
        {
            throw new InvalidDataException("The installed KeyPilot driver protocol is incompatible.");
        }
    }
}

internal static class BrokerClientIdentity
{
    public static void Validate(
        BrokerHello hello,
        BrokerLaunchParameters launch,
        uint connectedProcessId,
        bool parentHasExited)
    {
        ArgumentNullException.ThrowIfNull(hello);
        ArgumentNullException.ThrowIfNull(launch);
        if (parentHasExited || connectedProcessId == 0 ||
            connectedProcessId != launch.ParentProcessId ||
            hello.ParentProcessId != launch.ParentProcessId ||
            hello.ParentStartTimeUtcTicks != launch.ParentStartTimeUtcTicks ||
            hello.Token is null ||
            !CryptographicOperations.FixedTimeEquals(hello.Token, launch.Token))
        {
            throw new UnauthorizedAccessException("Broker handshake identity is invalid.");
        }
    }
}
