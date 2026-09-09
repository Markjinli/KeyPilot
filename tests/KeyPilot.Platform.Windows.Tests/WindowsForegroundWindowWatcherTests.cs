using KeyPilot.Platform.Windows.Foreground;

namespace KeyPilot.Platform.Windows.Tests;

internal static class WindowsForegroundWindowWatcherTests
{
    public static Task HookSignalsChangesAndHasIdempotentLifetimeAsync()
    {
        var native = new FakeForegroundWindowEventNative { Hook = 17 };
        using var watcher = new WindowsForegroundWindowWatcher(native);
        var notifications = 0;
        watcher.ForegroundChanged += (_, _) => throw new SyntheticSubscriberException();
        watcher.ForegroundChanged += (_, _) => notifications++;

        Assert(watcher.Start(), "A valid WinEvent hook should start.");
        Assert(watcher.Start(), "Starting an active watcher should be idempotent.");
        Assert(native.InstallCalls == 1 && watcher.IsRunning,
            "The foreground hook should be installed exactly once.");

        native.Raise(eventType: 0x0003, window: 42);
        Assert(notifications == 1,
            "Foreground notifications should reach later subscribers even when one throws.");
        native.Raise(eventType: 0x0004, window: 42);
        native.Raise(eventType: 0x0003, window: 0);
        Assert(notifications == 1, "Unrelated or empty-window events should be ignored.");

        watcher.Dispose();
        watcher.Dispose();
        Assert(native.UninstallCalls == 1 && native.UninstalledHook == 17,
            "Disposal should unhook the exact native handle once.");
        native.Raise(eventType: 0x0003, window: 42);
        Assert(notifications == 1, "A disposed watcher must ignore late native callbacks.");
        return Task.CompletedTask;
    }

    public static Task FailedHookRemainsStoppedAsync()
    {
        var native = new FakeForegroundWindowEventNative { Hook = 0 };
        using var watcher = new WindowsForegroundWindowWatcher(native);

        Assert(!watcher.Start() && !watcher.IsRunning,
            "A missing native hook should be reported without pretending to run.");
        watcher.Dispose();
        Assert(native.UninstallCalls == 0, "A failed installation must not be unhooked.");
        return Task.CompletedTask;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class FakeForegroundWindowEventNative : IForegroundWindowEventNative
    {
        private ForegroundWindowEventCallback? _callback;

        public nint Hook { get; init; }

        public int InstallCalls { get; private set; }

        public int UninstallCalls { get; private set; }

        public nint UninstalledHook { get; private set; }

        public nint InstallForegroundHook(ForegroundWindowEventCallback callback)
        {
            InstallCalls++;
            _callback = callback;
            return Hook;
        }

        public bool UninstallForegroundHook(nint hook)
        {
            UninstallCalls++;
            UninstalledHook = hook;
            _callback?.Invoke(hook, 0x0003, 42, 0, 0, 0, 0);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            return true;
        }

        public void Raise(uint eventType, nint window) =>
            _callback?.Invoke(Hook, eventType, window, 0, 0, 0, 0);
    }

    private sealed class SyntheticSubscriberException : Exception;
}
