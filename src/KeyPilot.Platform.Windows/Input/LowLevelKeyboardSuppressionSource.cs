using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using KeyPilot.Core.Actions;
using KeyPilot.Core.Input;
using KeyPilot.Platform.Windows.Actions;

namespace KeyPilot.Platform.Windows.Input;

public enum CompatibilityKeyboardSuppressionState
{
    Stopped,
    Starting,
    Active,
    Failed
}

public sealed record CompatibilityKeyboardSuppressionStatus(
    CompatibilityKeyboardSuppressionState State,
    string Message);

public sealed class CompatibilityKeyboardInputEventArgs(
    InputSource source,
    InputEventPhase phase,
    DateTimeOffset timestampUtc,
    long runtimeRevision) : EventArgs
{
    public InputSource Source { get; } = source;

    public InputEventPhase Phase { get; } = phase;

    public DateTimeOffset TimestampUtc { get; } = timestampUtc;

    public long RuntimeRevision { get; } = runtimeRevision;

    internal Task<bool>? AcceptedCommit { get; private set; }

    internal Task<bool>? AcceptedCompletion { get; private set; }

    public bool IsAccepted => AcceptedCommit is not null && AcceptedCompletion is not null;

    /// <summary>
    /// Supplies state-machine commit and eventual action completion separately. Once committed,
    /// an action failure disables future suppression but never replays the original key.
    /// </summary>
    public void Accept(Task<bool> commit, Task<bool> completion)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(completion);
        AcceptedCommit ??= commit;
        AcceptedCompletion ??= completion;
    }
}

