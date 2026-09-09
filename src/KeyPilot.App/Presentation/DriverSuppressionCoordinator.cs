using System.ComponentModel;
using System.Collections.Concurrent;
using KeyPilot.Core.Configuration;
using KeyPilot.Core.Input;
using KeyPilot.DriverClient;

namespace KeyPilot.App.Presentation;

internal interface IDriverSuppressionClient : IDisposable
{
    DriverCapabilities GetCapabilities();

    TimeSpan AcquireLease(TimeSpan requested);

    void ReplaceRules(IReadOnlyList<KeyboardRule> rules, ulong generation);

    void Heartbeat(ulong lastSuccessfullyDispatchedSequence);

    IReadOnlyList<DriverInputEvent> ReadEvents(int maximumEvents = 64);
}

internal sealed class KeyPilotDriverSuppressionClient : IDriverSuppressionClient
{
    private readonly KeyPilotDriverClient _inner = new();
    private readonly object _gate = new();
    private bool _disposed;

    public DriverCapabilities GetCapabilities()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _inner.GetCapabilities();
        }
    }

    public TimeSpan AcquireLease(TimeSpan requested)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _inner.AcquireLease(requested);
        }
    }

    public void ReplaceRules(IReadOnlyList<KeyboardRule> rules, ulong generation)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _inner.ReplaceRules(rules, generation);
        }
    }

    public void Heartbeat(ulong lastSuccessfullyDispatchedSequence)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _inner.Heartbeat(lastSuccessfullyDispatchedSequence);
        }
    }

    public IReadOnlyList<DriverInputEvent> ReadEvents(int maximumEvents = 64)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _inner.ReadEvents(maximumEvents);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _inner.Dispose();
        }
    }
}

internal enum DriverSuppressionState
{
    Stopped,
    Connecting,
    Active,
    DriverNotInstalled,
    AdministratorRequired,
    FailOpen
}

internal sealed record DriverSuppressionStatus(
    DriverSuppressionState State,
    string Message,
    int ActiveRuleCount = 0,
    int SkippedRuleCount = 0);

internal sealed class DriverSuppressionInputEventArgs(
    InputEvent inputEvent,
    ulong driverSequence,
    long connectionEpoch,
    long policyRevision,
    long runtimeRevision) : EventArgs
{
    public InputEvent InputEvent { get; } = inputEvent;

    public ulong DriverSequence { get; } = driverSequence;

    public long ConnectionEpoch { get; } = connectionEpoch;

    /// <summary>The coordinator policy revision that suppressed this physical input.</summary>
    public long PolicyRevision { get; } = policyRevision;

    /// <summary>The mapping-runtime revision supplied with the corresponding configuration.</summary>
    public long RuntimeRevision { get; } = runtimeRevision;
}

internal interface IDriverSuppressionCoordinator : IDisposable
{
    event EventHandler<DriverSuppressionStatus>? StatusChanged;

    event EventHandler<DriverSuppressionInputEventArgs>? InputReceived;

    bool IsActive { get; }

    DriverRuleSetBuildResult ApplyConfiguration(
        KeyPilotConfiguration configuration,
        long runtimeRevision = 0);

    void Start();

    bool IsSuppressing(InputSource source);

    bool ReportInputCommitted(
        long connectionEpoch,
        ulong driverSequence,
        MappingInputCommit commit,
        long policyRevision = 0);

    bool ReportInputCompletion(
        long connectionEpoch,
        ulong driverSequence,
        MappingInputCompletion completion,
        long policyRevision = 0);

    void Stop();
}

/// <summary>
/// Replaceable user-mode transport host for the kernel suppression lease. The class has no UI,
/// process-launch or action-execution authority, so it can move unchanged into an elevated broker.
/// </summary>
internal sealed class DriverSuppressionCoordinator : IDriverSuppressionCoordinator
{
    internal static readonly TimeSpan ProductionLeaseDuration = TimeSpan.FromMilliseconds(1500);
    internal static readonly TimeSpan ProductionHeartbeatInterval = TimeSpan.FromMilliseconds(400);
    internal static readonly TimeSpan ProductionReadInterval = TimeSpan.FromMilliseconds(15);
    internal static readonly TimeSpan ProductionReconnectInterval = TimeSpan.FromSeconds(2);

