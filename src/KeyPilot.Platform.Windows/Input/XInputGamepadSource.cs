namespace KeyPilot.Platform.Windows.Input;

/// <summary>
/// Polls the four XInput 1.4 user slots and publishes digital button edges plus button-like
/// trigger and thumb-stick edges. The implementation is observation-only: it never imports or
/// calls XInputSetState.
/// </summary>
public sealed class XInputGamepadSource : IDisposable
{
    public const int UserSlotCount = 4;

    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(8);
    private static readonly TimeSpan MinimumPollInterval = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan MaximumPollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumStopWait = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly IXInputStateReader _reader;
    private readonly TimeSpan _pollInterval;
    private CancellationTokenSource? _runCancellation;
    private Thread? _pollThread;
    private bool _started;
    private bool _disposed;
    private bool _isRunning;

    public XInputGamepadSource(TimeSpan? pollInterval = null)
        : this(new XInput14StateReader(), pollInterval)
    {
    }

    internal XInputGamepadSource(IXInputStateReader reader, TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var requestedInterval = pollInterval ?? DefaultPollInterval;
        if (requestedInterval < MinimumPollInterval || requestedInterval > MaximumPollInterval)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pollInterval),
                $"The XInput poll interval must be between {MinimumPollInterval.TotalMilliseconds:0} " +
                $"and {MaximumPollInterval.TotalMilliseconds:0} milliseconds.");
        }

        _reader = reader;
        _pollInterval = requestedInterval;
    }

    public event EventHandler<XInputButtonChangedEventArgs>? ButtonChanged;

    public event EventHandler<XInputVirtualControlChangedEventArgs>? VirtualControlChanged;

    /// <summary>
    /// Publishes every connected polling sample, plus the final disconnected sample. Consumers
    /// that drive continuous behavior should use this heartbeat instead of the deduplicated
    /// <see cref="RawStateChanged"/> event.
    /// </summary>
    public event EventHandler<XInputRawStateChangedEventArgs>? StateSampled;

    /// <summary>
    /// Publishes a raw sample only when connection, packet number or raw control state changes.
    /// Reserved button bits are retained here but remain excluded from <see cref="ButtonChanged"/>.
    /// </summary>
    public event EventHandler<XInputRawStateChangedEventArgs>? RawStateChanged;

    public event EventHandler<XInputConnectionChangedEventArgs>? ConnectionChanged;

    public event EventHandler<Exception>? CaptureFaulted;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _isRunning;
            }
        }
    }

    /// <summary>Starts one cancellable polling run. A source instance is intentionally single-use.</summary>
    public void Start(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                throw new InvalidOperationException("This XInput source has already been started.");
            }

            _started = true;
            _isRunning = true;
            var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _runCancellation = runCancellation;
            _pollThread = new Thread(() => PollLoop(runCancellation))
            {
                IsBackground = true,
                Name = "KeyPilot XInput capture"
            };

            try
            {
                _pollThread.Start();
            }
            catch
            {
                _isRunning = false;
                _runCancellation.Dispose();
                _runCancellation = null;
                _pollThread = null;
                throw;
            }
        }
    }

    /// <summary>Cancels polling and waits for it when called outside an event callback.</summary>
    public void Stop() => StopCore(markDisposed: false);

    public void Dispose()
    {
        StopCore(markDisposed: true);
        GC.SuppressFinalize(this);
    }

    private void StopCore(bool markDisposed)
    {
        Thread? thread;
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            if (markDisposed)
            {
                _disposed = true;
            }

            thread = _pollThread;
            cancellation = _runCancellation;
        }

        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The polling thread completed between taking the snapshot and cancellation.
        }

        // An event subscriber may dispose the source. Waiting for our own thread here would deadlock;
        // cancellation is already visible and the loop will finish after the callback returns.
        if (thread is not null && thread != Thread.CurrentThread)
        {
            if (!thread.Join(MaximumStopWait))
            {
                ReportCaptureFault(
                    new TimeoutException("The XInput polling thread did not stop within two seconds."));
            }
        }
    }

    private void PollLoop(CancellationTokenSource runCancellation)
    {
        var token = runCancellation.Token;
        var previous = new XInputSnapshot[UserSlotCount];
        var activeVirtualControls = new XInputVirtualControlState[UserSlotCount];

        try
        {
            while (!token.IsCancellationRequested)
            {
                for (var userIndex = 0; userIndex < UserSlotCount; userIndex++)
                {
                    if (token.IsCancellationRequested)
                    {
                        break;
                    }

                    try
                    {
                        var current = _reader.Read(userIndex);
                        activeVirtualControls[userIndex] = PublishSlotChanges(
                            userIndex,
                            previous[userIndex],
                            current,
                            activeVirtualControls[userIndex],
                            token);
                        previous[userIndex] = current;
                    }
                    catch (Exception exception) when (!IsFatalProcessException(exception))
                    {
                        ReportCaptureFault(exception);
                        if (IsUnavailableXInputRuntime(exception))
                        {
                            return;
                        }
                    }
                }

                if (token.WaitHandle.WaitOne(_pollInterval))
                {
                    break;
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_runCancellation, runCancellation))
                {
                    _runCancellation = null;
                    _pollThread = null;
                }

                _isRunning = false;
            }

            runCancellation.Dispose();
        }
    }

    private XInputVirtualControlState PublishSlotChanges(
        int userIndex,
        XInputSnapshot previous,
        XInputSnapshot current,
        XInputVirtualControlState activeVirtualControls,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return activeVirtualControls;
        }

        var timestamp = DateTimeOffset.UtcNow;

        var rawSample = new XInputRawStateChangedEventArgs(
            userIndex,
            current.IsConnected,
            current.PacketNumber,
            current.Buttons,
            current.LeftTrigger,
            current.RightTrigger,
            current.ThumbLX,
            current.ThumbLY,
            current.ThumbRX,
            current.ThumbRY,
            timestamp);

        if (current.IsConnected || previous.IsConnected)
        {
            PublishSafely(StateSampled, rawSample);
            if (cancellationToken.IsCancellationRequested)
            {
                return activeVirtualControls;
            }
        }

        if (previous != current)
        {
            PublishSafely(RawStateChanged, rawSample);

            if (cancellationToken.IsCancellationRequested)
            {
                return activeVirtualControls;
            }
        }

        if (!previous.IsConnected && current.IsConnected)
        {
            PublishSafely(
                ConnectionChanged,
                new XInputConnectionChangedEventArgs(userIndex, true, timestamp));
        }

        var previousButtons = previous.IsConnected ? previous.Buttons : (ushort)0;
        var currentButtons = current.IsConnected ? current.Buttons : (ushort)0;
        foreach (var transition in XInputButtonMapper.GetTransitions(previousButtons, currentButtons))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return activeVirtualControls;
            }

            PublishSafely(
                ButtonChanged,
                new XInputButtonChangedEventArgs(
                    userIndex,
                    transition.Button,
                    transition.IsPressed,
                    current.PacketNumber,
                    timestamp));
        }

        var virtualUpdate = XInputVirtualControlMapper.Update(activeVirtualControls, current);
        foreach (var transition in virtualUpdate.Transitions)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return activeVirtualControls;
            }

            PublishSafely(
                VirtualControlChanged,
                new XInputVirtualControlChangedEventArgs(
                    userIndex,
                    transition.Control,
                    transition.IsPressed,
                    current.PacketNumber,
                    timestamp));
        }

        if (previous.IsConnected && !current.IsConnected && !cancellationToken.IsCancellationRequested)
        {
            PublishSafely(
                ConnectionChanged,
                new XInputConnectionChangedEventArgs(userIndex, false, timestamp));
        }

        return virtualUpdate.ActiveControls;
    }

    private void PublishSafely<TEventArgs>(
        EventHandler<TEventArgs>? handlers,
        TEventArgs eventArgs)
        where TEventArgs : EventArgs
    {
        if (handlers is null)
        {
            return;
        }

        foreach (var subscriber in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<TEventArgs>)subscriber)(this, eventArgs);
            }
            catch (Exception exception) when (!IsFatalProcessException(exception))
            {
                ReportCaptureFault(
                    new InvalidOperationException("An XInput event subscriber failed.", exception));
            }
        }
    }

    private void ReportCaptureFault(Exception exception)
    {
        var handlers = CaptureFaulted;
        if (handlers is null)
        {
            return;
        }

        foreach (var subscriber in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<Exception>)subscriber)(this, exception);
            }
            catch
            {
                // Diagnostics must never terminate the capture thread.
            }
        }
    }

    private static bool IsUnavailableXInputRuntime(Exception exception) =>
        exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException;

    private static bool IsFatalProcessException(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;
}
