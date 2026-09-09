using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Threading.Channels;
using KeyPilot.DriverBroker;
using KeyPilot.DriverBroker.Protocol;
using KeyPilot.DriverClient;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Launch arguments round trip and reject extras", TestOptions),
    ("Codec round trips bounded protocol records", TestCodecRoundTrip),
    ("Codec rejects reserved and oversized data", TestCodecRejection),
    ("Progress gate requires commit and action progress", TestProgressGate),
    ("Handshake binds the pipe's observed client PID", TestClientIdentity),
    ("UI verifies launched broker PID and server reserves first pipe", TestPipeOwnership),
    ("Broker accepts only the compiled driver version", TestDriverCompatibility),
    ("Fake session configures rules only after a challenged heartbeat", TestSessionHappyPath),
    ("Action progress never advances kernel safe acknowledgement", TestActionProgressDoesNotAdvanceKernelAck),
    ("Fake session rejects stale health acknowledgement", TestSessionRejectsStaleAck),
    ("Fake session stops on transport disconnect", TestSessionDisconnect),
    ("Fake session stops when parent UI exits", TestSessionParentExit),
    ("Fake session releases lease on health ACK timeout", TestSessionAckTimeout),
};

var failures = 0;
foreach (var (name, run) in tests)
{
    try
    {
        await run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {name}: {exception}");
    }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} broker tests passed.");
return failures == 0 ? 0 : 1;

static Task TestOptions()
{
    var launch = BrokerLaunchParameters.CreateForCurrentProcess();
    var parsed = BrokerOptions.Parse(launch.ToArgumentList().ToArray());
    Assert(parsed.Launch.PipeName == launch.PipeName, "Pipe name changed during parse.");
    Assert(parsed.Launch.Token.SequenceEqual(launch.Token), "Token changed during parse.");
    AssertThrows<ArgumentException>(() => BrokerOptions.Parse(
        launch.ToArgumentList().Append("--extra").Append("value").ToArray()));
    var duplicate = launch.ToArgumentList().ToArray();
    duplicate[2] = "--pipe";
    AssertThrows<ArgumentException>(() => BrokerOptions.Parse(duplicate));
    return Task.CompletedTask;
}

static async Task TestCodecRoundTrip()
{
    var rule = KeyboardRule.ForEveryKeyboard(0x1e, KeyboardScanPrefix.None, 7);
    var configure = new BrokerConfigureRules(12, new[] { rule });
    var frame = BrokerFrameCodec.EncodeConfigureRules(configure, 3);
    await using var stream = new MemoryStream();
    await BrokerFrameCodec.WriteFrameAsync(stream, frame);
    stream.Position = 0;
    var decodedFrame = await BrokerFrameCodec.ReadFrameAsync(stream);
    var decoded = BrokerFrameCodec.DecodeConfigureRules(decodedFrame);
    Assert(decoded.Generation == 12 && decoded.Rules.Count == 1 && decoded.Rules[0].RuleId == 7,
        "Rules did not round trip.");

    var input = new DriverInputEvent(
        12, 7, 99, new byte[KeyPilotDriverClient.DeviceHashLength], 0x1e, 0, 0x41,
        DriverInputPhase.Down, 0);
    var events = BrokerFrameCodec.DecodeEventBatch(
        BrokerFrameCodec.EncodeEventBatch(new BrokerEventBatch(new[] { input })));
    Assert(events.Events.Single().Sequence == 99, "Event did not round trip.");
}

