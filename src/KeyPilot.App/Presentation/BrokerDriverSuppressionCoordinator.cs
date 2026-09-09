using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using KeyPilot.Core.Configuration;
using KeyPilot.Core.Input;
using KeyPilot.DriverBroker.Protocol;
using KeyPilot.DriverClient;
using Microsoft.Win32.SafeHandles;

namespace KeyPilot.App.Presentation;

internal interface IBrokerSuppressionConnection : IAsyncDisposable
{
    Task<ulong> ConfigureRulesAsync(
        ulong generation,
        IReadOnlyList<KeyboardRule> rules,
        CancellationToken cancellationToken);

    Task AcknowledgeHealthAsync(
        BrokerHealthAck acknowledgement,
        CancellationToken cancellationToken);

    Task<BrokerFrame> ReadMessageAsync(CancellationToken cancellationToken);
}

internal sealed class KeyPilotBrokerSuppressionConnection(
    KeyPilotBrokerConnection connection,
    Process brokerProcess) : IBrokerSuppressionConnection
{
    public Task<ulong> ConfigureRulesAsync(
        ulong generation,
        IReadOnlyList<KeyboardRule> rules,
        CancellationToken cancellationToken) =>
        connection.ConfigureRulesAsync(generation, rules, cancellationToken);

    public Task AcknowledgeHealthAsync(
        BrokerHealthAck acknowledgement,
        CancellationToken cancellationToken) =>
        connection.AcknowledgeHealthAsync(acknowledgement, cancellationToken);

    public Task<BrokerFrame> ReadMessageAsync(CancellationToken cancellationToken) =>
        connection.ReadMessageAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            brokerProcess.Dispose();
        }
    }
}

internal sealed record BrokerInstallation(string ExecutablePath, byte[] ExpectedSha256)
{
    private const string ExecutableName = "KeyPilot.DriverBroker.exe";
    private const string DigestName = "KeyPilot.DriverBroker.sha256";

    public static BrokerInstallation LocateProtected()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "broker"),
            Path.Combine(programFiles, "KeyPilot", "broker")
        };

        foreach (var directory in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!IsProtectedProgramFilesDirectory(directory))
            {
                continue;
            }
            var executable = Path.Combine(directory, ExecutableName);
            var digestPath = Path.Combine(directory, DigestName);
            if (!File.Exists(executable) || !File.Exists(digestPath))
            {
                continue;
            }

            var digestText = File.ReadAllText(digestPath).Trim();
            byte[] digest;
            try
            {
                digest = Convert.FromHexString(digestText);
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException("The installed broker SHA-256 manifest is malformed.", exception);
            }

            if (digest.Length != 32)
            {
                throw new InvalidDataException("The installed broker SHA-256 manifest is incomplete.");
            }

            return new BrokerInstallation(executable, digest);
        }

        throw new FileNotFoundException(
            "The protected KeyPilot broker installation and SHA-256 manifest were not found.");
    }

    private static bool IsProtectedProgramFilesDirectory(string directory)
    {
        var fullDirectory = Path.GetFullPath(directory);
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetEnvironmentVariable("ProgramW6432") ?? string.Empty
        };
        return roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Any(root =>
            {
                var relative = Path.GetRelativePath(root, fullDirectory);
                return relative.Length > 0 && relative != "." && !Path.IsPathRooted(relative) &&
                    !relative.Equals("..", StringComparison.Ordinal) &&
                    !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
            });
    }
}

internal static class BrokerSuppressionConnectionFactory
{
    public static async Task<IBrokerSuppressionConnection> ConnectInstalledAsync(
        CancellationToken cancellationToken)
    {
        var installation = BrokerInstallation.LocateProtected();
        var launch = BrokerLaunchParameters.CreateForCurrentProcess();
        Process? process = null;
        try
        {
            process = BrokerProcessLauncher.StartElevated(
                installation.ExecutablePath,
                installation.ExpectedSha256,
                launch);
            var connection = await KeyPilotBrokerConnection.ConnectAsync(
                    launch,
                    checked((uint)process.Id),
                    TimeSpan.FromSeconds(10),
                    cancellationToken)
                .ConfigureAwait(false);
            return new KeyPilotBrokerSuppressionConnection(connection, process);
        }
        catch
        {
            process?.Dispose();
            throw;
        }
    }
}

