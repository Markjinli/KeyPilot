using System.Buffers.Binary;
using System.Text;
using KeyPilot.DriverClient;

namespace KeyPilot.DriverBroker.Protocol;

public static class BrokerFrameCodec
{
    private const uint AllowedHealthFlags =
        (uint)(BrokerHealthFlags.StateMachineCommitted |
               BrokerHealthFlags.ActionQueueHealthy |
               BrokerHealthFlags.ActionProgressVerified);
    private const uint AllowedDriverStateFlags =
        (uint)(DriverStateFlags.FailOpen |
               DriverStateFlags.LeaseActive |
               DriverStateFlags.RulesActive |
               DriverStateFlags.QueueOverflowed |
               DriverStateFlags.InputTrackingLost |
               DriverStateFlags.ProgressTimeout |
               DriverStateFlags.EmergencyBypassActive);

    public static async ValueTask WriteFrameAsync(
        Stream stream,
        BrokerFrame frame,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ValidateFrame(frame);

        var header = new byte[BrokerProtocol.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, BrokerProtocol.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), BrokerProtocol.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), (ushort)frame.Type);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), checked((uint)frame.Payload.Length));
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(12), frame.CorrelationId);
        // Bytes 20..23 are reserved and remain zero.

        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (frame.Payload.Length != 0)
        {
            await stream.WriteAsync(frame.Payload, cancellationToken).ConfigureAwait(false);
        }
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<BrokerFrame> ReadFrameAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[BrokerProtocol.HeaderSize];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);

        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != BrokerProtocol.Magic ||
            BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4)) != BrokerProtocol.Version)
        {
            throw new BrokerProtocolException("Broker frame magic or version is invalid.");
        }
        if (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20)) != 0)
        {
            throw new BrokerProtocolException("Broker frame reserved header bits must be zero.");
        }

        var rawType = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6));
        if (!Enum.IsDefined(typeof(BrokerMessageType), rawType))
        {
            throw new BrokerProtocolException("Broker message type is unknown.");
        }

        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
        if (payloadLength > BrokerProtocol.MaximumPayloadSize)
        {
            throw new BrokerProtocolException("Broker frame payload exceeds the protocol limit.");
        }

        var payload = new byte[checked((int)payloadLength)];
        if (payload.Length != 0)
        {
            await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        }

        var frame = new BrokerFrame(
            (BrokerMessageType)rawType,
            BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(12)),
            payload);
        ValidateFrame(frame);
        return frame;
    }

    public static BrokerFrame EncodeHello(BrokerHello hello, ulong correlationId)
    {
        ArgumentNullException.ThrowIfNull(hello);
        RequireRequestCorrelation(correlationId);
        if (hello.ParentProcessId == 0 || hello.ParentStartTimeUtcTicks <= 0 ||
            hello.Token is null || hello.Token.Length != BrokerProtocol.TokenSize)
        {
            throw new ArgumentException("Hello identity is invalid.", nameof(hello));
        }

        var payload = new byte[44];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, hello.ParentProcessId);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(4), hello.ParentStartTimeUtcTicks);
        hello.Token.CopyTo(payload, 12);
        return new BrokerFrame(BrokerMessageType.Hello, correlationId, payload);
    }

    public static BrokerHello DecodeHello(BrokerFrame frame)
    {
        Require(frame, BrokerMessageType.Hello, 44, requestCorrelation: true);
        var token = frame.Payload.AsSpan(12, BrokerProtocol.TokenSize).ToArray();
        return new BrokerHello(
            BinaryPrimitives.ReadUInt32LittleEndian(frame.Payload),
            BinaryPrimitives.ReadInt64LittleEndian(frame.Payload.AsSpan(4)),
            token);
    }

    public static BrokerFrame EncodeConfigureRules(
        BrokerConfigureRules configure,
        ulong correlationId)
    {
        ArgumentNullException.ThrowIfNull(configure);
        RequireRequestCorrelation(correlationId);
        if (configure.Generation == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(configure), "Generation must be nonzero.");
        }
        KeyPilotDriverClient.ValidateRules(configure.Rules);

        var payload = new byte[checked(16 + configure.Rules.Count * BrokerProtocol.RuleWireSize)];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, configure.Generation);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), checked((uint)configure.Rules.Count));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12), BrokerProtocol.RuleWireSize);
        for (var index = 0; index < configure.Rules.Count; index++)
        {
            WriteRule(payload.AsSpan(16 + index * BrokerProtocol.RuleWireSize), configure.Rules[index]);
        }
        return new BrokerFrame(BrokerMessageType.ConfigureRules, correlationId, payload);
    }

    public static BrokerConfigureRules DecodeConfigureRules(BrokerFrame frame)
    {
        RequireTypeAndRequest(frame, BrokerMessageType.ConfigureRules);
        if (frame.Payload.Length < 16)
        {
            throw new BrokerProtocolException("ConfigureRules payload is truncated.");
        }
        var generation = BinaryPrimitives.ReadUInt64LittleEndian(frame.Payload);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(frame.Payload.AsSpan(8));
        var wireSize = BinaryPrimitives.ReadUInt32LittleEndian(frame.Payload.AsSpan(12));
        if (generation == 0 || count > KeyPilotDriverClient.MaximumRules ||
            wireSize != BrokerProtocol.RuleWireSize ||
            frame.Payload.Length != checked(16 + (int)count * BrokerProtocol.RuleWireSize))
        {
            throw new BrokerProtocolException("ConfigureRules metadata is invalid.");
        }

        var rules = new KeyboardRule[count];
        for (var index = 0; index < rules.Length; index++)
        {
            rules[index] = ReadRule(frame.Payload.AsSpan(16 + index * BrokerProtocol.RuleWireSize));
        }
        try
        {
            KeyPilotDriverClient.ValidateRules(rules);
        }
        catch (ArgumentException exception)
        {
            throw new BrokerProtocolException($"ConfigureRules contains an invalid rule: {exception.Message}");
        }
        return new BrokerConfigureRules(generation, rules);
    }

    public static BrokerFrame EncodeHealthAck(BrokerHealthAck ack, ulong correlationId)
    {
        ArgumentNullException.ThrowIfNull(ack);
        RequireRequestCorrelation(correlationId);
        if (ack.ChallengeId == 0 || ((uint)ack.Flags & ~AllowedHealthFlags) != 0)
        {
            throw new ArgumentException("Health acknowledgement is invalid.", nameof(ack));
        }

        var payload = new byte[40];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, ack.ChallengeId);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(8), ack.LastCommittedEventSequence);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(16), ack.LastCompletedActionSequence);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(24), ack.ActionProgressCounter);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(32), (uint)ack.Flags);
        return new BrokerFrame(BrokerMessageType.HealthAck, correlationId, payload);
    }

    public static BrokerHealthAck DecodeHealthAck(BrokerFrame frame)
    {
        Require(frame, BrokerMessageType.HealthAck, 40, requestCorrelation: true);
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(frame.Payload.AsSpan(32));
        if (BinaryPrimitives.ReadUInt32LittleEndian(frame.Payload.AsSpan(36)) != 0 ||
            (flags & ~AllowedHealthFlags) != 0)
        {
            throw new BrokerProtocolException("Health acknowledgement flags are invalid.");
        }
        return new BrokerHealthAck(
            BinaryPrimitives.ReadUInt64LittleEndian(frame.Payload),
            BinaryPrimitives.ReadUInt64LittleEndian(frame.Payload.AsSpan(8)),
            BinaryPrimitives.ReadUInt64LittleEndian(frame.Payload.AsSpan(16)),
            BinaryPrimitives.ReadUInt64LittleEndian(frame.Payload.AsSpan(24)),
            (BrokerHealthFlags)flags) is var acknowledgement &&
            acknowledgement.ChallengeId == frame.CorrelationId
                ? acknowledgement
                : throw new BrokerProtocolException("Health acknowledgement correlation is invalid.");
    }

    public static BrokerFrame EncodeShutdown(ulong correlationId)
    {
        RequireRequestCorrelation(correlationId);
        return new BrokerFrame(BrokerMessageType.Shutdown, correlationId, Array.Empty<byte>());
    }

    public static void DecodeShutdown(BrokerFrame frame) =>
        Require(frame, BrokerMessageType.Shutdown, 0, requestCorrelation: true);

    public static BrokerFrame EncodeAck(BrokerAck ack, ulong correlationId)
    {
        ArgumentNullException.ThrowIfNull(ack);
        RequireRequestCorrelation(correlationId);
        if (ack.AcknowledgedType is not (BrokerMessageType.Hello or
            BrokerMessageType.ConfigureRules or BrokerMessageType.Shutdown))
        {
            throw new ArgumentException("Acknowledged message type is invalid.", nameof(ack));
        }
        var payload = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, (ushort)ack.AcknowledgedType);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(8), ack.AppliedGeneration);
        return new BrokerFrame(BrokerMessageType.Ack, correlationId, payload);
    }

    public static BrokerAck DecodeAck(BrokerFrame frame)
    {
        Require(frame, BrokerMessageType.Ack, 16, requestCorrelation: true);
        if (BinaryPrimitives.ReadUInt16LittleEndian(frame.Payload.AsSpan(2)) != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(frame.Payload.AsSpan(4)) != 0)
        {
            throw new BrokerProtocolException("Ack reserved fields must be zero.");
        }
        var rawType = BinaryPrimitives.ReadUInt16LittleEndian(frame.Payload);
        if (rawType is not ((ushort)BrokerMessageType.Hello) and not
            ((ushort)BrokerMessageType.ConfigureRules) and not ((ushort)BrokerMessageType.Shutdown))
        {
            throw new BrokerProtocolException("Ack references an invalid message type.");
        }
        return new BrokerAck(
            (BrokerMessageType)rawType,
            BinaryPrimitives.ReadUInt64LittleEndian(frame.Payload.AsSpan(8)));
    }

    public static BrokerFrame EncodeHealthChallenge(BrokerHealthChallenge challenge)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        if (challenge.ChallengeId == 0 || challenge.ResponseDeadlineMilliseconds == 0)
        {
            throw new ArgumentException("Health challenge is invalid.", nameof(challenge));
        }
        var payload = new byte[24];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, challenge.ChallengeId);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(8), challenge.LastDispatchedEventSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16), challenge.ResponseDeadlineMilliseconds);
        return new BrokerFrame(BrokerMessageType.HealthChallenge, challenge.ChallengeId, payload);
    }

    public static BrokerHealthChallenge DecodeHealthChallenge(BrokerFrame frame)
    {
        Require(frame, BrokerMessageType.HealthChallenge, 24, requestCorrelation: true);
        if (BinaryPrimitives.ReadUInt32LittleEndian(frame.Payload.AsSpan(20)) != 0)
        {
            throw new BrokerProtocolException("Health challenge reserved field must be zero.");
        }
        var challenge = new BrokerHealthChallenge(
            BinaryPrimitives.ReadUInt64LittleEndian(frame.Payload),
            BinaryPrimitives.ReadUInt64LittleEndian(frame.Payload.AsSpan(8)),
            BinaryPrimitives.ReadUInt32LittleEndian(frame.Payload.AsSpan(16)));
        if (challenge.ChallengeId == 0 || challenge.ChallengeId != frame.CorrelationId ||
            challenge.ResponseDeadlineMilliseconds == 0)
        {
            throw new BrokerProtocolException("Health challenge identity is invalid.");
        }
        return challenge;
    }

    public static BrokerFrame EncodeStatus(BrokerStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var message = Encoding.UTF8.GetBytes(status.Message ?? string.Empty);
        if (message.Length > BrokerProtocol.MaximumStatusMessageBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(status), "Status message is too long.");
        }
        var payload = new byte[24 + message.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)status.Code);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), (uint)status.DriverState);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(8), status.ActiveGeneration);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16), status.MaximumRules);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(20), checked((uint)message.Length));
        message.CopyTo(payload, 24);
        return new BrokerFrame(BrokerMessageType.Status, 0, payload);
    }

    public static BrokerStatus DecodeStatus(BrokerFrame frame)
    {
        RequireTypeAndNotification(frame, BrokerMessageType.Status);
        if (frame.Payload.Length < 24)
        {
            throw new BrokerProtocolException("Status payload is truncated.");
        }
        var rawCode = BinaryPrimitives.ReadUInt32LittleEndian(frame.Payload);
        var rawState = BinaryPrimitives.ReadUInt32LittleEndian(frame.Payload.AsSpan(4));
        var messageLength = BinaryPrimitives.ReadUInt32LittleEndian(frame.Payload.AsSpan(20));
        if (!Enum.IsDefined(typeof(BrokerStatusCode), rawCode) ||
            (rawState & ~AllowedDriverStateFlags) != 0 ||
            messageLength > BrokerProtocol.MaximumStatusMessageBytes ||
            frame.Payload.Length != 24 + messageLength)
        {
            throw new BrokerProtocolException("Status payload metadata is invalid.");
        }
        string message;
        try
        {
            message = new UTF8Encoding(false, true).GetString(frame.Payload, 24, checked((int)messageLength));
        }
        catch (DecoderFallbackException exception)
        {
            throw new BrokerProtocolException($"Status text is not valid UTF-8: {exception.Message}");
        }
        return new BrokerStatus(
            (BrokerStatusCode)rawCode,
            (DriverStateFlags)rawState,
            BinaryPrimitives.ReadUInt64LittleEndian(frame.Payload.AsSpan(8)),
            BinaryPrimitives.ReadUInt32LittleEndian(frame.Payload.AsSpan(16)),
            message);
    }

    public static BrokerFrame EncodeEventBatch(BrokerEventBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Events.Count is < 1 or > BrokerProtocol.MaximumEventsPerBatch)
        {
            throw new ArgumentOutOfRangeException(nameof(batch), "Event batch size is invalid.");
        }
        var payload = new byte[checked(8 + batch.Events.Count * BrokerProtocol.EventWireSize)];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, checked((uint)batch.Events.Count));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), BrokerProtocol.EventWireSize);
        for (var index = 0; index < batch.Events.Count; index++)
        {
            WriteEvent(payload.AsSpan(8 + index * BrokerProtocol.EventWireSize), batch.Events[index]);
        }
        return new BrokerFrame(BrokerMessageType.EventBatch, 0, payload);
    }

    public static BrokerEventBatch DecodeEventBatch(BrokerFrame frame)
    {
        RequireTypeAndNotification(frame, BrokerMessageType.EventBatch);
        if (frame.Payload.Length < 8)
        {
            throw new BrokerProtocolException("Event batch payload is truncated.");
        }
        var count = BinaryPrimitives.ReadUInt32LittleEndian(frame.Payload);
        var wireSize = BinaryPrimitives.ReadUInt32LittleEndian(frame.Payload.AsSpan(4));
        if (count is < 1 or > BrokerProtocol.MaximumEventsPerBatch ||
            wireSize != BrokerProtocol.EventWireSize ||
            frame.Payload.Length != checked(8 + (int)count * BrokerProtocol.EventWireSize))
        {
            throw new BrokerProtocolException("Event batch metadata is invalid.");
        }
        var events = new DriverInputEvent[count];
        for (var index = 0; index < events.Length; index++)
        {
            events[index] = ReadEvent(frame.Payload.AsSpan(8 + index * BrokerProtocol.EventWireSize));
        }
        return new BrokerEventBatch(events);
    }

    public static void ValidateFrame(BrokerFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!Enum.IsDefined(frame.Type) || frame.Payload is null ||
            frame.Payload.Length > BrokerProtocol.MaximumPayloadSize)
        {
            throw new BrokerProtocolException("Broker frame envelope is invalid.");
        }
    }

    private static void WriteRule(Span<byte> target, KeyboardRule rule)
    {
        rule.DeviceHash.Span.CopyTo(target);
        BinaryPrimitives.WriteUInt16LittleEndian(target[16..], rule.MakeCode);
        BinaryPrimitives.WriteUInt16LittleEndian(target[18..], rule.RequiredFlags);
        BinaryPrimitives.WriteUInt16LittleEndian(target[20..], rule.IgnoredFlags);
        BinaryPrimitives.WriteUInt32LittleEndian(target[24..], (uint)rule.Flags);
        BinaryPrimitives.WriteUInt64LittleEndian(target[28..], rule.RuleId);
    }

    private static KeyboardRule ReadRule(ReadOnlySpan<byte> source) => new(
        ReadRuleHash(source),
        BinaryPrimitives.ReadUInt16LittleEndian(source[16..]),
        BinaryPrimitives.ReadUInt16LittleEndian(source[18..]),
        BinaryPrimitives.ReadUInt16LittleEndian(source[20..]),
        (KeyboardRuleFlags)BinaryPrimitives.ReadUInt32LittleEndian(source[24..]),
        BinaryPrimitives.ReadUInt64LittleEndian(source[28..]));

    private static byte[] ReadRuleHash(ReadOnlySpan<byte> source)
    {
        if (BinaryPrimitives.ReadUInt16LittleEndian(source[22..]) != 0)
        {
            throw new BrokerProtocolException("Keyboard rule reserved field must be zero.");
        }
        return source[..KeyPilotDriverClient.DeviceHashLength].ToArray();
    }

    private static void WriteEvent(Span<byte> target, DriverInputEvent input)
    {
        if (input.Sequence == 0 || input.Generation == 0 || input.RuleId == 0 ||
            input.DeviceHash.Length != KeyPilotDriverClient.DeviceHashLength ||
            !Enum.IsDefined(input.Phase))
        {
            throw new ArgumentException("Driver event is invalid.", nameof(input));
        }
        BinaryPrimitives.WriteUInt64LittleEndian(target, input.Generation);
        BinaryPrimitives.WriteUInt64LittleEndian(target[8..], input.RuleId);
        BinaryPrimitives.WriteUInt64LittleEndian(target[16..], input.Sequence);
        input.DeviceHash.AsSpan().CopyTo(target[24..]);
        BinaryPrimitives.WriteUInt16LittleEndian(target[40..], input.MakeCode);
        BinaryPrimitives.WriteUInt16LittleEndian(target[42..], input.Flags);
        BinaryPrimitives.WriteUInt16LittleEndian(target[44..], input.VirtualKey);
        BinaryPrimitives.WriteUInt16LittleEndian(target[46..], (ushort)input.Phase);
        BinaryPrimitives.WriteUInt32LittleEndian(target[48..], input.UnitId);
        // Bytes 52..59 are reserved.
    }

    private static DriverInputEvent ReadEvent(ReadOnlySpan<byte> source)
    {
        if (BinaryPrimitives.ReadUInt64LittleEndian(source[52..]) != 0)
        {
            throw new BrokerProtocolException("Event reserved bytes must be zero.");
        }
        var phase = (DriverInputPhase)BinaryPrimitives.ReadUInt16LittleEndian(source[46..]);
        var generation = BinaryPrimitives.ReadUInt64LittleEndian(source);
        var ruleId = BinaryPrimitives.ReadUInt64LittleEndian(source[8..]);
        var sequence = BinaryPrimitives.ReadUInt64LittleEndian(source[16..]);
        var rawUnitId = BinaryPrimitives.ReadUInt32LittleEndian(source[48..]);
        if (generation == 0 || ruleId == 0 || sequence == 0 || !Enum.IsDefined(phase) ||
            rawUnitId > ushort.MaxValue)
        {
            throw new BrokerProtocolException("Event payload is invalid.");
        }
        return new DriverInputEvent(
            generation,
            ruleId,
            sequence,
            source.Slice(24, KeyPilotDriverClient.DeviceHashLength).ToArray(),
            BinaryPrimitives.ReadUInt16LittleEndian(source[40..]),
            BinaryPrimitives.ReadUInt16LittleEndian(source[42..]),
            BinaryPrimitives.ReadUInt16LittleEndian(source[44..]),
            phase,
            checked((ushort)rawUnitId));
    }

    private static async ValueTask ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Broker pipe disconnected during a frame.");
            }
            offset += read;
        }
    }

    private static void Require(
        BrokerFrame frame,
        BrokerMessageType type,
        int payloadLength,
        bool requestCorrelation)
    {
        ValidateFrame(frame);
        if (frame.Type != type || frame.Payload.Length != payloadLength ||
            (requestCorrelation ? frame.CorrelationId == 0 : frame.CorrelationId != 0))
        {
            throw new BrokerProtocolException($"{type} frame shape is invalid.");
        }
    }

    private static void RequireTypeAndRequest(BrokerFrame frame, BrokerMessageType type)
    {
        ValidateFrame(frame);
        if (frame.Type != type || frame.CorrelationId == 0)
        {
            throw new BrokerProtocolException($"{type} request envelope is invalid.");
        }
    }

    private static void RequireTypeAndNotification(BrokerFrame frame, BrokerMessageType type)
    {
        ValidateFrame(frame);
        if (frame.Type != type || frame.CorrelationId != 0)
        {
            throw new BrokerProtocolException($"{type} notification envelope is invalid.");
        }
    }

    private static void RequireRequestCorrelation(ulong correlationId)
    {
        if (correlationId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(correlationId));
        }
    }
}