static async Task TestCodecRejection()
{
    var oversizedHeader = new byte[BrokerProtocol.HeaderSize];
    BinaryPrimitives.WriteUInt32LittleEndian(oversizedHeader, BrokerProtocol.Magic);
    BinaryPrimitives.WriteUInt16LittleEndian(oversizedHeader.AsSpan(4), BrokerProtocol.Version);
    BinaryPrimitives.WriteUInt16LittleEndian(oversizedHeader.AsSpan(6), (ushort)BrokerMessageType.Hello);
    BinaryPrimitives.WriteUInt32LittleEndian(
        oversizedHeader.AsSpan(8), BrokerProtocol.MaximumPayloadSize + 1u);
    await AssertThrowsAsync<BrokerProtocolException>(async () =>
        _ = await BrokerFrameCodec.ReadFrameAsync(new MemoryStream(oversizedHeader)));

    var ruleFrame = BrokerFrameCodec.EncodeConfigureRules(
        new BrokerConfigureRules(1, new[]
        {
            KeyboardRule.ForEveryKeyboard(0x1e, KeyboardScanPrefix.None, 1)
        }),
        1);
    ruleFrame.Payload[16 + 22] = 1;
    AssertThrows<BrokerProtocolException>(() => BrokerFrameCodec.DecodeConfigureRules(ruleFrame));
}

static Task TestProgressGate()
{
    var gate = new BrokerProgressGate(TimeProvider.System);
    var challenge = gate.Issue(10, TimeSpan.FromSeconds(1));
    AssertThrows<BrokerProtocolException>(() => gate.Accept(new BrokerHealthAck(
        challenge.ChallengeId,
        9,
        9,
        1,
        BrokerHealthFlags.StateMachineCommitted | BrokerHealthFlags.ActionQueueHealthy)));

    gate = new BrokerProgressGate(TimeProvider.System);
    challenge = gate.Issue(10, TimeSpan.FromSeconds(1));
    AssertThrows<BrokerProtocolException>(() => gate.Accept(new BrokerHealthAck(
        challenge.ChallengeId,
        10,
        0,
        0,
        BrokerHealthFlags.StateMachineCommitted | BrokerHealthFlags.ActionQueueHealthy |
        BrokerHealthFlags.ActionProgressVerified)));

    gate = new BrokerProgressGate(TimeProvider.System);
    challenge = gate.Issue(10, TimeSpan.FromSeconds(1));
    var acknowledged = gate.Accept(new BrokerHealthAck(
        challenge.ChallengeId,
        10,
        0,
        1,
        BrokerHealthFlags.StateMachineCommitted | BrokerHealthFlags.ActionQueueHealthy |
        BrokerHealthFlags.ActionProgressVerified));
    Assert(acknowledged == 0 && !gate.IsPending,
        "Action progress incorrectly advanced the kernel-safe acknowledgement.");
    return Task.CompletedTask;
}

static Task TestPipeOwnership()
{
    BrokerServerProcessIdentity.Validate(4242, 4242);
    AssertThrows<UnauthorizedAccessException>(() =>
        BrokerServerProcessIdentity.Validate(4242, 4343));
    AssertThrows<ArgumentOutOfRangeException>(() =>
        BrokerServerProcessIdentity.Validate(0, 4242));
    Assert(
        (BrokerServer.SecurePipeOptions & PipeOptions.FirstPipeInstance) != 0,
        "The elevated broker must refuse a pre-existing server instance for its random pipe name.");
    Assert(
        (BrokerServer.SecurePipeOptions & PipeOptions.CurrentUserOnly) != 0,
        "The broker pipe must remain restricted to the current user.");
    return Task.CompletedTask;
}

static Task TestClientIdentity()
{
    var launch = BrokerLaunchParameters.CreateForCurrentProcess();
    var hello = new BrokerHello(
        launch.ParentProcessId,
        launch.ParentStartTimeUtcTicks,
        launch.Token.ToArray());
    BrokerClientIdentity.Validate(hello, launch, launch.ParentProcessId, parentHasExited: false);
    AssertThrows<UnauthorizedAccessException>(() =>
        BrokerClientIdentity.Validate(hello, launch, launch.ParentProcessId + 1, parentHasExited: false));
    AssertThrows<UnauthorizedAccessException>(() =>
        BrokerClientIdentity.Validate(hello, launch, launch.ParentProcessId, parentHasExited: true));
    hello.Token[0] ^= 0xff;
    AssertThrows<UnauthorizedAccessException>(() =>
        BrokerClientIdentity.Validate(hello, launch, launch.ParentProcessId, parentHasExited: false));
    return Task.CompletedTask;
}

