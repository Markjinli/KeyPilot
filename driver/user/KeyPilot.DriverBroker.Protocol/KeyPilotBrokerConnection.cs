using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using KeyPilot.DriverClient;
using Microsoft.Win32.SafeHandles;

namespace KeyPilot.DriverBroker.Protocol;

/// <summary>
/// Authenticated UI-side IPC connection. Responses are correlated internally while Status,
/// EventBatch and HealthChallenge notifications are exposed through ReadMessageAsync. The
/// bounded notification channel intentionally stops draining the pipe if the UI stops consuming;
/// the broker then times out its write and releases the driver lease.
/// </summary>
public sealed class KeyPilotBrokerConnection : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<BrokerAck>> _pending = new();
    private readonly Channel<BrokerFrame> _notifications = Channel.CreateBounded<BrokerFrame>(
        new BoundedChannelOptions(64)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _receiveTask;
    private long _nextCorrelation;
    private int _disposed;

    private KeyPilotBrokerConnection(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
    }

    public static async Task<KeyPilotBrokerConnection> ConnectAsync(
        BrokerLaunchParameters parameters,
        uint expectedServerProcessId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        parameters.Validate();
        if (expectedServerProcessId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedServerProcessId));
        }
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var pipe = new NamedPipeClientStream(
            ".",
            parameters.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        var connection = new KeyPilotBrokerConnection(pipe);
        try
        {
            using var timeoutCancellation = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutCancellation.Token);
            await pipe.ConnectAsync(linked.Token).ConfigureAwait(false);
            BrokerServerProcessIdentity.ValidateConnectedServer(pipe, expectedServerProcessId);

            var correlation = connection.NextCorrelation();
            await connection.WriteAsync(
                BrokerFrameCodec.EncodeHello(
                    new BrokerHello(
                        parameters.ParentProcessId,
                        parameters.ParentStartTimeUtcTicks,
                        parameters.Token),
                    correlation),
                linked.Token).ConfigureAwait(false);
            var response = await BrokerFrameCodec.ReadFrameAsync(pipe, linked.Token).ConfigureAwait(false);
            var ack = BrokerFrameCodec.DecodeAck(response);
            if (response.CorrelationId != correlation || ack.AcknowledgedType != BrokerMessageType.Hello)
            {
                throw new BrokerProtocolException("Broker rejected the authenticated hello.");
            }
            connection._receiveTask = connection.ReceiveLoopAsync();
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task<ulong> ConfigureRulesAsync(
        ulong generation,
        IReadOnlyList<KeyboardRule> rules,
        CancellationToken cancellationToken = default) =>
        SendRequestAsync(
            correlation => BrokerFrameCodec.EncodeConfigureRules(
                new BrokerConfigureRules(generation, rules), correlation),
            BrokerMessageType.ConfigureRules,
            generation,
            cancellationToken);

    public Task AcknowledgeHealthAsync(
        BrokerHealthAck acknowledgement,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            BrokerFrameCodec.EncodeHealthAck(acknowledgement, acknowledgement.ChallengeId),
            cancellationToken);

    public async Task<BrokerFrame> ReadMessageAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return await _notifications.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        _ = await SendRequestAsync(
            BrokerFrameCodec.EncodeShutdown,
            BrokerMessageType.Shutdown,
            expectedGeneration: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _lifetime.Cancel();
        await _pipe.DisposeAsync().ConfigureAwait(false);
        if (_receiveTask is not null)
        {
            try
            {
                await _receiveTask.ConfigureAwait(false);
            }
            catch
            {
                // The channel and pending requests already carry the terminal exception.
            }
        }
        _notifications.Writer.TryComplete();
        var disposed = new ObjectDisposedException(nameof(KeyPilotBrokerConnection));
        FailPending(disposed);
        _writeGate.Dispose();
        _lifetime.Dispose();
    }

    private async Task<ulong> SendRequestAsync(
        Func<ulong, BrokerFrame> frameFactory,
        BrokerMessageType expectedType,
        ulong? expectedGeneration,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var correlation = NextCorrelation();
        var completion = new TaskCompletionSource<BrokerAck>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(correlation, completion))
        {
            throw new InvalidOperationException("Broker request correlation collision.");
        }
        try
        {
            await WriteAsync(frameFactory(correlation), cancellationToken).ConfigureAwait(false);
            var ack = await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (ack.AcknowledgedType != expectedType ||
                (expectedGeneration.HasValue && ack.AppliedGeneration != expectedGeneration.Value))
            {
                throw new BrokerProtocolException("Broker request acknowledgement is inconsistent.");
            }
            return ack.AppliedGeneration;
        }
        catch (OperationCanceledException)
        {
            // The caller can no longer determine whether a security policy was applied. Tear down
            // the connection so the broker releases the exclusive lease instead of guessing.
            _lifetime.Cancel();
            _pipe.Dispose();
            throw;
        }
        finally
        {
            _pending.TryRemove(correlation, out _);
        }
    }

    private async Task ReceiveLoopAsync()
    {
        Exception? failure = null;
        try
        {
            while (true)
            {
                var frame = await BrokerFrameCodec.ReadFrameAsync(_pipe, _lifetime.Token)
                    .ConfigureAwait(false);
                if (frame.Type == BrokerMessageType.Ack)
                {
                    var ack = BrokerFrameCodec.DecodeAck(frame);
                    if (!_pending.TryGetValue(frame.CorrelationId, out var completion) ||
                        !completion.TrySetResult(ack))
                    {
                        throw new BrokerProtocolException("Broker sent an unsolicited or duplicate acknowledgement.");
                    }
                    continue;
                }

                // Decode once here so malformed notifications fail the whole safety connection,
                // rather than being deferred until arbitrary UI code looks at them.
                switch (frame.Type)
                {
                    case BrokerMessageType.Status:
                        _ = BrokerFrameCodec.DecodeStatus(frame);
                        break;
                    case BrokerMessageType.EventBatch:
                        _ = BrokerFrameCodec.DecodeEventBatch(frame);
                        break;
                    case BrokerMessageType.HealthChallenge:
                        _ = BrokerFrameCodec.DecodeHealthChallenge(frame);
                        break;
                    default:
                        throw new BrokerProtocolException("Broker sent an invalid notification type.");
                }
                await _notifications.Writer.WriteAsync(frame, _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            failure = new EndOfStreamException("Broker connection was closed.");
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            failure ??= new EndOfStreamException("Broker pipe disconnected.");
            try
            {
                _pipe.Dispose();
            }
            catch
            {
                // The terminal error below is authoritative.
            }
            _notifications.Writer.TryComplete(failure);
            FailPending(failure);
        }
    }

    private void FailPending(Exception exception)
    {
        foreach (var completion in _pending.Values)
        {
            completion.TrySetException(exception);
        }
    }

    private async Task WriteAsync(BrokerFrame frame, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await BrokerFrameCodec.WriteFrameAsync(_pipe, frame, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private ulong NextCorrelation()
    {
        var correlation = checked((ulong)Interlocked.Increment(ref _nextCorrelation));
        return correlation == 0 ? throw new InvalidOperationException("Correlation space is exhausted.") : correlation;
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}

internal static class BrokerServerProcessIdentity
{
    public static void ValidateConnectedServer(
        NamedPipeClientStream pipe,
        uint expectedServerProcessId)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        if (!NativeMethods.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var actualProcessId) ||
            actualProcessId == 0)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Unable to identify the process serving the broker pipe.");
        }

        Validate(expectedServerProcessId, actualProcessId);
    }

    internal static void Validate(uint expectedServerProcessId, uint actualServerProcessId)
    {
        if (expectedServerProcessId == 0 || actualServerProcessId == 0)
        {
            throw new ArgumentOutOfRangeException(
                expectedServerProcessId == 0
                    ? nameof(expectedServerProcessId)
                    : nameof(actualServerProcessId));
        }

        if (actualServerProcessId != expectedServerProcessId)
        {
            throw new UnauthorizedAccessException(
                $"The broker pipe belongs to process {actualServerProcessId}, not launched broker {expectedServerProcessId}.");
        }
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetNamedPipeServerProcessId(
            SafePipeHandle pipe,
            out uint serverProcessId);
    }
}
