using System.Collections.Concurrent;
using System.Threading.Channels;
using KeyPilot.App.Presentation;
using KeyPilot.Core.Actions;
using KeyPilot.Core.Configuration;
using KeyPilot.Core.Input;
using KeyPilot.DriverBroker.Protocol;
using KeyPilot.DriverClient;

namespace KeyPilot.Platform.Windows.Tests;

internal static class BrokerDriverSuppressionCoordinatorTests
{
    public static async Task CommitAndCompletionWatermarksStaySeparateAsync()
    {
        var connection = new FakeBrokerConnection();
        var statuses = new ConcurrentQueue<DriverSuppressionStatus>();
        using var coordinator = new BrokerDriverSuppressionCoordinator(_ => Task.FromResult<IBrokerSuppressionConnection>(connection));
        coordinator.StatusChanged += (_, status) => statuses.Enqueue(status);
        var delivered = new TaskCompletionSource<DriverSuppressionInputEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.InputReceived += (_, args) =>
        {
            coordinator.ReportInputCommitted(
                args.ConnectionEpoch,
                args.DriverSequence,
                new MappingInputCommit(true, 1, null),
                args.PolicyRevision);
            delivered.TrySetResult(args);
        };
        coordinator.ApplyConfiguration(Configuration(), runtimeRevision: 301);
        coordinator.Start();
        connection.SendStatus(BrokerStatusCode.LeaseActive, maximumRules: 64);

        var configured = await connection.Configured.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => coordinator.IsActive, "Broker policy never became active.");
        var rule = configured.Rules.Single();
        connection.SendEvents(new DriverInputEvent(
            configured.Generation,
            rule.RuleId,
            1,
            new byte[KeyPilotDriverClient.DeviceHashLength],
            rule.MakeCode,
            rule.RequiredFlags,
            0,
            DriverInputPhase.Down,
            0));
        var input = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Require(input.PolicyRevision == 1, "Broker input did not carry its policy revision.");
        Require(input.RuntimeRevision == 301, "Broker input did not carry its runtime revision.");

        connection.SendChallenge(101, 1);
        var first = await TakeHealthAckAsync(connection, statuses);
        Require(first.LastCommittedEventSequence == 1, "State-machine commit did not advance.");
        Require(first.LastCompletedActionSequence == 0, "Commit incorrectly acknowledged action completion.");
        Require(
            (first.Flags & BrokerHealthFlags.ActionProgressVerified) != 0,
            "A pending action must carry real progress proof.");