static Task TestDriverCompatibility()
{
    BrokerDriverCompatibility.Validate(new DriverCapabilities(
        KeyPilotDriverClient.DriverVersionMajor,
        KeyPilotDriverClient.DriverVersionMinor,
        KeyPilotDriverClient.MaximumRules,
        DriverStateFlags.FailOpen,
        0));
    AssertThrows<InvalidDataException>(() => BrokerDriverCompatibility.Validate(new DriverCapabilities(
        1,
        0,
        KeyPilotDriverClient.MaximumRules,
        DriverStateFlags.FailOpen,
        0)));
    return Task.CompletedTask;
}

static async Task TestSessionHappyPath()
{
    var transport = new FakeTransport();
    var driver = new FakeDriver();
    var parent = new FakeParent();
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    var session = CreateSession(transport, driver, parent);
    var run = session.RunAsync(cancellation.Token);

    var healthFrame = await transport.TakeWrittenAsync(cancellation.Token);
    var health = BrokerFrameCodec.DecodeHealthChallenge(healthFrame);
    await transport.SendAsync(BrokerFrameCodec.EncodeHealthAck(
        new BrokerHealthAck(
            health.ChallengeId,
            health.LastDispatchedEventSequence,
            health.LastDispatchedEventSequence,
            0,
            BrokerHealthFlags.StateMachineCommitted | BrokerHealthFlags.ActionQueueHealthy),
        health.ChallengeId));
    await WaitUntilAsync(() => driver.Heartbeats.Count == 1, cancellation.Token);
    Assert(driver.Heartbeats.Single() == 0, "Heartbeat did not use the acknowledged sequence.");

    var rule = KeyboardRule.ForEveryKeyboard(0x1e, KeyboardScanPrefix.None, 1);
    await transport.SendAsync(BrokerFrameCodec.EncodeConfigureRules(
        new BrokerConfigureRules(1, new[] { rule }), 2));
    var configureAck = await TakeUntilAsync(
        transport,
        frame => frame.Type == BrokerMessageType.Ack && frame.CorrelationId == 2,
        cancellation.Token);
    Assert(BrokerFrameCodec.DecodeAck(configureAck).AppliedGeneration == 1,
        "Configuration was not acknowledged.");
    Assert(driver.AppliedGeneration == 1 && driver.Rules.Count == 1,
        "Configuration did not reach the fake driver.");

    await transport.SendAsync(BrokerFrameCodec.EncodeShutdown(3));
    _ = await TakeUntilAsync(
        transport,
        frame => frame.Type == BrokerMessageType.Ack && frame.CorrelationId == 3,
        cancellation.Token);
    await run.WaitAsync(cancellation.Token);
    Assert(driver.Disposed, "Shutdown did not dispose the driver lease owner.");
}