/// <summary>
/// Best-effort, user-session keyboard suppression for machines where the protected driver is not
/// installed. The native callback performs only immutable rule lookup, edge bookkeeping and a
/// bounded TryWrite. Any uncertainty passes the event through. It deliberately cannot identify a
/// physical keyboard and therefore accepts only AnyOfKind keyboard scan-code mappings.
/// </summary>
public sealed class LowLevelKeyboardSuppressionSource : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const uint WmQuit = 0x0012;
    private const uint LlkhfExtended = 0x01;
    private const uint LlkhfLowerIlInjected = 0x02;
    private const uint LlkhfInjected = 0x10;
    private const uint VkPause = 0x13;
    private const int EventQueueCapacity = 1_024;
    private const uint PeekMessageNoRemove = 0;

    private readonly object _lifecycleGate = new();
    private readonly object _failOpenTransitionGate = new();
    private readonly InputInjectionMarker _originMarker;
    private readonly Channel<SuppressedPacket> _events;
    private readonly Task _eventWorker;
    private readonly OrderedCommitDispatcher<SuppressedPacket> _commitDispatcher;
    private readonly NativeMethods.LowLevelKeyboardProc _hookCallback;
    private readonly WindowsSendInputBackend _reinjectionBackend = new();
    private readonly CompatibilityKeyboardEdgeTracker _edgeTracker = new();
    private readonly PendingRawInputTracker _pendingRawInput =
        new(EventQueueCapacity, TimeSpan.FromSeconds(30));
    private readonly ManualResetEventSlim _messageQueueReady = new(false);
    private RuntimePolicy _runtimePolicy = new(CompatibilityKeyboardRuleSet.Empty, 0);
    private Thread? _hookThread;
    private nint _hookHandle;
    private uint _hookThreadId;
    private int _requestedEnabled;
    private int _hookInstalled;
    private int _started;
    private int _disposed;
    private int _enableGeneration;
    private int _stopRequested;
    private int _inflightSuppressedPackets;
    private int _failureLatched;
    private int _failureResetArmed;
    private int _pendingCommitPackets;
    private bool _drainingFailOpen;
    private string? _pendingFailOpenMessage;

    public LowLevelKeyboardSuppressionSource(InputInjectionMarker originMarker)
    {
        if (originMarker.Value == 0)
        {
            throw new ArgumentException("A non-zero injection marker is required.", nameof(originMarker));
        }

        _originMarker = originMarker;
        _events = Channel.CreateBounded<SuppressedPacket>(new BoundedChannelOptions(EventQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        _commitDispatcher = new OrderedCommitDispatcher<SuppressedPacket>(
            OnCommitAccepted,
            OnCommitRejectedAsync);
        _hookCallback = HookCallback;
        _eventWorker = RunNonBlockingEventWorkerAsync();
    }

    public event EventHandler<CompatibilityKeyboardInputEventArgs>? InputReceived;

    public event EventHandler<CompatibilityKeyboardSuppressionStatus>? StatusChanged;

    public bool IsInstalled => Volatile.Read(ref _hookInstalled) != 0;

    public bool IsEnabled => Volatile.Read(ref _requestedEnabled) != 0 && IsInstalled;

    public bool IsFailureLatched => Volatile.Read(ref _failureLatched) != 0;

    public void ApplySources(IEnumerable<InputSource> sources, long runtimeRevision)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (runtimeRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(runtimeRevision),
                "A positive runtime revision is required.");
        }

        var next = CompatibilityKeyboardRuleSet.Create(sources);
        lock (_failOpenTransitionGate)
        {
            var previous = Volatile.Read(ref _runtimePolicy);
            if (previous.RuntimeRevision == runtimeRevision &&
                previous.Rules.SetEquals(next))
            {
                return;
            }

            if (previous.RuntimeRevision > 0 &&
                previous.RuntimeRevision != runtimeRevision &&
                _pendingCommitPackets > 0)
            {
                _drainingFailOpen = true;
                _pendingFailOpenMessage ??=
                    "前台应用已切换；正在按原顺序回放旧上下文输入并 fail-open。";
                Volatile.Write(ref _failureLatched, 1);
            }

            Interlocked.Increment(ref _enableGeneration);
            Volatile.Write(ref _runtimePolicy, new RuntimePolicy(next, runtimeRevision));
        }
    }

    internal bool IsRuntimeRevisionCurrent(long runtimeRevision) =>
        Volatile.Read(ref _runtimePolicy).RuntimeRevision == runtimeRevision;

    /// <summary>Starts the dedicated hook message loop. Installation failure is reported and remains fail-open.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        PublishStatus(new CompatibilityKeyboardSuppressionStatus(
            CompatibilityKeyboardSuppressionState.Starting,
            "正在启动仅键盘兼容抑制…"));
        lock (_lifecycleGate)
        {
            _hookThread = new Thread(HookThreadMain)
            {
                IsBackground = true,
                Name = "KeyPilot compatibility keyboard hook"
            };
            _hookThread.Start();
        }
    }

    public void SetEnabled(bool enabled)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!enabled && IsFailureLatched)
        {
            // A persisted/explicit Off transition is required before a failed compatibility
            // session may be retried. Incidental attempts to re-apply an enabled profile cannot
            // silently clear the safety latch.
            Volatile.Write(ref _failureResetArmed, 1);
        }
        else if (enabled && IsFailureLatched)
        {
            if (Interlocked.Exchange(ref _failureResetArmed, 0) == 0)
            {
                return;
            }

            Volatile.Write(ref _failureLatched, 0);
        }

        var requested = enabled ? 1 : 0;
        if (Interlocked.Exchange(ref _requestedEnabled, requested) != requested)
        {
            Interlocked.Increment(ref _enableGeneration);
        }
    }

    /// <summary>
    /// Consumes the callback token corresponding to a Raw Input packet. This prevents duplicate
    /// action execution without assuming that a hook remains healthy merely because it installed.
    /// </summary>
    public bool TryConsumeSuppressed(InputSource source, bool isKeyDown)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!CompatibilityKeyboardKey.TryCreateObserved(source, out var key))
        {
            return false;
        }

        var packetKey = new PendingRawInputKey(key, isKeyDown);
        return _pendingRawInput.TryConsume(packetKey);
    }

    public void Stop()
    {
        Volatile.Write(ref _stopRequested, 1);
        Volatile.Write(ref _requestedEnabled, 0);
        Thread? thread;
        lock (_lifecycleGate)
        {
            thread = _hookThread;
        }

        if (thread is not null && thread != Thread.CurrentThread)
        {
            _messageQueueReady.Wait(TimeSpan.FromSeconds(2));
        }

        nint hook;
        uint threadId;
        lock (_lifecycleGate)
        {
            // Re-read after the ready handshake: the hook thread may have published both values
            // between the first lifecycle check and message-queue creation.
            hook = _hookHandle;
            threadId = _hookThreadId;
            _hookHandle = 0;
        }

        if (hook != 0)
        {
            NativeMethods.UnhookWindowsHookEx(hook);
        }

        if (threadId != 0)
        {
            NativeMethods.PostThreadMessage(threadId, WmQuit, 0, 0);
        }

        if (thread is not null && thread != Thread.CurrentThread)
        {
            thread.Join(TimeSpan.FromSeconds(2));
        }

        Volatile.Write(ref _hookInstalled, 0);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Stop();
        _events.Writer.TryComplete();
        try
        {
            _eventWorker.Wait(TimeSpan.FromSeconds(2));
            _commitDispatcher.Completion.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // The hook is already disabled and uninstalled; diagnostics cannot weaken fail-open.
        }

        GC.SuppressFinalize(this);
    }

    private void HookThreadMain()
    {
        _hookThreadId = NativeMethods.GetCurrentThreadId();
        // Force creation of the thread message queue before Start/Stop can race a WM_QUIT post.
        NativeMethods.PeekMessage(out _, 0, 0, 0, PeekMessageNoRemove);
        _messageQueueReady.Set();
        if (Volatile.Read(ref _stopRequested) != 0)
        {
            return;
        }

        var module = NativeMethods.GetModuleHandle(null);
        var hook = NativeMethods.SetWindowsHookEx(WhKeyboardLl, _hookCallback, module, 0);
        if (hook == 0)
        {
            var error = Marshal.GetLastWin32Error();
            PublishStatus(new CompatibilityKeyboardSuppressionStatus(
                CompatibilityKeyboardSuppressionState.Failed,
                $"仅键盘兼容抑制启动失败，已放行原输入：{new Win32Exception(error).Message}"));
            return;
        }

        lock (_lifecycleGate)
        {
            if (Volatile.Read(ref _stopRequested) != 0)
            {
                NativeMethods.UnhookWindowsHookEx(hook);
                return;
            }

            _hookHandle = hook;
        }

        Volatile.Write(ref _hookInstalled, 1);
        PublishStatus(new CompatibilityKeyboardSuppressionStatus(
            CompatibilityKeyboardSuppressionState.Active,
            "兼容抑制已就绪 · 仅键盘 · 不等同内核驱动"));

        try
        {
            while (true)
            {
                var result = NativeMethods.GetMessage(out var message, 0, 0, 0);
                if (result == 0)
                {
                    break;
                }

                if (result < 0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "The keyboard-hook message loop failed.");
                }

                NativeMethods.TranslateMessage(in message);
                NativeMethods.DispatchMessage(in message);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            Volatile.Write(ref _requestedEnabled, 0);
            PublishStatus(new CompatibilityKeyboardSuppressionStatus(
                CompatibilityKeyboardSuppressionState.Failed,
                $"仅键盘兼容抑制异常，已放行原输入：{exception.Message}"));
        }
        finally
        {
            Volatile.Write(ref _hookInstalled, 0);
            lock (_lifecycleGate)
            {
                if (_hookHandle != 0)
                {
                    NativeMethods.UnhookWindowsHookEx(_hookHandle);
                    _hookHandle = 0;
                }

                _hookThreadId = 0;
                _hookThread = null;
            }
        }
    }

    private nint HookCallback(int code, nuint wParam, nint lParam)
    {
        if (code < 0)
        {
            return NativeMethods.CallNextHookEx(0, code, wParam, lParam);
        }

        var generation = Volatile.Read(ref _enableGeneration);
        _edgeTracker.AdvanceGeneration(generation);

        var message = unchecked((int)wParam);
        var isKeyDown = message is WmKeyDown or WmSysKeyDown;
        var isKeyUp = message is WmKeyUp or WmSysKeyUp;
        if (!isKeyDown && !isKeyUp)
        {
            return NativeMethods.CallNextHookEx(0, code, wParam, lParam);
        }

        var data = Marshal.PtrToStructure<NativeMethods.KbdLlHookStruct>(lParam);
        if ((data.Flags & (LlkhfInjected | LlkhfLowerIlInjected)) != 0 ||
            _originMarker.Matches(unchecked((uint)data.ExtraInformation)))
        {
            return NativeMethods.CallNextHookEx(0, code, wParam, lParam);
        }

        if (!CompatibilityKeyboardKey.TryCreate(data.ScanCode, data.VirtualKey, data.Flags, out var key))
        {
            return NativeMethods.CallNextHookEx(0, code, wParam, lParam);
        }

        if (Volatile.Read(ref _requestedEnabled) == 0 || Volatile.Read(ref _hookInstalled) == 0)
        {
            _edgeTracker.ObservePassThrough(key, isKeyDown);
            return NativeMethods.CallNextHookEx(0, code, wParam, lParam);
        }

        var runtimePolicy = Volatile.Read(ref _runtimePolicy);
        if (!runtimePolicy.Rules.Contains(key))
        {
            _edgeTracker.ObservePassThrough(key, isKeyDown);
            return NativeMethods.CallNextHookEx(0, code, wParam, lParam);
        }

        if (!_edgeTracker.TryObserve(key, isKeyDown, out var phase))
        {
            // If KeyPilot was enabled while the key was already held, its unmatched release must
            // pass through. This avoids fabricating a KeyUp trigger or swallowing another app's down.
            return NativeMethods.CallNextHookEx(0, code, wParam, lParam);
        }

        bool bypassMapping;
        lock (_failOpenTransitionGate)
        {
            if (Volatile.Read(ref _requestedEnabled) == 0 ||
                Volatile.Read(ref _runtimePolicy).RuntimeRevision !=
                    runtimePolicy.RuntimeRevision)
            {
                _edgeTracker.ObservePassThrough(key, isKeyDown);
                return NativeMethods.CallNextHookEx(0, code, wParam, lParam);
            }

            if (_pendingCommitPackets >= EventQueueCapacity)
            {
                _drainingFailOpen = true;
                _pendingFailOpenMessage ??= "兼容抑制的提交队列已达安全上限；当前事件保持抑制并 fail-open。";
                Volatile.Write(ref _failureLatched, 1);
                return 1;
            }

            _pendingCommitPackets++;
            bypassMapping = _drainingFailOpen;
        }

        var packet = new SuppressedPacket(
            key,
            phase,
            DateTimeOffset.UtcNow,
            bypassMapping,
            runtimePolicy.RuntimeRevision);
        if (Interlocked.Increment(ref _inflightSuppressedPackets) > EventQueueCapacity)
        {
            Interlocked.Decrement(ref _inflightSuppressedPackets);
            BeginOrderedFailOpen("兼容抑制的待处理事件已达安全上限；当前事件保持抑制并 fail-open。");
            FinishCommitDecision();
            return 1;
        }

        var pendingKey = new PendingRawInputKey(key, isKeyDown);
        if (!_pendingRawInput.TryReserve(pendingKey, generation, out _))
        {
            Interlocked.Decrement(ref _inflightSuppressedPackets);
            BeginOrderedFailOpen("兼容抑制的 Raw Input 关联队列已满；当前事件保持抑制并 fail-open。");
            FinishCommitDecision();
            return 1;
        }

        if (!_events.Writer.TryWrite(packet))
        {
            Interlocked.Decrement(ref _inflightSuppressedPackets);
            BeginOrderedFailOpen("兼容抑制事件队列已满；当前事件保持抑制并 fail-open。");
            FinishCommitDecision();
            return 1;
        }

        return 1;
    }

    private async Task RunNonBlockingEventWorkerAsync()
    {
        try
        {
            await foreach (var packet in _events.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                var args = new CompatibilityKeyboardInputEventArgs(
                    packet.Key.CreateSource(),
                    packet.Phase,
                    packet.TimestampUtc,
                    packet.RuntimeRevision);
                var staleRuntimeContext = !IsRuntimeRevisionCurrent(packet.RuntimeRevision);
                if (staleRuntimeContext)
                {
                    BeginOrderedFailOpen(
                        "前台应用已切换；正在按原顺序回放旧上下文输入并 fail-open。");
                }

                if (!staleRuntimeContext &&
                    !packet.BypassMapping &&
                    !IsFailOpenDraining())
                {
                    var handlers = InputReceived;
                    if (handlers is not null)
                    {
                        foreach (var subscriber in handlers.GetInvocationList())
                        {
                            try
                            {
                                ((EventHandler<CompatibilityKeyboardInputEventArgs>)subscriber)(this, args);
                            }
                            catch
                            {
                                // A subscriber cannot unwind into the event pump.
                            }
                        }
                    }
                }

                await _commitDispatcher.EnqueueAsync(
                    packet,
                    args.AcceptedCommit,
                    args.AcceptedCompletion).ConfigureAwait(false);
            }
        }
        finally
        {
            _commitDispatcher.Complete();
        }
    }

    private void OnCommitAccepted(SuppressedPacket packet, Task<bool>? completion)
    {
        FinishCommitDecision();
        _ = TrackCommittedCompletionAsync(completion);
    }

    private async ValueTask OnCommitRejectedAsync(
        SuppressedPacket packet,
        Exception? mappingFailure)
    {
        BeginOrderedFailOpen(mappingFailure is null
            ? "映射运行时未提交按键事件，正在按原顺序回放并 fail-open。"
            : $"映射运行时未提交按键事件，正在按原顺序回放并 fail-open：{mappingFailure.Message}");

        try
        {
            await _reinjectionBackend.InjectAsync(
                new InputInjectionRequest(
                    new ControlInjectionTarget(packet.Key.CreateSource().Control),
                    packet.Phase == InputEventPhase.Released
                        ? InputInjectionPhase.Up
                        : InputInjectionPhase.Down,
                    _originMarker),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            BeginOrderedFailOpen($"按键事件回放失败，已保持 fail-open：{exception.Message}");
        }
        finally
        {
            Interlocked.Decrement(ref _inflightSuppressedPackets);
            FinishCommitDecision();
        }
    }

    private async Task TrackCommittedCompletionAsync(Task<bool>? completion)
    {
        try
        {
            var completed = await ObserveAsync(completion).ConfigureAwait(false);
            if (!completed.Success)
            {
                BeginOrderedFailOpen(completed.Exception is null
                    ? "已提交的映射执行失败；后续映射将安全关闭，当前原按键不会回放。"
                    : $"已提交的映射执行失败；后续映射将安全关闭，当前原按键不会回放：{completed.Exception.Message}");
            }
        }
        finally
        {
            Interlocked.Decrement(ref _inflightSuppressedPackets);
        }
    }

    private bool IsFailOpenDraining()
    {
        lock (_failOpenTransitionGate)
        {
            return _drainingFailOpen;
        }
    }

    private void BeginOrderedFailOpen(string message)
    {
        lock (_failOpenTransitionGate)
        {
            _drainingFailOpen = true;
            _pendingFailOpenMessage ??= message;
            Volatile.Write(ref _failureLatched, 1);
        }

        TryFinalizeOrderedFailOpen();
    }

    private void FinishCommitDecision()
    {
        lock (_failOpenTransitionGate)
        {
            if (_pendingCommitPackets <= 0)
            {
                throw new InvalidOperationException("The compatibility commit count underflowed.");
            }

            _pendingCommitPackets--;
        }

        TryFinalizeOrderedFailOpen();
    }

    private void TryFinalizeOrderedFailOpen()
    {
        string? message = null;
        lock (_failOpenTransitionGate)
        {
            if (!_drainingFailOpen || _pendingCommitPackets != 0)
            {
                return;
            }

            _drainingFailOpen = false;
            Volatile.Write(ref _requestedEnabled, 0);
            Interlocked.Increment(ref _enableGeneration);
            message = _pendingFailOpenMessage ?? "兼容抑制已按输入顺序安全切换为 fail-open。";
            _pendingFailOpenMessage = null;
        }

        PublishStatus(new CompatibilityKeyboardSuppressionStatus(
            CompatibilityKeyboardSuppressionState.Failed,
            message));
    }

    private static async Task<(bool Success, Exception? Exception)> ObserveAsync(Task<bool>? task)
    {
        if (task is null)
        {
            return (false, null);
        }

        try
        {
            return (await task.ConfigureAwait(false), null);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            return (false, exception);
        }
    }

    private void PublishStatus(CompatibilityKeyboardSuppressionStatus status)
    {
        if (status.State == CompatibilityKeyboardSuppressionState.Failed)
        {
            Volatile.Write(ref _failureLatched, 1);
        }

        var handlers = StatusChanged;
        if (handlers is null)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            foreach (var subscriber in handlers.GetInvocationList())
            {
                try
                {
                    ((EventHandler<CompatibilityKeyboardSuppressionStatus>)subscriber)(this, status);
                }
                catch
                {
                    // Status is diagnostic only.
                }
            }
        });
    }

    private readonly record struct SuppressedPacket(
        CompatibilityKeyboardKey Key,
        InputEventPhase Phase,
        DateTimeOffset TimestampUtc,
        bool BypassMapping,
        long RuntimeRevision);

    private sealed record RuntimePolicy(
        CompatibilityKeyboardRuleSet Rules,
        long RuntimeRevision);

    private static class NativeMethods
    {
        internal delegate nint LowLevelKeyboardProc(int code, nuint wParam, nint lParam);

        [StructLayout(LayoutKind.Sequential)]
        internal readonly struct KbdLlHookStruct
        {
            public readonly uint VirtualKey;
            public readonly uint ScanCode;
            public readonly uint Flags;
            public readonly uint Time;
            public readonly nuint ExtraInformation;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal readonly struct Message
        {
            public readonly nint Window;
            public readonly uint Id;
            public readonly nuint WParam;
            public readonly nint LParam;
            public readonly uint Time;
            public readonly int PointX;
            public readonly int PointY;
            public readonly uint Private;
        }

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern nint SetWindowsHookEx(
            int hookId,
            LowLevelKeyboardProc callback,
            nint module,
            uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnhookWindowsHookEx(nint hook);

        [DllImport("user32.dll")]
        internal static extern nint CallNextHookEx(nint hook, int code, nuint wParam, nint lParam);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern int GetMessage(out Message message, nint window, uint minimum, uint maximum);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TranslateMessage(in Message message);

        [DllImport("user32.dll")]
        internal static extern nint DispatchMessage(in Message message);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PeekMessage(
            out Message message,
            nint window,
            uint minimum,
            uint maximum,
            uint removeMessage);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern nint GetModuleHandle(string? moduleName);

        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();
    }
}

/// <summary>
/// Resolves commit decisions and required replays in physical-edge FIFO order. Eventual action
/// completion is handed off without awaiting it, so a long macro cannot block later commits.
/// </summary>
internal sealed class OrderedCommitDispatcher<T>
{
    private readonly Channel<WorkItem> _items = Channel.CreateUnbounded<WorkItem>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });
    private readonly Action<T, Task<bool>?> _onCommitted;
    private readonly Func<T, Exception?, ValueTask> _onRejectedAsync;

    public OrderedCommitDispatcher(
        Action<T, Task<bool>?> onCommitted,
        Func<T, Exception?, ValueTask> onRejectedAsync)
    {
        _onCommitted = onCommitted ?? throw new ArgumentNullException(nameof(onCommitted));
        _onRejectedAsync = onRejectedAsync ?? throw new ArgumentNullException(nameof(onRejectedAsync));
        Completion = RunAsync();
    }

    public Task Completion { get; }

    public ValueTask EnqueueAsync(
        T item,
        Task<bool>? commit,
        Task<bool>? completion,
        CancellationToken cancellationToken = default) =>
        _items.Writer.WriteAsync(new WorkItem(item, commit, completion), cancellationToken);

    public void Complete() => _items.Writer.TryComplete();

    private async Task RunAsync()
    {
        await foreach (var item in _items.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            var committed = false;
            Exception? failure = null;
            if (item.Commit is not null)
            {
                try
                {
                    committed = await item.Commit.ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
                {
                    failure = exception;
                }
            }

            if (committed)
            {
                _onCommitted(item.Item, item.Completion);
            }
            else
            {
                await _onRejectedAsync(item.Item, failure).ConfigureAwait(false);
            }
        }
    }

    private sealed record WorkItem(
        T Item,
        Task<bool>? Commit,
        Task<bool>? Completion);
}

