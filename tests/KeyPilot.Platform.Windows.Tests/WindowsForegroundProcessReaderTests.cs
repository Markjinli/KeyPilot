using KeyPilot.Platform.Windows.Foreground;

namespace KeyPilot.Platform.Windows.Tests;

internal static class WindowsForegroundProcessReaderTests
{
    public static Task ResolvesAndTrimsForegroundProcessNameAsync()
    {
        var native = new FakeForegroundWindowNative
        {
            Window = 0x1234,
            ThreadId = 27,
            ProcessId = 418
        };
        var resolvedProcessId = 0;
        var reader = new WindowsForegroundProcessReader(native, processId =>
        {
            resolvedProcessId = processId;
            return "  chrome  ";
        });

        Assert(reader.TryGetForegroundProcessName(out var processName),
            "A valid foreground owner should resolve successfully.");
        Assert(processName == "chrome", "The process name should be trimmed without changing its identity.");
        Assert(resolvedProcessId == 418, "The resolver should receive the foreground window's process ID.");
        return Task.CompletedTask;
    }

    public static Task RejectsMissingOrInvalidForegroundOwnersAsync()
    {
        var resolverCalls = 0;
        string? Resolver(int _)
        {
            resolverCalls++;
            return "unused";
        }

        var noWindow = new WindowsForegroundProcessReader(
            new FakeForegroundWindowNative { Window = 0 },
            Resolver);
        Assert(!noWindow.TryGetForegroundProcessName(out var noWindowName) && noWindowName == string.Empty,
            "A missing foreground window should be an unsuccessful empty read.");

        var noThread = new WindowsForegroundProcessReader(
            new FakeForegroundWindowNative { Window = 1, ThreadId = 0, ProcessId = 12 },
            Resolver);
        Assert(!noThread.TryGetForegroundProcessName(out _),
            "A failed GetWindowThreadProcessId call should be rejected.");

        var noProcess = new WindowsForegroundProcessReader(
            new FakeForegroundWindowNative { Window = 1, ThreadId = 3, ProcessId = 0 },
            Resolver);
        Assert(!noProcess.TryGetForegroundProcessName(out _),
            "A zero process ID should be rejected.");

        var oversizedProcess = new WindowsForegroundProcessReader(
            new FakeForegroundWindowNative
            {
                Window = 1,
                ThreadId = 3,
                ProcessId = (uint)int.MaxValue + 1u
            },
            Resolver);
        Assert(!oversizedProcess.TryGetForegroundProcessName(out _),
            "A process ID that cannot be passed to Process.GetProcessById should be rejected.");
        Assert(resolverCalls == 0, "Invalid native identities must not reach the process resolver.");
        return Task.CompletedTask;
    }

    public static Task ProcessRacesAndInvalidNamesFailSafelyAsync()
    {
        var invalidName = new WindowsForegroundProcessReader(
            FakeForegroundWindowNative.Valid,
            _ => "   ");
        Assert(!invalidName.TryGetForegroundProcessName(out var invalidProcessName) &&
               invalidProcessName == string.Empty,
            "An empty process name should fail without leaking whitespace.");

        var processExited = new WindowsForegroundProcessReader(
            FakeForegroundWindowNative.Valid,
            _ => throw new ArgumentException("process exited"));
        Assert(!processExited.TryGetForegroundProcessName(out var exitedProcessName) &&
               exitedProcessName == string.Empty,
            "A process disappearing during lookup should not escape to the caller.");

        var nativeFailure = new WindowsForegroundProcessReader(
            new FakeForegroundWindowNative { Failure = new InvalidOperationException("native failure") },
            _ => "unused");
        Assert(!nativeFailure.TryGetForegroundProcessName(out var failedProcessName) &&
               failedProcessName == string.Empty,
            "An unexpected native read failure should remain an unsuccessful empty read.");
        return Task.CompletedTask;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class FakeForegroundWindowNative : IForegroundWindowNative
    {
        public static FakeForegroundWindowNative Valid => new()
        {
            Window = 1,
            ThreadId = 2,
            ProcessId = 3
        };

        public nint Window { get; init; }

        public uint ThreadId { get; init; }

        public uint ProcessId { get; init; }

        public Exception? Failure { get; init; }

        public nint GetForegroundWindow()
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            return Window;
        }

        public uint GetWindowThreadProcessId(nint window, out uint processId)
        {
            processId = ProcessId;
            return ThreadId;
        }
    }
}