static async Task TestActionProgressDoesNotAdvanceKernelAck()
{
    var transport = new FakeTransport();
    var driver = new FakeDriver { StagnantHeartbeatLimit = 2 };
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    var run = CreateSession(transport, driver, new FakeParent()).RunAsync(cancellation.Token);

    async ValueTask AcknowledgeIdleChallengeAsync(BrokerFrame frame)
    {
        if (frame.Type == BrokerMessageType.Status)
        {
            return;
        }
        Assert(frame.Type == BrokerMessageType.HealthChallenge,
            $"Unexpected broker frame while waiting for action-test setup: {frame.Type}.");
        var challenge = BrokerFrameCodec.DecodeHealthChallenge(frame);
        Assert(challenge.LastDispatchedEventSequence == 0,
            "The test must not treat a dispatched action challenge as idle.");
        await transport.SendAsync(BrokerFrameCodec.EncodeHealthAck(
            HealthyAck(challenge, completed: 0, progress: 0), challenge.ChallengeId));
    }

    var initial = BrokerFrameCodec.DecodeHealthChallenge(
        await transport.TakeWrittenAsync(cancellation.Token));
    await transport.SendAsync(BrokerFrameCodec.EncodeHealthAck(
        HealthyAck(initial, completed: 0, progress: 0), initial.ChallengeId));
    await WaitUntilAsync(() => driver.Heartbeats.Count == 1, cancellation.Token);

    var rule = KeyboardRule.ForEveryKeyboard(0x1e, KeyboardScanPrefix.None, 1);
    await transport.SendAsync(BrokerFrameCodec.EncodeConfigureRules(
        new BrokerConfigureRules(1, new[] { rule }), 2));
    _ = await TakeUntilAsync(
        transport,
        frame => frame.Type == BrokerMessageType.Ack && frame.CorrelationId == 2,
        cancellation.Token,
        AcknowledgeIdleChallengeAsync);
    driver.EventBatches.Enqueue(new[]
    {
        new DriverInputEvent(
            1, 1, 1, new byte[KeyPilotDriverClient.DeviceHashLength], 0x1e, 0, 0x41,
            DriverInputPhase.Down, 0)
    });
    _ = await TakeUntilAsync(
        transport,
        frame => frame.Type == BrokerMessageType.EventBatch,
        cancellation.Token,
        AcknowledgeIdleChallengeAsync);

    var firstProgress = BrokerFrameCodec.DecodeHealthChallenge(await TakeUntilAsync(
        transport,
        frame => frame.Type == BrokerMessageType.HealthChallenge,
        cancellation.Token));
    Assert(firstProgress.LastDispatchedEventSequence == 1, "Challenge missed the dispatched event.");
    var heartbeatsBeforeProgress = driver.Heartbeats.Count;
    await transport.SendAsync(BrokerFrameCodec.EncodeHealthAck(
        HealthyAck(firstProgress, completed: 0, progress: 1), firstProgress.ChallengeId));
    await WaitUntilAsync(
        () => driver.Heartbeats.Count == heartbeatsBeforeProgress + 1,
        cancellation.Token);
    Assert(driver.Heartbeats.Last() == 0,
        "Action progress was incorrectly reported to the kernel as completion.");

    var secondProgress = BrokerFrameCodec.DecodeHealthChallenge(await TakeUntilAsync(
        transport,
        frame => frame.Type == BrokerMessageType.HealthChallenge,
        cancellation.Token));
    await transport.SendAsync(BrokerFrameCodec.EncodeHealthAck(
        HealthyAck(secondProgress, completed: 0, progress: 2), secondProgress.ChallengeId));
    await AssertThrowsAsync<InvalidDataException>(async () => await run.WaitAsync(cancellation.Token));
    Assert(driver.Heartbeats.All(sequence => sequence == 0),
        "A stagnant action advanced the kernel acknowledgement.");
    Assert(driver.Disposed, "Progress timeout did not dispose the driver lease owner.");
}

static BrokerHealthAck HealthyAck(
    BrokerHealthChallenge challenge,
    ulong completed,
    ulong progress) => new(
        challenge.ChallengeId,
        challenge.LastDispatchedEventSequence,
        completed,
        progress,
        BrokerHealthFlags.StateMachineCommitted |
        BrokerHealthFlags.ActionQueueHealthy |
        (completed < challenge.LastDispatchedEventSequence
            ? BrokerHealthFlags.ActionProgressVerified
            : BrokerHealthFlags.None));