internal readonly record struct PendingRawInputKey(
    CompatibilityKeyboardKey Key,
    bool IsKeyDown);

/// <summary>
/// Correlates a suppressed hook edge with the matching Raw Input packet. Tokens are deliberately
/// independent from the current rule generation: an already-suppressed packet must still be
/// deduplicated after a rule switch or fail-open transition. The generation stamp prevents a
/// failed newer reservation from cancelling an older token for the same physical edge identity.
/// </summary>
internal sealed class PendingRawInputTracker
{
    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly long _timeToLiveMilliseconds;
    private readonly Func<long> _monotonicMilliseconds;
    private readonly Dictionary<PendingRawInputKey, LinkedList<PendingToken>> _tokensByKey = [];
    private readonly Dictionary<long, PendingToken> _tokensById = [];
    private readonly LinkedList<PendingToken> _expiryOrder = [];
    private long _nextTokenId;

    public PendingRawInputTracker(
        int capacity,
        TimeSpan timeToLive,
        Func<long>? monotonicMilliseconds = null)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        if (timeToLive <= TimeSpan.Zero || timeToLive.TotalMilliseconds > long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(timeToLive));
        }

        _capacity = capacity;
        _timeToLiveMilliseconds = checked((long)Math.Ceiling(timeToLive.TotalMilliseconds));
        _monotonicMilliseconds = monotonicMilliseconds ?? (() => Environment.TickCount64);
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                ExpireTokens(_monotonicMilliseconds());
                return _tokensById.Count;
            }
        }
    }

    public bool TryReserve(PendingRawInputKey key, int generation, out long tokenId)
    {
        lock (_gate)
        {
            var now = _monotonicMilliseconds();
            ExpireTokens(now);
            if (_tokensById.Count >= _capacity)
            {
                tokenId = 0;
                return false;
            }

            do
            {
                tokenId = unchecked(++_nextTokenId);
            }
            while (tokenId == 0 || _tokensById.ContainsKey(tokenId));

            var expiresAt = now > long.MaxValue - _timeToLiveMilliseconds
                ? long.MaxValue
                : now + _timeToLiveMilliseconds;
            var token = new PendingToken(tokenId, key, generation, expiresAt);
            if (!_tokensByKey.TryGetValue(key, out var matchingTokens))
            {
                matchingTokens = [];
                _tokensByKey.Add(key, matchingTokens);
            }

            token.KeyNode = matchingTokens.AddLast(token);
            token.ExpiryNode = _expiryOrder.AddLast(token);
            _tokensById.Add(tokenId, token);
            return true;
        }
    }

    public bool Cancel(PendingRawInputKey key, long tokenId)
    {
        lock (_gate)
        {
            ExpireTokens(_monotonicMilliseconds());
            if (!_tokensById.TryGetValue(tokenId, out var token) || token.Key != key)
            {
                return false;
            }

            RemoveToken(token);
            return true;
        }
    }

    public bool TryConsume(PendingRawInputKey key) => TryConsume(key, out _);

    internal bool TryConsume(PendingRawInputKey key, out int generation)
    {
        lock (_gate)
        {
            ExpireTokens(_monotonicMilliseconds());
            if (!_tokensByKey.TryGetValue(key, out var matchingTokens) ||
                matchingTokens.First is null)
            {
                generation = default;
                return false;
            }

            var token = matchingTokens.First.Value;
            generation = token.Generation;
            RemoveToken(token);
            return true;
        }
    }

    private void ExpireTokens(long now)
    {
        while (_expiryOrder.First is { Value: var token } && token.ExpiresAt <= now)
        {
            RemoveToken(token);
        }
    }

    private void RemoveToken(PendingToken token)
    {
        _tokensById.Remove(token.Id);
        if (token.KeyNode is not null && _tokensByKey.TryGetValue(token.Key, out var matchingTokens))
        {
            matchingTokens.Remove(token.KeyNode);
            if (matchingTokens.Count == 0)
            {
                _tokensByKey.Remove(token.Key);
            }
        }

        if (token.ExpiryNode is not null)
        {
            _expiryOrder.Remove(token.ExpiryNode);
        }

        token.KeyNode = null;
        token.ExpiryNode = null;
    }

    private sealed class PendingToken(
        long id,
        PendingRawInputKey key,
        int generation,
        long expiresAt)
    {
        public long Id { get; } = id;

        public PendingRawInputKey Key { get; } = key;

        public int Generation { get; } = generation;

        public long ExpiresAt { get; } = expiresAt;

        public LinkedListNode<PendingToken>? KeyNode { get; set; }

        public LinkedListNode<PendingToken>? ExpiryNode { get; set; }
    }
}

