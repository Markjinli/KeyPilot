using KeyPilot.DriverClient;

namespace KeyPilot.DriverBroker.Protocol;

public static class BrokerProtocol
{
    public const uint Magic = 0x42504B4B; // "KKPB" in little endian.
    public const ushort Version = 1;
    public const int HeaderSize = 24;
    public const int TokenSize = 32;
    public const int MaximumPayloadSize = 32 * 1024;
    public const int MaximumStatusMessageBytes = 1024;
    public const int MaximumEventsPerBatch = 64;
    public const int RuleWireSize = 36;
    public const int EventWireSize = 60;
}

public enum BrokerMessageType : ushort
{
    Hello = 1,
    ConfigureRules = 2,
    HealthAck = 3,
    Shutdown = 4,

    Ack = 101,
    EventBatch = 102,
    HealthChallenge = 103,
    Status = 104,
}

public enum BrokerStatusCode : uint
{
    Starting = 1,
    LeaseActive = 2,
    RulesActive = 3,
    FailOpen = 4,
    ShuttingDown = 5,
    Fatal = 6,
}

[Flags]
public enum BrokerHealthFlags : uint
{
    None = 0,
    StateMachineCommitted = 1 << 0,
    ActionQueueHealthy = 1 << 1,
    ActionProgressVerified = 1 << 2,
}

public sealed record BrokerFrame(
    BrokerMessageType Type,
    ulong CorrelationId,
    byte[] Payload);

public sealed record BrokerHello(
    uint ParentProcessId,
    long ParentStartTimeUtcTicks,
    byte[] Token);

public sealed record BrokerConfigureRules(
    ulong Generation,
    IReadOnlyList<KeyboardRule> Rules);

/// <summary>
/// An acknowledgement is a safety claim, not a receipt notification. The UI advances
/// LastCommittedEventSequence only after its mapping state machine accepted all events through
/// that sequence. It advances LastCompletedActionSequence when every resulting action is done
/// (no-action events count as complete). A still-running bounded action may instead advance the
/// monotonic ActionProgressCounter and set ActionProgressVerified. That progress proof keeps the
/// broker/UI link healthy but never advances the kernel acknowledgement: only the continuous
/// LastCompletedActionSequence is passed to the driver.
/// </summary>
public sealed record BrokerHealthAck(
    ulong ChallengeId,
    ulong LastCommittedEventSequence,
    ulong LastCompletedActionSequence,
    ulong ActionProgressCounter,
    BrokerHealthFlags Flags);

public sealed record BrokerHealthChallenge(
    ulong ChallengeId,
    ulong LastDispatchedEventSequence,
    uint ResponseDeadlineMilliseconds);

public sealed record BrokerAck(
    BrokerMessageType AcknowledgedType,
    ulong AppliedGeneration);

public sealed record BrokerStatus(
    BrokerStatusCode Code,
    DriverStateFlags DriverState,
    ulong ActiveGeneration,
    uint MaximumRules,
    string Message);

public sealed record BrokerEventBatch(IReadOnlyList<DriverInputEvent> Events);

public sealed class BrokerProtocolException : IOException
{
    public BrokerProtocolException(string message)
        : base(message)
    {
    }
}