    private readonly Func<IDriverSuppressionClient> _clientFactory;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeSpan _leaseDuration;
    private readonly TimeSpan _heartbeatInterval;
    private readonly TimeSpan _readInterval;
    private readonly TimeSpan _reconnectInterval;
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _wake = new(initialState: false);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly ConcurrentQueue<ReportedInputCompletion> _reportedCompletions = new();
    private DriverDesiredPolicy _desired = new(0, 0, DriverRuleSetBuildResult.Empty);
    private long _desiredRevision;
    private DriverActivePolicy? _active;
    private IDriverSuppressionClient? _currentClient;
    private DriverSuppressionStatus? _lastStatus;
    private Thread? _worker;
    private ulong _nextGeneration;
    private long _inputSequence;
    private long _nextConnectionEpoch;
    private bool _started;
    private bool _stopRequested;
    private bool _disposed;

    public DriverSuppressionCoordinator(
        Func<IDriverSuppressionClient>? clientFactory = null,
        Func<DateTimeOffset>? utcNow = null,
        TimeSpan? leaseDuration = null,
        TimeSpan? heartbeatInterval = null,
        TimeSpan? readInterval = null,
        TimeSpan? reconnectInterval = null)
    {
        _clientFactory = clientFactory ?? (() => new KeyPilotDriverSuppressionClient());
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _leaseDuration = leaseDuration ?? ProductionLeaseDuration;
        _heartbeatInterval = heartbeatInterval ?? ProductionHeartbeatInterval;
        _readInterval = readInterval ?? ProductionReadInterval;
        _reconnectInterval = reconnectInterval ?? ProductionReconnectInterval;

        if (_leaseDuration <= TimeSpan.Zero || _heartbeatInterval <= TimeSpan.Zero ||
            _readInterval <= TimeSpan.Zero || _reconnectInterval <= TimeSpan.Zero ||
            _heartbeatInterval > TimeSpan.FromMilliseconds(500) ||
            _heartbeatInterval + _heartbeatInterval >= _leaseDuration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(heartbeatInterval),
                "The heartbeat must be at most 500 ms and leave at least two intervals per lease.");
        }
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
        lock (_gate)
        {
            var revision = checked(_desired.Revision + 1);
            _desired = new DriverDesiredPolicy(revision, runtimeRevision, build);
            Volatile.Write(ref _desiredRevision, revision);
        }