internal sealed class CompatibilityKeyboardRuleSet
{
    public static CompatibilityKeyboardRuleSet Empty { get; } = new([]);

    private readonly HashSet<CompatibilityKeyboardKey> _keys;

    private CompatibilityKeyboardRuleSet(HashSet<CompatibilityKeyboardKey> keys)
    {
        _keys = keys;
    }

    public int Count => _keys.Count;

    public bool Contains(CompatibilityKeyboardKey key) => _keys.Contains(key);

    public bool SetEquals(CompatibilityKeyboardRuleSet other) =>
        other is not null && _keys.SetEquals(other._keys);

    public static CompatibilityKeyboardRuleSet Create(IEnumerable<InputSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var keys = new HashSet<CompatibilityKeyboardKey>();
        foreach (var source in sources)
        {
            if (CompatibilityKeyboardKey.TryCreate(source, out var key) && !key.IsEmergencyReserved)
            {
                keys.Add(key);
            }
        }

        return new CompatibilityKeyboardRuleSet(keys);
    }
}

internal readonly record struct CompatibilityKeyboardKey(ushort MakeCode, ushort Prefix)
{
    private const ushort PrefixMask = 0x0006;
    private const uint LowLevelExtendedFlag = 0x01;
    private const uint VirtualKeyPause = 0x13;

    public bool IsEmergencyReserved =>
        (MakeCode == 0x1D && Prefix == 0x0000) || // left Ctrl
        (MakeCode == 0x2A && Prefix == 0x0000) || // left Shift
        (MakeCode == 0x58 && Prefix == 0x0000) || // F12 / emergency bypass chord
        (MakeCode == 0x38 && Prefix == 0x0000) || // left Alt
        (MakeCode == 0x53 && Prefix == 0x0002);   // E0 Delete / SAS path

    public static bool TryCreate(InputSource? source, out CompatibilityKeyboardKey key)
    {
        key = default;
        if (source?.Device is null || source.Control is null ||
            source.Device.Kind != InputDeviceKind.Keyboard ||
            source.Device.MatchMode != DeviceMatchMode.AnyOfKind ||
            source.Control.Kind != InputControlKind.KeyboardScanCode ||
            source.Control.Code is <= 0 or > ushort.MaxValue ||
            !TryReadPrefix(source.Control.RawQualifier, out var prefix) ||
            source.Control.IsExtended != (prefix != 0))
        {
            return false;
        }

        key = new CompatibilityKeyboardKey((ushort)source.Control.Code, prefix);
        return true;
    }

    public static bool TryCreateObserved(InputSource? source, out CompatibilityKeyboardKey key)
    {
        key = default;
        if (source?.Device is null || source.Control is null ||
            source.Device.Kind != InputDeviceKind.Keyboard ||
            source.Control.Kind != InputControlKind.KeyboardScanCode ||
            source.Control.Code is <= 0 or > ushort.MaxValue ||
            !TryReadPrefix(source.Control.RawQualifier, out var prefix) ||
            source.Control.IsExtended != (prefix != 0))
        {
            return false;
        }

        key = new CompatibilityKeyboardKey((ushort)source.Control.Code, prefix);
        return true;
    }

    public static bool TryCreate(uint scanCode, uint virtualKey, uint flags, out CompatibilityKeyboardKey key)
    {
        key = default;
        var makeCode = virtualKey == VirtualKeyPause ? 0x45u : scanCode;
        if (makeCode is 0 or > ushort.MaxValue)
        {
            return false;
        }

        var prefix = virtualKey == VirtualKeyPause
            ? (ushort)0x0004
            : (flags & LowLevelExtendedFlag) != 0
                ? (ushort)0x0002
                : (ushort)0;
        key = new CompatibilityKeyboardKey((ushort)makeCode, prefix);
        return true;
    }

    public InputSource CreateSource() => new()
    {
        Device = new InputDeviceSelector
        {
            Kind = InputDeviceKind.Keyboard,
            MatchMode = DeviceMatchMode.AnyOfKind
        },
        Control = new InputControlId
        {
            Kind = InputControlKind.KeyboardScanCode,
            Code = MakeCode,
            IsExtended = Prefix != 0,
            RawQualifier = $"RAWKEYBOARD-V1;PREFIX={Prefix:X4}"
        }
    };

    private static bool TryReadPrefix(string? qualifier, out ushort prefix)
    {
        prefix = 0;
        if (string.IsNullOrWhiteSpace(qualifier))
        {
            return false;
        }

        var matches = qualifier.Split(
                ';',
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(part => part.StartsWith("PREFIX=", StringComparison.OrdinalIgnoreCase))
            .Select(part => part["PREFIX=".Length..])
            .ToArray();
        return matches.Length == 1 && matches[0].Length == 4 &&
            ushort.TryParse(
                matches[0],
                System.Globalization.NumberStyles.AllowHexSpecifier,
                System.Globalization.CultureInfo.InvariantCulture,
                out prefix) &&
            (prefix & ~PrefixMask) == 0 && prefix is 0x0000 or 0x0002 or 0x0004;
    }
}

internal sealed class CompatibilityKeyboardEdgeTracker
{
    private readonly HashSet<CompatibilityKeyboardKey> _heldKeys = [];
    private readonly HashSet<CompatibilityKeyboardKey> _passThroughHeldKeys = [];
    private int _generation = int.MinValue;

    public void AdvanceGeneration(int generation)
    {
        if (_generation == generation)
        {
            return;
        }

        _generation = generation;
    }

    public void ObservePassThrough(CompatibilityKeyboardKey key, bool isKeyDown)
    {
        if (isKeyDown)
        {
            _heldKeys.Add(key);
            _passThroughHeldKeys.Add(key);
            return;
        }

        _heldKeys.Remove(key);
        _passThroughHeldKeys.Remove(key);
    }

    public bool TryObserve(
        CompatibilityKeyboardKey key,
        bool isKeyDown,
        out InputEventPhase phase)
    {
        if (isKeyDown)
        {
            var isNewPress = _heldKeys.Add(key);
            phase = isNewPress ? InputEventPhase.Pressed : InputEventPhase.Repeated;
            return isNewPress || !_passThroughHeldKeys.Contains(key);
        }

        if (_heldKeys.Remove(key))
        {
            phase = InputEventPhase.Released;
            return !_passThroughHeldKeys.Remove(key);
        }

        phase = default;
        return false;
    }
}