        coordinator.ReportInputCompletion(
            input.ConnectionEpoch,
            input.DriverSequence,
            new MappingInputCompletion(true, 2, null),
            input.PolicyRevision);
        connection.SendChallenge(102, 1);
        var second = await TakeHealthAckAsync(connection, statuses);
        Require(second.LastCompletedActionSequence == 1, "Successful action did not advance completion.");
        Require(
            (second.Flags & BrokerHealthFlags.ActionProgressVerified) == 0,
            "A fully completed pipeline does not need an in-progress claim.");
    }

    public static async Task SupersededPolicyReportsFailOpenWithoutAckAsync()
    {
        var connection = new FakeBrokerConnection();
        var delivered = new TaskCompletionSource<DriverSuppressionInputEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = new TaskCompletionSource<DriverSuppressionStatus>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new BrokerDriverSuppressionCoordinator(
            _ => Task.FromResult<IBrokerSuppressionConnection>(connection));
        coordinator.InputReceived += (_, args) => delivered.TrySetResult(args);
        coordinator.StatusChanged += (_, status) =>
        {
            if (status.State == DriverSuppressionState.FailOpen)
            {
                failed.TrySetResult(status);
            }
        };

        coordinator.ApplyConfiguration(Configuration(), runtimeRevision: 401);
        coordinator.Start();
        connection.SendStatus(BrokerStatusCode.LeaseActive, maximumRules: 64);
        var configured = await connection.Configured.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => coordinator.IsActive, "Broker policy never became active.");
        var rule = configured.Rules.Single();
        connection.SendEvents(new DriverInputEvent(
            configured.Generation,
            rule.RuleId,
            1,
            new byte[KeyPilotDriverClient.DeviceHashLength],
            rule.MakeCode,
            rule.RequiredFlags,
            0,
            DriverInputPhase.Down,
            0));
        var input = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Require(input.PolicyRevision == 1 && input.RuntimeRevision == 401,
            "Broker input did not retain its policy/runtime revisions.");

        coordinator.ApplyConfiguration(Configuration(), runtimeRevision: 402);
        Require(coordinator.ReportInputCommitted(
            input.ConnectionEpoch,
            input.DriverSequence,
            new MappingInputCommit(true, 1, null),
            input.PolicyRevision), "The stale commit should reach the worker and force fail-open.");
        Require(coordinator.ReportInputCompletion(
            input.ConnectionEpoch,
            input.DriverSequence,
            new MappingInputCompletion(true, 2, null),
            input.PolicyRevision), "The stale completion should reach the worker and force fail-open.");

        await failed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => connection.Disposed, "Superseded broker reports did not close IPC.");
        Require(connection.HealthAcknowledgements.IsEmpty,
            "A superseded broker report must never produce a successful health ACK.");
    }

    public static async Task BoundedWaitLivenessSurvivesRepeatedChallengesAsync()
    {
        var connection = new FakeBrokerConnection();
        var statuses = new ConcurrentQueue<DriverSuppressionStatus>();
        long progress = 1;
        using var coordinator = new BrokerDriverSuppressionCoordinator(
            _ => Task.FromResult<IBrokerSuppressionConnection>(connection));
        coordinator.StatusChanged += (_, status) => statuses.Enqueue(status);
        coordinator.InputReceived += (_, args) => coordinator.ReportInputCommitted(
            args.ConnectionEpoch,
            args.DriverSequence,
            new MappingInputCommit(true, 1, null)
            {
                VerifyLivenessAsync = _ => Task.FromResult(Interlocked.Increment(ref progress))
            });
        coordinator.ApplyConfiguration(Configuration());
        coordinator.Start();
        connection.SendStatus(BrokerStatusCode.LeaseActive, maximumRules: 64);
        var configured = await connection.Configured.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => coordinator.IsActive, "Broker policy never became active.");
        var rule = configured.Rules.Single();
        connection.SendEvents(new DriverInputEvent(
            configured.Generation,
            rule.RuleId,
            1,
            new byte[KeyPilotDriverClient.DeviceHashLength],
            rule.MakeCode,
            rule.RequiredFlags,
            0,
            DriverInputPhase.Down,
            0));

        connection.SendChallenge(201, 1);
        _ = await TakeHealthAckAsync(connection, statuses);
        for (ulong challenge = 202; challenge <= 204; challenge++)
        {
            await Task.Delay(400);
            connection.SendChallenge(challenge, 1);
            var acknowledgement = await TakeHealthAckAsync(connection, statuses);
            Require(acknowledgement.LastCompletedActionSequence == 0,
                "A liveness proof must not advance the action-completion watermark.");
            Require((acknowledgement.Flags & BrokerHealthFlags.ActionProgressVerified) != 0,
                "A valid bounded wait must answer each repeated health challenge.");
        }
    }

    public static async Task FailedActionClosesBrokerAndFailsOpenAsync()
    {
        var connection = new FakeBrokerConnection();
        var failed = new TaskCompletionSource<DriverSuppressionStatus>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new BrokerDriverSuppressionCoordinator(_ => Task.FromResult<IBrokerSuppressionConnection>(connection));
        coordinator.StatusChanged += (_, status) =>
        {
            if (status.State == DriverSuppressionState.FailOpen)
            {
                failed.TrySetResult(status);
            }
        };
        coordinator.InputReceived += (_, args) =>
        {
            coordinator.ReportInputCommitted(
                args.ConnectionEpoch,
                args.DriverSequence,
                new MappingInputCommit(true, 1, null));
            coordinator.ReportInputCompletion(
                args.ConnectionEpoch,
                args.DriverSequence,
                new MappingInputCompletion(false, 2, new IOException("action failed")));
        };
        coordinator.ApplyConfiguration(Configuration());
        coordinator.Start();
        connection.SendStatus(BrokerStatusCode.LeaseActive, maximumRules: 64);
        var configured = await connection.Configured.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => coordinator.IsActive, "Broker policy never became active.");
        var rule = configured.Rules.Single();
        connection.SendEvents(new DriverInputEvent(
            configured.Generation,
            rule.RuleId,
            1,
            new byte[KeyPilotDriverClient.DeviceHashLength],
            rule.MakeCode,
            rule.RequiredFlags,
            0,
            DriverInputPhase.Down,
            0));

        var status = await failed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Require(status.Message.Contains("fail-open", StringComparison.OrdinalIgnoreCase),
            "Failure status did not explicitly say fail-open.");
        await WaitUntilAsync(() => connection.Disposed, "Failed action did not close broker IPC.");
    }

    public static async Task MissingProtectedInstallationIsExplicitlyFailOpenAsync()
    {
        var statusSource = new TaskCompletionSource<DriverSuppressionStatus>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new BrokerDriverSuppressionCoordinator(
            _ => Task.FromException<IBrokerSuppressionConnection>(
                new FileNotFoundException("protected broker missing")));
        coordinator.StatusChanged += (_, status) =>
        {
            if (status.State == DriverSuppressionState.DriverNotInstalled)
            {
                statusSource.TrySetResult(status);
            }
        };
        coordinator.ApplyConfiguration(Configuration());
        coordinator.Start();

        var status = await statusSource.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Require(status.Message.Contains("fail-open", StringComparison.OrdinalIgnoreCase),
            "Missing broker status did not explicitly preserve fail-open.");
        Require(!coordinator.IsActive, "Missing broker must never claim suppression ownership.");
    }

    public static async Task DisabledPolicyNeverConnectsAndReleasesActiveBrokerAsync()
    {
        var connection = new FakeBrokerConnection();
        var connectionCount = 0;
        using var coordinator = new BrokerDriverSuppressionCoordinator(_ =>
        {
            Interlocked.Increment(ref connectionCount);
            return Task.FromResult<IBrokerSuppressionConnection>(connection);
        });

        coordinator.ApplyConfiguration(Configuration() with { IsMappingEnabled = false });
        coordinator.Start();
        await Task.Delay(50);
        Require(connectionCount == 0, "A disabled policy must not connect to or elevate the broker.");

        coordinator.ApplyConfiguration(Configuration());
        await WaitUntilAsync(() => connectionCount == 1, "Explicit enable did not start one broker attempt.");
        connection.SendStatus(BrokerStatusCode.LeaseActive, maximumRules: 64);
        await connection.Configured.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => coordinator.IsActive, "Enabled policy never became active.");

        coordinator.ApplyConfiguration(Configuration() with { IsMappingEnabled = false });
        await WaitUntilAsync(() => connection.Disposed, "Disabling did not close broker IPC and release the lease.");
        Require(!coordinator.IsActive, "A disabled policy must immediately relinquish suppression ownership.");
        Require(connectionCount == 1, "Disabling must not create a replacement broker connection.");
    }

    public static async Task DriverProbePreventsUselessBrokerLaunchAsync()
    {
        var connectionCount = 0;
        var statusSource = new TaskCompletionSource<DriverSuppressionStatus>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new BrokerDriverSuppressionCoordinator(
            _ =>
            {
                Interlocked.Increment(ref connectionCount);
                return Task.FromResult<IBrokerSuppressionConnection>(new FakeBrokerConnection());
            },
            driverProbe: () => InstalledDriverPresence.Missing);
        coordinator.StatusChanged += (_, status) =>
        {
            if (status.State == DriverSuppressionState.DriverNotInstalled)
            {
                statusSource.TrySetResult(status);
            }
        };
        coordinator.ApplyConfiguration(Configuration());
        coordinator.Start();

        await statusSource.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Require(connectionCount == 0, "A missing driver device must be detected before broker launch/UAC.");
        Require(InstalledDriverProbe.ClassifyOpenError(5) == InstalledDriverPresence.Available, "Access denied must prove device presence.");
        Require(InstalledDriverProbe.ClassifyOpenError(2) == InstalledDriverPresence.Missing, "File not found must classify the device as missing.");
    }

    public static async Task FailureRequiresExplicitDisableBeforeRetryAsync()
    {
        var connectionCount = 0;
        var secondConnection = new FakeBrokerConnection();
        var failed = new TaskCompletionSource<DriverSuppressionStatus>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new BrokerDriverSuppressionCoordinator(_ =>
        {
            var attempt = Interlocked.Increment(ref connectionCount);
            return attempt == 1
                ? Task.FromException<IBrokerSuppressionConnection>(new IOException("synthetic broker failure"))
                : Task.FromResult<IBrokerSuppressionConnection>(secondConnection);
        });
        coordinator.StatusChanged += (_, status) =>
        {
            if (status.State == DriverSuppressionState.FailOpen)
            {
                failed.TrySetResult(status);
            }
        };
        coordinator.ApplyConfiguration(Configuration());
        coordinator.Start();
        var failureStatus = await failed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Require(failureStatus.Message.Contains("关闭再开启", StringComparison.Ordinal), "Failure must explain the explicit retry gesture.");

        coordinator.ApplyConfiguration(Configuration());
        await Task.Delay(50);
        Require(connectionCount == 1, "An enabled-to-enabled update must not silently reacquire a failed lease.");

        coordinator.ApplyConfiguration(Configuration() with { IsMappingEnabled = false });
        coordinator.ApplyConfiguration(Configuration());
        await WaitUntilAsync(() => connectionCount == 2, "Explicit off-to-on did not permit one new broker attempt.");
        secondConnection.SendStatus(BrokerStatusCode.LeaseActive, maximumRules: 64);
        await secondConnection.Configured.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => coordinator.IsActive, "The explicit retry did not recover the broker policy.");
    }

    private static KeyPilotConfiguration Configuration()
    {
        var mapping = new InputMapping
        {
            Name = "broker test",
            Source = new InputSource
            {
                Device = new InputDeviceSelector
                {
                    Kind = InputDeviceKind.Keyboard,
                    MatchMode = DeviceMatchMode.AnyOfKind
                },
                Control = new InputControlId
                {
                    Kind = InputControlKind.KeyboardScanCode,
                    Code = 0x1E,
                    RawQualifier = "RAWKEYBOARD-V1;PREFIX=0000"
                }
            },
            Trigger = new MappingTrigger { Kind = MappingTriggerKind.KeyDown },
            Action = new SendKeyAction
            {
                Target = new InputControlId { Kind = InputControlKind.VirtualKey, Code = 0x42 }
            }
        };
        var profile = new MappingProfile { Name = "active", Mappings = { mapping } };
        return new KeyPilotConfiguration
        {
            IsMappingEnabled = true,
            ActiveProfileId = profile.Id,
            Profiles = { profile }
        };
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, string message)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!predicate())
        {
            try
            {
                await Task.Delay(5, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException(message);
            }
        }
    }

    private static async Task<BrokerHealthAck> TakeHealthAckAsync(
        FakeBrokerConnection connection,
        ConcurrentQueue<DriverSuppressionStatus> statuses)
    {
        try
        {
            return await connection.TakeHealthAckAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Health ACK failed. Statuses: {string.Join(" | ", statuses.Select(item => item.Message))}",
                exception);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class FakeBrokerConnection : IBrokerSuppressionConnection
    {
        private readonly Channel<BrokerFrame> _notifications = Channel.CreateUnbounded<BrokerFrame>();
        private readonly Channel<BrokerHealthAck> _healthAcks = Channel.CreateUnbounded<BrokerHealthAck>();

        public TaskCompletionSource<BrokerConfigureRules> Configured { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Disposed { get; private set; }

        public ConcurrentQueue<BrokerHealthAck> HealthAcknowledgements { get; } = new();

        public Task<ulong> ConfigureRulesAsync(
            ulong generation,
            IReadOnlyList<KeyboardRule> rules,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Configured.TrySetResult(new BrokerConfigureRules(generation, rules.ToArray()));
            return Task.FromResult(generation);
        }

        public Task AcknowledgeHealthAsync(
            BrokerHealthAck acknowledgement,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HealthAcknowledgements.Enqueue(acknowledgement);
            return _healthAcks.Writer.WriteAsync(acknowledgement, cancellationToken).AsTask();
        }

        public Task<BrokerFrame> ReadMessageAsync(CancellationToken cancellationToken) =>
            _notifications.Reader.ReadAsync(cancellationToken).AsTask();

        public void SendStatus(BrokerStatusCode code, uint maximumRules) =>
            _notifications.Writer.TryWrite(BrokerFrameCodec.EncodeStatus(new BrokerStatus(
                code,
                DriverStateFlags.LeaseActive,
                0,
                maximumRules,
                "fake")));

        public void SendEvents(params DriverInputEvent[] events) =>
            _notifications.Writer.TryWrite(BrokerFrameCodec.EncodeEventBatch(new BrokerEventBatch(events)));

        public void SendChallenge(ulong challengeId, ulong sequence) =>
            _notifications.Writer.TryWrite(BrokerFrameCodec.EncodeHealthChallenge(
                new BrokerHealthChallenge(challengeId, sequence, 500)));

        public async Task<BrokerHealthAck> TakeHealthAckAsync(TimeSpan timeout)
        {
            using var cancellation = new CancellationTokenSource(timeout);
            return await _healthAcks.Reader.ReadAsync(cancellation.Token);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _notifications.Writer.TryComplete();
            _healthAcks.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