        _wake.Set();
        return build;
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stopRequested)
            {
                throw new InvalidOperationException("A stopped driver coordinator cannot be restarted.");
            }
            if (_started)
            {
                return;
            }

            _started = true;
            _worker = new Thread(Run)
            {
                IsBackground = true,
                Name = "KeyPilot driver suppression transport"
            };
            _worker.Start();
        }
    }

    /// <summary>
    /// True only after the corresponding wildcard rule generation was atomically accepted by the
    /// driver. Callers may then suppress the duplicate Raw Input execution path.
    /// </summary>
    public bool IsSuppressing(InputSource source)
    {
        var active = Volatile.Read(ref _active);
        return active is not null &&
            active.Build.Rules.Any(rule => DriverSuppressionRuleBuilder.RuleMatchesSource(rule, source));
    }

    /// <summary>
    /// Reports actual mapping/action completion for one delivered driver event. Merely enqueueing
    /// an input is never sufficient. A failed completion tears down the lease on the transport
    /// thread; successful out-of-order completions cannot advance past a sequence gap.
    /// </summary>
    public bool ReportInputCompletion(
        long connectionEpoch,
        ulong driverSequence,
        MappingInputCompletion completion,
        long policyRevision = 0)
    {
        ArgumentNullException.ThrowIfNull(completion);
        if (Volatile.Read(ref _active) is null || Volatile.Read(ref _disposed) ||
            connectionEpoch <= 0 || driverSequence == 0)
        {
            return false;
        }

        _reportedCompletions.Enqueue(new ReportedInputCompletion(
            connectionEpoch,
            driverSequence,
            policyRevision,
            completion));
        _wake.Set();
        return true;
    }

    public bool ReportInputCommitted(
        long connectionEpoch,
        ulong driverSequence,
        MappingInputCommit commit,
        long policyRevision = 0)
    {
        ArgumentNullException.ThrowIfNull(commit);
        // The direct v2 driver client has no separate UI-health challenge. Kernel progress is
        // advanced exclusively by ReportInputCompletion and its contiguous success watermark.
        return commit.Success &&
            (policyRevision == 0 || policyRevision == Volatile.Read(ref _desiredRevision)) &&
            Volatile.Read(ref _active) is not null &&
            !Volatile.Read(ref _disposed) && connectionEpoch > 0 && driverSequence > 0;
    }

    public void Stop()
    {
        Thread? worker;
        IDriverSuppressionClient? client;
        lock (_gate)
        {
            if (!_started)
            {
                return;
            }

            _started = false;
            _stopRequested = true;
            worker = _worker;
            client = _currentClient;
        }

        _lifetimeCancellation.Cancel();
        _wake.Set();

        // Closing the handle invalidates the kernel lease even if the transport thread is inside
        // a synchronous IOCTL. This happens before the action runtime is stopped by MainWindow.
        DisposeClientSafely(client);
        if (worker is not null && worker != Thread.CurrentThread && !worker.Join(TimeSpan.FromSeconds(2)))
        {
            PublishStatus(new DriverSuppressionStatus(
                DriverSuppressionState.FailOpen,
                "驱动通信线程未及时退出；句柄已关闭，内核租约将自动失效。"));
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
        _wake.Dispose();
        _lifetimeCancellation.Dispose();
    }

    internal static DriverSuppressionStatus ClassifyConnectionFailure(Exception exception) =>
        exception is DriverInputTrackingLostException
            ? new DriverSuppressionStatus(
                DriverSuppressionState.FailOpen,
                "驱动已停止抑制：键盘输入跟踪丢失，请重新插拔设备或完成一次 D0 电源循环。")
            : exception is Win32Exception { NativeErrorCode: 2 or 3 }
            ? new DriverSuppressionStatus(
                DriverSuppressionState.DriverNotInstalled,
                "驱动未安装；当前使用用户态映射，原按键不会被屏蔽。")
            : exception is Win32Exception { NativeErrorCode: 5 }
                ? new DriverSuppressionStatus(
                    DriverSuppressionState.AdministratorRequired,
                    "驱动已安装但无法访问：当前直连需管理员启动；界面仍保持 asInvoker。")
                : new DriverSuppressionStatus(
                    DriverSuppressionState.FailOpen,
                    $"驱动连接失败，已 fail-open：{exception.Message}");

    private void Run()
    {
        var cancellationToken = _lifetimeCancellation.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            // A configuration signal issued before the initial connection is already reflected
            // in _desired. Do not let that stale signal bypass the fail-open reconnect delay.
            _wake.Reset();
            IDriverSuppressionClient? client = null;
            var leaseAcquired = false;
            Exception? failure = null;
            try
            {
                PublishStatus(new DriverSuppressionStatus(
                    DriverSuppressionState.Connecting,
                    "正在连接 KeyPilot 驱动…"));
                client = _clientFactory();
                lock (_gate)
                {
                    _currentClient = client;
                }

                var capabilities = client.GetCapabilities();
                if (capabilities.DriverVersionMajor != 1 || capabilities.MaximumRules == 0)
                {
                    throw new InvalidDataException("The installed KeyPilot driver protocol is not compatible.");
                }
                if ((capabilities.State & DriverStateFlags.InputTrackingLost) != 0)
                {
                    throw new DriverInputTrackingLostException();
                }

                var grantedLease = client.AcquireLease(_leaseDuration);
                leaseAcquired = true;
                if (grantedLease <= _heartbeatInterval + _heartbeatInterval)
                {
                    throw new InvalidDataException("The granted driver lease is too short for safe heartbeats.");
                }

                var connectionEpoch = Interlocked.Increment(ref _nextConnectionEpoch);
                RunConnected(
                    client,
                    capabilities.MaximumRules,
                    connectionEpoch,
                    cancellationToken);
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
                // Dispose first: kernel fail-open precedes Raw Input becoming the execution source.
                DisposeClientSafely(client);
                lock (_gate)
                {
                    if (ReferenceEquals(_currentClient, client))
                    {
                        _currentClient = null;
                    }
                }

                Volatile.Write(ref _active, null);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (failure is not null)
            {
                PublishStatus(leaseAcquired
                    ? new DriverSuppressionStatus(
                        DriverSuppressionState.FailOpen,
                        $"驱动链路异常，租约已释放并 fail-open：{failure.Message}")
                    : ClassifyConnectionFailure(failure));
            }

            WaitForWake(_reconnectInterval, cancellationToken);
        }

        PublishStatus(new DriverSuppressionStatus(
            DriverSuppressionState.Stopped,
            "驱动抑制服务已停止；原始输入保持放行。"));
    }

    private void RunConnected(
        IDriverSuppressionClient client,
        uint maximumRules,
        long connectionEpoch,
        CancellationToken cancellationToken)
    {
        long appliedRevision = -1;
        ulong lastDriverSequence = 0;
        ulong lastAcknowledgedSequence = 0;
        var awaitingCompletion = new HashSet<ulong>();
        var completedOutOfOrder = new HashSet<ulong>();
        var nextHeartbeatUtc = _utcNow();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DrainReportedCompletions(
                connectionEpoch,
                awaitingCompletion,
                completedOutOfOrder,
                ref lastAcknowledgedSequence);
            DriverDesiredPolicy desired;
            lock (_gate)
            {
                desired = _desired;
            }

            if (desired.Revision != appliedRevision)
            {
                if (desired.Build.Rules.Count > maximumRules)
                {
                    throw new InvalidDataException(
                        $"The installed driver accepts only {maximumRules} rules.");
                }

                var generation = NextGeneration();
                client.ReplaceRules(desired.Build.Rules, generation);
                var active = new DriverActivePolicy(
                    generation,
                    desired.Revision,
                    desired.RuntimeRevision,
                    desired.Build);
                Volatile.Write(ref _active, active);
                appliedRevision = desired.Revision;
                PublishStatus(ActiveStatus(active));
            }

            var now = _utcNow();
            if (now >= nextHeartbeatUtc)
            {
                client.Heartbeat(lastAcknowledgedSequence);
                nextHeartbeatUtc = now + _heartbeatInterval;
            }

            var activeSnapshot = Volatile.Read(ref _active)
                ?? throw new InvalidOperationException("Driver policy disappeared while the lease was active.");
            foreach (var input in client.ReadEvents())
            {
                if (input.Sequence == 0 || input.Sequence <= lastDriverSequence)
                {
                    throw new InvalidDataException("Driver event sequence is not strictly increasing.");
                }

                lastDriverSequence = input.Sequence;
                if (activeSnapshot.Revision != Volatile.Read(ref _desiredRevision))
                {
                    throw new InvalidOperationException(
                        "A suppressed event belongs to a superseded driver policy; the lease must fail open without acknowledging it.");
                }

                if (input.Generation != activeSnapshot.Generation)
                {
                    throw new InvalidOperationException(
                        "A suppressed event belongs to a stale driver generation; the lease must fail open without acknowledging it.");
                }

                if (!activeSnapshot.Build.Bindings.TryGetValue(input.RuleId, out var binding) ||
                    !DriverSuppressionRuleBuilder.EventMatchesRule(input, binding.Rule))
                {
                    throw new InvalidDataException("Driver event does not match the active rule binding.");
                }

                var phase = input.Phase switch
                {
                    DriverInputPhase.Down => InputEventPhase.Pressed,
                    DriverInputPhase.Up => InputEventPhase.Released,
                    DriverInputPhase.Repeat => InputEventPhase.Repeated,
                    _ => throw new InvalidDataException("Driver event has an unknown input phase.")
                };
                if (!awaitingCompletion.Add(input.Sequence))
                {
                    throw new InvalidDataException("Driver input is already waiting for completion.");
                }

                var handlers = InputReceived ??
                    throw new InvalidOperationException("No mapping runtime is attached to driver input.");
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
                        connectionEpoch,
                        activeSnapshot.Revision,
                        activeSnapshot.RuntimeRevision));
            }

            DrainReportedCompletions(
                connectionEpoch,
                awaitingCompletion,
                completedOutOfOrder,
                ref lastAcknowledgedSequence);

            var untilHeartbeat = nextHeartbeatUtc - _utcNow();
            var wait = untilHeartbeat <= TimeSpan.Zero
                ? TimeSpan.FromMilliseconds(1)
                : untilHeartbeat < _readInterval
                    ? untilHeartbeat
                    : _readInterval;
            WaitForWake(wait, cancellationToken);
        }
    }

    private void DrainReportedCompletions(
        long connectionEpoch,
        ISet<ulong> awaitingCompletion,
        ISet<ulong> completedOutOfOrder,
        ref ulong lastAcknowledgedSequence)
    {
        while (_reportedCompletions.TryDequeue(out var reported))
        {
            if (reported.ConnectionEpoch != connectionEpoch)
            {
                continue;
            }

            if (reported.PolicyRevision != 0 &&
                reported.PolicyRevision != Volatile.Read(ref _desiredRevision))
            {
                throw new InvalidOperationException(
                    "Completion belongs to a superseded driver policy; it must not advance the kernel acknowledgement.");
            }

            if (!awaitingCompletion.Remove(reported.DriverSequence))
            {
                throw new InvalidDataException("Action completion does not match a pending driver event.");
            }

            if (!reported.Completion.Success)
            {
                throw new InvalidOperationException(
                    "A suppressed input action did not complete; the lease must fail open.",
                    reported.Completion.Exception);
            }

            if (!completedOutOfOrder.Add(reported.DriverSequence))
            {
                throw new InvalidDataException("Driver input completion was reported more than once.");
            }
        }

        AdvanceAcknowledgedSequence(completedOutOfOrder, ref lastAcknowledgedSequence);
    }

    private static void AdvanceAcknowledgedSequence(
        ISet<ulong> completedOutOfOrder,
        ref ulong lastAcknowledgedSequence)
    {
        while (lastAcknowledgedSequence < ulong.MaxValue &&
               completedOutOfOrder.Remove(lastAcknowledgedSequence + 1))
        {
            lastAcknowledgedSequence++;
        }
    }

    private ulong NextGeneration()
    {
        if (_nextGeneration == ulong.MaxValue)
        {
            throw new InvalidOperationException("Driver policy generation is exhausted.");
        }

        return ++_nextGeneration;
    }

    private DriverSuppressionStatus ActiveStatus(DriverActivePolicy active)
    {
        var skipped = active.Build.Issues.Count;
        var detail = active.Build.Rules.Count == 0
            ? "驱动租约正常；当前方案没有可安全屏蔽的键盘规则。"
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
                // UI diagnostics never control the safety lease.
            }
        }
    }

    private void WaitForWake(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            _wake.Wait(delay, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation may race the caller's pre-wait check during normal shutdown.
        }
        finally
        {
            _wake.Reset();
        }
    }

    private static void DisposeClientSafely(IDriverSuppressionClient? client)
    {
        try
        {
            client?.Dispose();
        }
        catch
        {
            // Closing/lease release is best effort; the kernel deadline remains the final guard.
        }
    }

    private static bool IsFatalProcessException(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private sealed record DriverDesiredPolicy(
        long Revision,
        long RuntimeRevision,
        DriverRuleSetBuildResult Build);

    private sealed record DriverActivePolicy(
        ulong Generation,
        long Revision,
        long RuntimeRevision,
        DriverRuleSetBuildResult Build);

    private sealed record ReportedInputCompletion(
        long ConnectionEpoch,
        ulong DriverSequence,
        long PolicyRevision,
        MappingInputCompletion Completion);

    private sealed class DriverInputTrackingLostException : Exception
    {
        public DriverInputTrackingLostException()
            : base("The driver reported that keyboard input tracking was lost.")
        {
        }
    }
}