static async Task TestSessionRejectsStaleAck()
{
    var transport = new FakeTransport();
    var driver = new FakeDriver();
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    var run = CreateSession(transport, driver, new FakeParent()).RunAsync(cancellation.Token);
    var challenge = BrokerFrameCodec.DecodeHealthChallenge(
        await transport.TakeWrittenAsync(cancellation.Token));
    await transport.SendAsync(BrokerFrameCodec.EncodeHealthAck(
        new BrokerHealthAck(
            challenge.ChallengeId + 1,
            0,
            0,
            0,
            BrokerHealthFlags.StateMachineCommitted | BrokerHealthFlags.ActionQueueHealthy),
        challenge.ChallengeId + 1));
    await AssertThrowsAsync<BrokerProtocolException>(async () => await run.WaitAsync(cancellation.Token));
    Assert(driver.Heartbeats.Count == 0, "Stale acknowledgement reached the driver.");
    Assert(driver.Disposed, "Protocol failure did not dispose the driver lease owner.");
}

static async Task TestSessionDisconnect()
{
    var transport = new FakeTransport();
    var driver = new FakeDriver();
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    var run = CreateSession(transport, driver, new FakeParent()).RunAsync(cancellation.Token);
    _ = await transport.TakeWrittenAsync(cancellation.Token);
    transport.Disconnect();
    await AssertThrowsAsync<EndOfStreamException>(async () => await run.WaitAsync(cancellation.Token));
    Assert(driver.Heartbeats.Count == 0, "Disconnected UI caused an independent heartbeat.");
    Assert(driver.Disposed, "Pipe disconnect did not dispose the driver lease owner.");
}

static async Task TestSessionParentExit()
{
    var transport = new FakeTransport();
    var driver = new FakeDriver();
    var parent = new FakeParent { HasExited = true };
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    var run = CreateSession(transport, driver, parent).RunAsync(cancellation.Token);
    await AssertThrowsAsync<EndOfStreamException>(async () => await run.WaitAsync(cancellation.Token));
    Assert(driver.Disposed, "Parent exit did not dispose the driver lease owner.");
}

static async Task TestSessionAckTimeout()
{
    var transport = new FakeTransport();
    var driver = new FakeDriver();
    var session = new BrokerSession(
        transport,
        driver,
        new FakeParent(),
        driver.GetCapabilities(),
        new BrokerSessionTiming(
            TimeSpan.FromMilliseconds(2),
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(40),
            TimeSpan.FromMilliseconds(40)));
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    var run = session.RunAsync(cancellation.Token);
    _ = await transport.TakeWrittenAsync(cancellation.Token);
    await AssertThrowsAsync<TimeoutException>(async () => await run.WaitAsync(cancellation.Token));
    Assert(driver.Heartbeats.Count == 0, "A timer heartbeat bypassed the missing UI ACK.");
    Assert(driver.Disposed, "Health ACK timeout did not dispose the driver lease owner.");
}

static BrokerSession CreateSession(
    FakeTransport transport,
    FakeDriver driver,
    FakeParent parent) => new(
        transport,
        driver,
        parent,
        driver.GetCapabilities(),
        new BrokerSessionTiming(
            TimeSpan.FromMilliseconds(2),
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromMilliseconds(100)));

static async Task<BrokerFrame> TakeUntilAsync(
    FakeTransport transport,
    Func<BrokerFrame, bool> predicate,
    CancellationToken cancellationToken,
    Func<BrokerFrame, ValueTask>? handleSkippedFrame = null)
{
    while (true)
    {
        var frame = await transport.TakeWrittenAsync(cancellationToken);
        if (predicate(frame))
        {
            return frame;
        }
        if (handleSkippedFrame is not null)
        {
            await handleSkippedFrame(frame);
        }
    }
}