internal enum InstalledDriverPresence
{
    Available,
    Missing
}

internal static class InstalledDriverProbe
{
    private const uint ShareRead = 0x00000001;
    private const uint ShareWrite = 0x00000002;
    private const uint OpenExisting = 3;

    public static InstalledDriverPresence Probe()
    {
        using var handle = NativeMethods.CreateFile(
            @"\\.\KeyPilot",
            desiredAccess: 0,
            ShareRead | ShareWrite,
            0,
            OpenExisting,
            flagsAndAttributes: 0,
            0);
        if (!handle.IsInvalid)
        {
            return InstalledDriverPresence.Available;
        }

        return ClassifyOpenError(Marshal.GetLastWin32Error());
    }

    internal static InstalledDriverPresence ClassifyOpenError(int errorCode) => errorCode switch
    {
        2 or 3 or 1060 => InstalledDriverPresence.Missing,
        // An access-denied open proves that the named device exists; the elevated broker may use it.
        5 => InstalledDriverPresence.Available,
        _ => throw new Win32Exception(errorCode, "Unable to probe the KeyPilot driver device.")
    };

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            nint securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            nint templateFile);
    }
}

/// <summary>
/// Runs the kernel lease in the small elevated broker while this process remains asInvoker.
/// Driver events, mapping decisions and every external action remain in the ordinary UI process.
/// Any IPC, validation, mapping or action failure closes the pipe so the broker releases its
/// handle and the kernel immediately returns to fail-open forwarding.
/// </summary>
internal sealed class BrokerDriverSuppressionCoordinator : IDriverSuppressionCoordinator
{
    private static readonly TimeSpan NotificationPollInterval = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan ConfigureTimeout = TimeSpan.FromSeconds(2);

    private readonly Func<CancellationToken, Task<IBrokerSuppressionConnection>> _connectionFactory;
    private readonly Func<InstalledDriverPresence> _driverProbe;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly ConcurrentQueue<BrokerInputReport> _reports = new();
    private BrokerDesiredPolicy _desired = new(0, 0, DriverRuleSetBuildResult.Empty);
    private long _desiredRevision;
    private BrokerActivePolicy? _active;
    private IBrokerSuppressionConnection? _currentConnection;
    private DriverSuppressionStatus? _lastStatus;
    private Task? _worker;
    private CancellationTokenSource? _attemptCancellation;
    private ulong _nextGeneration;
    private long _inputSequence;
    private long _nextConnectionEpoch;
    private bool _serviceStarted;
    private bool _restartRequiresDisable;
    private bool _stopRequested;
    private bool _disposed;

