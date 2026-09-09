using KeyPilot.DriverBroker.Protocol;
using KeyPilot.DriverClient;
using System.IO.Pipes;

namespace KeyPilot.DriverBroker;

internal interface IBrokerTransport : IAsyncDisposable
{
    ValueTask<BrokerFrame> ReadAsync(CancellationToken cancellationToken);

    ValueTask WriteAsync(BrokerFrame frame, CancellationToken cancellationToken);
}

internal interface IBrokerDriverClient : IDisposable
{
    DriverCapabilities GetCapabilities();

    TimeSpan AcquireLease(TimeSpan requested);

    void ReplaceRules(IReadOnlyList<KeyboardRule> rules, ulong generation);

    void Heartbeat(ulong lastSuccessfullyDispatchedSequence);

    IReadOnlyList<DriverInputEvent> ReadEvents(int maximumEvents);
}

internal interface IParentLifetime : IDisposable
{
    bool HasExited { get; }
}

internal interface IPipeClientProcessIdProvider
{
    uint GetClientProcessId(NamedPipeServerStream pipe);
}

internal sealed record BrokerSessionTiming(
    TimeSpan PollInterval,
    TimeSpan HeartbeatInterval,
    TimeSpan HealthAckTimeout,
    TimeSpan WriteTimeout)
{
    public static BrokerSessionTiming FromLease(TimeSpan grantedLease)
    {
        if (grantedLease < TimeSpan.FromMilliseconds(250))
        {
            throw new InvalidDataException("The driver granted an unsafe lease duration.");
        }

        var heartbeat = TimeSpan.FromMilliseconds(Math.Max(25, grantedLease.TotalMilliseconds / 4));
        var ackTimeout = TimeSpan.FromMilliseconds(Math.Max(50, grantedLease.TotalMilliseconds / 3));
        var writeTimeout = TimeSpan.FromMilliseconds(Math.Max(50, grantedLease.TotalMilliseconds / 4));
        if (heartbeat + ackTimeout >= grantedLease)
        {
            throw new InvalidDataException("The driver lease cannot accommodate a challenged heartbeat.");
        }
        return new BrokerSessionTiming(TimeSpan.FromMilliseconds(10), heartbeat, ackTimeout, writeTimeout);
    }
}