static async Task WaitUntilAsync(Func<bool> predicate, CancellationToken cancellationToken)
{
    while (!predicate())
    {
        await Task.Delay(2, cancellationToken);
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertThrows<T>(Action action) where T : Exception
{
    try
    {
        action();
    }
    catch (T)
    {
        return;
    }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

static async Task AssertThrowsAsync<T>(Func<Task> action) where T : Exception
{
    try
    {
        await action();
    }
    catch (T)
    {
        return;
    }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

internal sealed class FakeTransport : IBrokerTransport
{
    private readonly Channel<BrokerFrame> _incoming = Channel.CreateUnbounded<BrokerFrame>();
    private readonly Channel<BrokerFrame> _written = Channel.CreateUnbounded<BrokerFrame>();

    public async ValueTask<BrokerFrame> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _incoming.Reader.ReadAsync(cancellationToken);
        }
        catch (ChannelClosedException exception)
        {
            throw new EndOfStreamException("fake disconnect", exception);
        }
    }

    public ValueTask WriteAsync(BrokerFrame frame, CancellationToken cancellationToken) =>
        _written.Writer.WriteAsync(frame, cancellationToken);

    public ValueTask SendAsync(BrokerFrame frame) => _incoming.Writer.WriteAsync(frame);

    public ValueTask<BrokerFrame> TakeWrittenAsync(CancellationToken cancellationToken) =>
        _written.Reader.ReadAsync(cancellationToken);

    public void Disconnect() => _incoming.Writer.TryComplete(new EndOfStreamException("fake disconnect"));

    public ValueTask DisposeAsync()
    {
        _incoming.Writer.TryComplete();
        _written.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeDriver : IBrokerDriverClient
{
    public ConcurrentQueue<ulong> Heartbeats { get; } = new();
    public ConcurrentQueue<IReadOnlyList<DriverInputEvent>> EventBatches { get; } = new();
    public IReadOnlyList<KeyboardRule> Rules { get; private set; } = Array.Empty<KeyboardRule>();
    public ulong AppliedGeneration { get; private set; }
    public bool Disposed { get; private set; }
    public int StagnantHeartbeatLimit { get; init; } = int.MaxValue;
    private ulong _lastHeartbeat = ulong.MaxValue;
    private ulong _lastDeliveredSequence;
    private int _stagnantHeartbeats;

    public DriverCapabilities GetCapabilities()
    {
        var state = DriverStateFlags.LeaseActive |
            (Rules.Count > 0 ? DriverStateFlags.RulesActive : DriverStateFlags.FailOpen) |
            (_stagnantHeartbeats >= StagnantHeartbeatLimit
                ? DriverStateFlags.ProgressTimeout | DriverStateFlags.FailOpen
                : 0);
        return new DriverCapabilities(
            KeyPilotDriverClient.DriverVersionMajor,
            KeyPilotDriverClient.DriverVersionMinor,
            KeyPilotDriverClient.MaximumRules,
            state,
            AppliedGeneration);
    }

    public TimeSpan AcquireLease(TimeSpan requested) => requested;

    public void ReplaceRules(IReadOnlyList<KeyboardRule> rules, ulong generation)
    {
        Rules = rules.ToArray();
        AppliedGeneration = generation;
    }

    public void Heartbeat(ulong lastSuccessfullyDispatchedSequence)
    {
        Heartbeats.Enqueue(lastSuccessfullyDispatchedSequence);
        if (lastSuccessfullyDispatchedSequence >= _lastDeliveredSequence)
        {
            _lastHeartbeat = lastSuccessfullyDispatchedSequence;
            _stagnantHeartbeats = 0;
            return;
        }
        if (_lastHeartbeat == lastSuccessfullyDispatchedSequence)
        {
            _stagnantHeartbeats++;
        }
        else
        {
            _lastHeartbeat = lastSuccessfullyDispatchedSequence;
            _stagnantHeartbeats = 1;
        }
    }

    public IReadOnlyList<DriverInputEvent> ReadEvents(int maximumEvents)
    {
        if (!EventBatches.TryDequeue(out var events))
        {
            return Array.Empty<DriverInputEvent>();
        }
        if (events.Count > 0)
        {
            _lastDeliveredSequence = events[^1].Sequence;
        }
        return events;
    }

    public void Dispose() => Disposed = true;
}

internal sealed class FakeParent : IParentLifetime
{
    public bool HasExited { get; set; }

    public void Dispose()
    {
    }
}