    public BrokerDriverSuppressionCoordinator(
        Func<CancellationToken, Task<IBrokerSuppressionConnection>>? connectionFactory = null,
        Func<DateTimeOffset>? utcNow = null,
        Func<InstalledDriverPresence>? driverProbe = null)
    {
        _connectionFactory = connectionFactory ?? BrokerSuppressionConnectionFactory.ConnectInstalledAsync;
        _driverProbe = driverProbe ?? (connectionFactory is null
            ? InstalledDriverProbe.Probe
            : static () => InstalledDriverPresence.Available);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public event EventHandler<DriverSuppressionStatus>? StatusChanged;

    public event EventHandler<DriverSuppressionInputEventArgs>? InputReceived;

    public bool IsActive => Volatile.Read(ref _active) is not null;

    public DriverRuleSetBuildResult ApplyConfiguration(
        KeyPilotConfiguration configuration,
        long runtimeRevision = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(runtimeRevision);
        var build = DriverSuppressionRuleBuilder.Build(configuration);
        CancellationTokenSource? cancellation = null;
        IBrokerSuppressionConnection? connection = null;
        lock (_gate)
        {
            var revision = checked(_desired.Revision + 1);
            _desired = new BrokerDesiredPolicy(revision, runtimeRevision, build);
            Volatile.Write(ref _desiredRevision, revision);
            if (build.Rules.Count == 0)
            {
                // Disabling (or removing the last kernel rule) is the explicit reset gesture.
                _restartRequiresDisable = false;
                cancellation = _attemptCancellation;
                connection = _currentConnection;
                Volatile.Write(ref _active, null);
            }
            else if (_serviceStarted && !_restartRequiresDisable)
            {
                StartAttemptLocked();
            }
        }

        if (build.Rules.Count == 0)
        {
            CancelSafely(cancellation);
            DisposeConnectionSynchronously(connection);
            PublishStatus(new DriverSuppressionStatus(
                DriverSuppressionState.Stopped,
                "全局映射已关闭或没有可用内核规则；不会启动 Broker，原始输入保持放行。"));
        }

        return build;
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stopRequested)
            {
                throw new InvalidOperationException("A stopped broker coordinator cannot be restarted.");
            }
            if (_serviceStarted)
            {
                return;
            }

            _serviceStarted = true;
            if (_desired.Build.Rules.Count > 0 && !_restartRequiresDisable)
            {
                StartAttemptLocked();
            }
        }
    }

    public bool IsSuppressing(InputSource source)
    {
        var active = Volatile.Read(ref _active);
        return active is not null &&
            active.Build.Rules.Any(rule => DriverSuppressionRuleBuilder.RuleMatchesSource(rule, source));
    }

    public bool ReportInputCommitted(
        long connectionEpoch,
        ulong driverSequence,
        MappingInputCommit commit,
        long policyRevision = 0)
    {
        ArgumentNullException.ThrowIfNull(commit);
        if (!CanAcceptReport(connectionEpoch, driverSequence))
        {
            return false;
        }

        _reports.Enqueue(BrokerInputReport.ForCommit(
            connectionEpoch,
            driverSequence,
            policyRevision,
            commit));
        return true;
    }

    public bool ReportInputCompletion(
        long connectionEpoch,
        ulong driverSequence,
        MappingInputCompletion completion,
        long policyRevision = 0)
    {
        ArgumentNullException.ThrowIfNull(completion);
        if (!CanAcceptReport(connectionEpoch, driverSequence))
        {
            return false;
        }

        _reports.Enqueue(BrokerInputReport.ForCompletion(
            connectionEpoch,
            driverSequence,
            policyRevision,
            completion));
        return true;
    }

    public void Stop()
    {
        Task? worker;
        CancellationTokenSource? cancellation;
        IBrokerSuppressionConnection? connection;
        lock (_gate)
        {
            if (_stopRequested)
            {
                return;
            }

            _serviceStarted = false;
            _stopRequested = true;
            worker = _worker;
            cancellation = _attemptCancellation;
            connection = _currentConnection;
        }

        _lifetimeCancellation.Cancel();
        CancelSafely(cancellation);
        // Closing IPC first makes the elevated broker dispose its exclusive driver handle before
        // MainWindow cancels any remaining action work.
        DisposeConnectionSynchronously(connection);
        if (worker is not null && !worker.Wait(TimeSpan.FromSeconds(2)))
        {
            PublishStatus(new DriverSuppressionStatus(
                DriverSuppressionState.FailOpen,
                "Broker 通信线程未及时退出；IPC 已关闭，内核租约将按截止时间自动失效。"));
        }

        Volatile.Write(ref _active, null);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
        _lifetimeCancellation.Dispose();
    }

    private async Task RunAsync(CancellationTokenSource attempt)
    {
        var cancellationToken = attempt.Token;
        Exception? failure = null;
        IBrokerSuppressionConnection? connection = null;
        var connected = false;
        try
        {
            if (_driverProbe() == InstalledDriverPresence.Missing)
            {
                throw new FileNotFoundException(
                    "KeyPilot 驱动设备当前不可用；为避免无意义 UAC，本次未启动 Broker。");
            }

            PublishStatus(new DriverSuppressionStatus(
                DriverSuppressionState.Connecting,
                "驱动设备已就绪，正在连接受保护的 KeyPilot Broker…"));
            connection = await _connectionFactory(cancellationToken).ConfigureAwait(false);
            connected = true;
            lock (_gate)
            {
                _currentConnection = connection;
            }

            var connectionEpoch = Interlocked.Increment(ref _nextConnectionEpoch);
            await RunConnectedAsync(connection, connectionEpoch, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception exception) when (!IsFatalProcessException(exception))
        {
            failure = exception;
        }
        finally
        {
            await DisposeConnectionSafelyAsync(connection).ConfigureAwait(false);
            lock (_gate)
            {
                if (ReferenceEquals(_currentConnection, connection))
                {
                    _currentConnection = null;
                }
            }

            Volatile.Write(ref _active, null);
        }

        var unexpectedFailure = failure is not null && !cancellationToken.IsCancellationRequested;
        lock (_gate)
        {
            if (ReferenceEquals(_attemptCancellation, attempt))
            {
                _attemptCancellation = null;
                _worker = null;
                if (unexpectedFailure && _desired.Build.Rules.Count > 0)
                {
                    _restartRequiresDisable = true;
                }
            }
        }

        if (unexpectedFailure)
        {
            var status = connected
                ? new DriverSuppressionStatus(
                    DriverSuppressionState.FailOpen,
                    $"Broker/驱动链路异常，已释放租约并 fail-open：{failure!.Message}")
                : ClassifyConnectionFailure(failure!);
            PublishStatus(status with
            {
                Message = status.Message + " 如需重试，请关闭再开启全局映射。"
            });
        }

        if (cancellationToken.IsCancellationRequested)
        {
            PublishStatus(new DriverSuppressionStatus(
                DriverSuppressionState.Stopped,
                "驱动抑制服务已停止；原始输入保持放行。"));
        }

        attempt.Dispose();
        lock (_gate)
        {
            // Handles a quick explicit off→on while the cancelled attempt was still unwinding.
            if (!_stopRequested && _serviceStarted && !_restartRequiresDisable &&
                _desired.Build.Rules.Count > 0)
            {
                StartAttemptLocked();
            }
        }
    }

    private void StartAttemptLocked()
    {
        if (_stopRequested || !_serviceStarted || _restartRequiresDisable ||
            _desired.Build.Rules.Count == 0 ||
            _worker is { IsCompleted: false })
        {
            return;
        }

        _attemptCancellation?.Dispose();
        var attempt = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        _attemptCancellation = attempt;
        _worker = Task.Run(() => RunAsync(attempt));
    }

    private async Task RunConnectedAsync(
        IBrokerSuppressionConnection connection,
        long connectionEpoch,
        CancellationToken cancellationToken)
    {
        var maximumRules = await WaitForLeaseAsync(connection, cancellationToken).ConfigureAwait(false);
        long appliedRevision = -1;
        var state = new BrokerEventState(connectionEpoch);
        Task<BrokerFrame>? pendingRead = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DrainReports(state);

            BrokerDesiredPolicy desired;
            lock (_gate)
            {
                desired = _desired;
            }

            if (desired.Revision != appliedRevision)
            {
                var active = await ApplyPolicyAsync(
                        connection,
                        desired,
                        maximumRules,
                        cancellationToken)
                    .ConfigureAwait(false);
                appliedRevision = desired.Revision;
                Volatile.Write(ref _active, active);
                PublishStatus(ActiveStatus(active));
            }

            pendingRead ??= connection.ReadMessageAsync(cancellationToken);
            var poll = Task.Delay(NotificationPollInterval, cancellationToken);
            if (await Task.WhenAny(pendingRead, poll).ConfigureAwait(false) != pendingRead)
            {
                continue;
            }

            var frame = await pendingRead.ConfigureAwait(false);
            pendingRead = null;
            await HandleNotificationAsync(connection, frame, state, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task<uint> WaitForLeaseAsync(
        IBrokerSuppressionConnection connection,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var frame = await connection.ReadMessageAsync(cancellationToken).ConfigureAwait(false);
            if (frame.Type != BrokerMessageType.Status)
            {
                throw new BrokerProtocolException("Broker sent input before its lease status.");
            }

            var status = BrokerFrameCodec.DecodeStatus(frame);
            if (status.Code == BrokerStatusCode.LeaseActive && status.MaximumRules > 0)
            {
                return status.MaximumRules;
            }
            if (status.Code is BrokerStatusCode.FailOpen or BrokerStatusCode.Fatal)
            {
                throw new BrokerFailOpenException(status.Message);
            }
        }
    }

    private async Task<BrokerActivePolicy> ApplyPolicyAsync(
        IBrokerSuppressionConnection connection,
        BrokerDesiredPolicy desired,
        uint maximumRules,
        CancellationToken cancellationToken)
    {
        if (desired.Build.Rules.Count > maximumRules)
        {
            throw new InvalidDataException($"The installed broker accepts only {maximumRules} rules.");
        }

        var generation = NextGeneration();
        using var timeout = new CancellationTokenSource(ConfigureTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        await connection.ConfigureRulesAsync(generation, desired.Build.Rules, linked.Token)
            .ConfigureAwait(false);
        return new BrokerActivePolicy(
            generation,
            desired.Revision,
            desired.RuntimeRevision,
            desired.Build);
    }

    private async Task HandleNotificationAsync(
        IBrokerSuppressionConnection connection,
        BrokerFrame frame,
        BrokerEventState state,
        CancellationToken cancellationToken)
    {
        switch (frame.Type)
        {
            case BrokerMessageType.EventBatch:
                DispatchEventBatch(BrokerFrameCodec.DecodeEventBatch(frame), state);
                DrainReports(state);
                return;

            case BrokerMessageType.HealthChallenge:
                await RespondToHealthChallengeAsync(
                        connection,
                        BrokerFrameCodec.DecodeHealthChallenge(frame),
                        state,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;

            case BrokerMessageType.Status:
            {
                var status = BrokerFrameCodec.DecodeStatus(frame);
                if (status.Code is BrokerStatusCode.FailOpen or BrokerStatusCode.Fatal or
                    BrokerStatusCode.ShuttingDown)
                {
                    throw new BrokerFailOpenException(status.Message);
                }
                return;
            }

            default:
                throw new BrokerProtocolException("Broker sent an unsupported notification.");
        }
    }

    private void DispatchEventBatch(BrokerEventBatch batch, BrokerEventState state)
    {
        var active = Volatile.Read(ref _active)
            ?? throw new InvalidOperationException("Broker policy disappeared while its lease was active.");
        foreach (var input in batch.Events)
        {
            if (input.Sequence == 0 || input.Sequence <= state.LastDispatchedSequence)
            {
                throw new InvalidDataException("Broker event sequence is not strictly increasing.");
            }

            state.LastDispatchedSequence = input.Sequence;
            if (active.Revision != Volatile.Read(ref _desiredRevision) ||
                input.Generation != active.Generation)
            {
                throw new InvalidOperationException(
                    "A suppressed broker event belongs to a stale policy or generation; IPC must close without acknowledging it.");
            }

            if (!active.Build.Bindings.TryGetValue(input.RuleId, out var binding) ||
                !DriverSuppressionRuleBuilder.EventMatchesRule(input, binding.Rule))
            {
                throw new InvalidDataException("Broker event does not match the active rule binding.");
            }

            var phase = input.Phase switch
            {
                DriverInputPhase.Down => InputEventPhase.Pressed,
                DriverInputPhase.Up => InputEventPhase.Released,
                DriverInputPhase.Repeat => InputEventPhase.Repeated,
                _ => throw new InvalidDataException("Broker event has an unknown input phase.")
            };
            if (!state.AwaitingCommit.Add(input.Sequence) ||
                !state.AwaitingCompletion.Add(input.Sequence))
            {
                throw new InvalidDataException("Broker input is already being tracked.");
            }

            var handlers = InputReceived ??
                throw new InvalidOperationException("No mapping runtime is attached to broker input.");
            handlers.Invoke(
                this,
                new DriverSuppressionInputEventArgs(
                    new InputEvent
                    {
                        Source = binding.Source,
                        Phase = phase,
                        TimestampUtc = _utcNow(),
                        SequenceNumber = Interlocked.Increment(ref _inputSequence)
                    },
                    input.Sequence,
                    state.ConnectionEpoch,
                    active.Revision,
                    active.RuntimeRevision));
        }
    }

    private async Task RespondToHealthChallengeAsync(
        IBrokerSuppressionConnection connection,
        BrokerHealthChallenge challenge,
        BrokerEventState state,
        CancellationToken cancellationToken)
    {
        // A completion can arrive while the notification read is pending. Drain it before
        // evaluating either watermark; the health challenge itself is what wakes this loop.
        DrainReports(state);
        if (challenge.LastDispatchedEventSequence != state.LastDispatchedSequence ||
            challenge.ResponseDeadlineMilliseconds == 0)
        {
            throw new BrokerProtocolException("Broker health challenge has an inconsistent sequence.");
        }

        var deadline = _utcNow() + TimeSpan.FromMilliseconds(challenge.ResponseDeadlineMilliseconds);
        while (state.LastCommittedSequence < challenge.LastDispatchedEventSequence)
        {
            DrainReports(state);
            if (state.LastCommittedSequence >= challenge.LastDispatchedEventSequence)
            {
                break;
            }
            if (_utcNow() >= deadline)
            {
                throw new TimeoutException("The mapping state machine did not commit broker input in time.");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(2), cancellationToken).ConfigureAwait(false);
        }

        var flags = BrokerHealthFlags.StateMachineCommitted | BrokerHealthFlags.ActionQueueHealthy;
        if (state.LastCompletedSequence < challenge.LastDispatchedEventSequence)
        {
            if (state.ActionProgressCounter <= state.LastAcknowledgedProgressCounter)
            {
                await VerifyAwaitingLivenessAsync(state, deadline, cancellationToken)
                    .ConfigureAwait(false);
            }
            flags |= BrokerHealthFlags.ActionProgressVerified;
        }

        var remaining = deadline - _utcNow();
        if (remaining <= TimeSpan.Zero)
        {
            throw new TimeoutException("The broker health response deadline elapsed.");
        }

        using var timeout = new CancellationTokenSource(remaining);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        await connection.AcknowledgeHealthAsync(
                new BrokerHealthAck(
                    challenge.ChallengeId,
                    state.LastCommittedSequence,
                    state.LastCompletedSequence,
                    state.ActionProgressCounter,
                    flags),
                linked.Token)
            .ConfigureAwait(false);
        state.LastAcknowledgedProgressCounter = state.ActionProgressCounter;
    }

    private void DrainReports(BrokerEventState state)
    {
        while (_reports.TryDequeue(out var report))
        {
            if (report.ConnectionEpoch != state.ConnectionEpoch)
            {
                continue;
            }

            if (report.PolicyRevision != 0 &&
                report.PolicyRevision != Volatile.Read(ref _desiredRevision))
            {
                throw new InvalidOperationException(
                    "A broker input report belongs to a superseded policy; it must not advance either acknowledgement watermark.");
            }

            state.ActionProgressCounter = Math.Max(
                state.ActionProgressCounter,
                checked((ulong)Math.Max(0, report.ProgressCounter)));
            if (report.IsCommit)
            {
                if (!state.AwaitingCommit.Remove(report.DriverSequence))
                {
                    throw new InvalidDataException("Broker commit does not match pending input.");
                }
                if (!report.Success)
                {
                    throw new InvalidOperationException(
                        "The mapping state machine rejected suppressed input.",
                        report.Exception);
                }
                if (!state.CommittedOutOfOrder.Add(report.DriverSequence))
                {
                    throw new InvalidDataException("Broker input commit was reported twice.");
                }
                if (report.VerifyLivenessAsync is not null)
                {
                    state.LivenessProbes[report.DriverSequence] = report.VerifyLivenessAsync;
                }
                AdvanceContiguous(state.CommittedOutOfOrder, ref state.LastCommittedSequence);
            }
            else
            {
                if (!state.AwaitingCompletion.Remove(report.DriverSequence))
                {
                    throw new InvalidDataException("Broker completion does not match pending input.");
                }
                state.LivenessProbes.Remove(report.DriverSequence);
                if (!report.Success)
                {
                    throw new InvalidOperationException(
                        "A suppressed input action did not complete.",
                        report.Exception);
                }
                if (!state.CompletedOutOfOrder.Add(report.DriverSequence))
                {
                    throw new InvalidDataException("Broker input completion was reported twice.");
                }
                AdvanceContiguous(state.CompletedOutOfOrder, ref state.LastCompletedSequence);
            }
        }
    }

    private async Task VerifyAwaitingLivenessAsync(
        BrokerEventState state,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        foreach (var sequence in state.AwaitingCompletion.Order())
        {
            if (!state.LivenessProbes.TryGetValue(sequence, out var probe))
            {
                throw new InvalidOperationException(
                    "A suppressed action has no verifiable bounded-wait probe; the broker must fail open.");
            }

            var remaining = deadline - _utcNow();
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException("The bounded-wait liveness probe missed the broker deadline.");
            }

            using var timeout = new CancellationTokenSource(remaining);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeout.Token);
            var progress = await probe(linked.Token).ConfigureAwait(false);
            state.ActionProgressCounter = Math.Max(
                state.ActionProgressCounter,
                checked((ulong)Math.Max(0, progress)));
        }

        if (state.ActionProgressCounter <= state.LastAcknowledgedProgressCounter)
        {
            throw new InvalidOperationException(
                "A suppressed action made no verifiable bounded progress; the broker must fail open.");
        }
    }

    private static void AdvanceContiguous(ISet<ulong> completed, ref ulong watermark)
    {
        while (watermark < ulong.MaxValue && completed.Remove(watermark + 1))
        {
            watermark++;
        }
    }

    private bool CanAcceptReport(long connectionEpoch, ulong driverSequence) =>
        Volatile.Read(ref _active) is not null && !Volatile.Read(ref _disposed) &&
        connectionEpoch > 0 && driverSequence > 0;

    private ulong NextGeneration()
    {
        if (_nextGeneration == ulong.MaxValue)
        {
            throw new InvalidOperationException("Broker policy generation is exhausted.");
        }
        return ++_nextGeneration;
    }

    private static DriverSuppressionStatus ClassifyConnectionFailure(Exception exception) =>
        exception is Win32Exception { NativeErrorCode: 1223 }
            ? new DriverSuppressionStatus(
                DriverSuppressionState.AdministratorRequired,
                "已取消 Broker 的 UAC 授权；当前保持 fail-open，仅使用用户态映射。")
            : exception is FileNotFoundException
                ? new DriverSuppressionStatus(
                    DriverSuppressionState.DriverNotInstalled,
                    "驱动设备未就绪，或未找到受保护的 Broker 安装/清单；Broker 未启动，当前保持 fail-open。")
                : exception is UnauthorizedAccessException
                    ? new DriverSuppressionStatus(
                        DriverSuppressionState.AdministratorRequired,
                        $"Broker 安全校验未通过；当前保持 fail-open：{exception.Message}")
                    : new DriverSuppressionStatus(
                        DriverSuppressionState.FailOpen,
                        $"Broker 连接失败，当前保持 fail-open：{exception.Message}");

    private static DriverSuppressionStatus ActiveStatus(BrokerActivePolicy active)
    {
        var skipped = active.Build.Issues.Count;
        var detail = active.Build.Rules.Count == 0
            ? "Broker 与驱动租约正常；当前方案没有可安全屏蔽的键盘规则。"
            : $"驱动屏蔽已启用：{active.Build.Rules.Count} 条规则，generation {active.Generation}。";
        if (skipped > 0)
        {
            detail += $" {skipped} 条规则因当前驱动安全边界跳过。";
        }
        return new DriverSuppressionStatus(
            DriverSuppressionState.Active,
            detail,
            active.Build.Rules.Count,
            skipped);
    }

    private void PublishStatus(DriverSuppressionStatus status)
    {
        lock (_gate)
        {
            if (Equals(_lastStatus, status))
            {
                return;
            }
            _lastStatus = status;
        }

        var handlers = StatusChanged;
        if (handlers is null)
        {
            return;
        }
        foreach (var subscriber in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<DriverSuppressionStatus>)subscriber)(this, status);
            }
            catch
            {
                // UI diagnostics never control the lease.
            }
        }
    }

    private static async ValueTask DisposeConnectionSafelyAsync(
        IBrokerSuppressionConnection? connection)
    {
        try
        {
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            // The broker deadline is the final release guard.
        }
    }

    private static void DisposeConnectionSynchronously(IBrokerSuppressionConnection? connection)
    {
        try
        {
            connection?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // The broker deadline is the final release guard.
        }
    }

    private static void CancelSafely(CancellationTokenSource? cancellation)
    {
        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The previous one-shot attempt already completed.
        }
    }

    private static bool IsFatalProcessException(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private sealed record BrokerDesiredPolicy(
        long Revision,
        long RuntimeRevision,
        DriverRuleSetBuildResult Build);

    private sealed record BrokerActivePolicy(
        ulong Generation,
        long Revision,
        long RuntimeRevision,
        DriverRuleSetBuildResult Build);

    private sealed record BrokerInputReport(
        long ConnectionEpoch,
        ulong DriverSequence,
        long PolicyRevision,
        bool IsCommit,
        bool Success,
        long ProgressCounter,
        Exception? Exception,
        Func<CancellationToken, Task<long>>? VerifyLivenessAsync)
    {
        public static BrokerInputReport ForCommit(
            long connectionEpoch,
            ulong driverSequence,
            long policyRevision,
            MappingInputCommit commit) =>
            new(
                connectionEpoch,
                driverSequence,
                policyRevision,
                true,
                commit.Success,
                commit.ProgressCounter,
                commit.Exception,
                commit.VerifyLivenessAsync);

        public static BrokerInputReport ForCompletion(
            long connectionEpoch,
            ulong driverSequence,
            long policyRevision,
            MappingInputCompletion completion) =>
            new(
                connectionEpoch,
                driverSequence,
                policyRevision,
                false,
                completion.Success,
                completion.ProgressCounter,
                completion.Exception,
                null);
    }

    private sealed class BrokerEventState(long connectionEpoch)
    {
        public long ConnectionEpoch { get; } = connectionEpoch;
        public ulong LastDispatchedSequence;
        public ulong LastCommittedSequence;
        public ulong LastCompletedSequence;
        public ulong ActionProgressCounter;
        public ulong LastAcknowledgedProgressCounter;
        public HashSet<ulong> AwaitingCommit { get; } = [];
        public HashSet<ulong> AwaitingCompletion { get; } = [];
        public HashSet<ulong> CommittedOutOfOrder { get; } = [];
        public HashSet<ulong> CompletedOutOfOrder { get; } = [];
        public Dictionary<ulong, Func<CancellationToken, Task<long>>> LivenessProbes { get; } = [];

    }

    private sealed class BrokerFailOpenException(string message) : IOException(message);
}
