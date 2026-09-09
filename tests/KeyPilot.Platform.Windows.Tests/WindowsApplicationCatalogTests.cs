using KeyPilot.Core.Configuration;
using KeyPilot.Platform.Windows.Foreground;

namespace KeyPilot.Platform.Windows.Tests;

internal static class WindowsApplicationCatalogTests
{
    public static Task ListsVisibleRootWindowsAndSkipsToolWindowsAsync()
    {
        var catalog = new WindowsApplicationCatalog(
            new FakeForegroundWindowNative(),
            new FakeWindowEnumerationNative
            {
                Windows =
                [
                    new FakeWindow
                    {
                        Handle = 11,
                        Visible = true,
                        Root = 11,
                        ProcessId = 101,
                        Title = "Chrome"
                    },
                    new FakeWindow
                    {
                        Handle = 12,
                        Visible = true,
                        Root = 12,
                        ToolWindow = true,
                        ProcessId = 102,
                        Title = "tray"
                    },
                    new FakeWindow
                    {
                        Handle = 13,
                        Visible = true,
                        Root = 99,
                        ProcessId = 103,
                        Title = "child"
                    },
                    new FakeWindow
                    {
                        Handle = 14,
                        Visible = true,
                        Root = 14,
                        ProcessId = 101,
                        Title = "Chrome duplicate"
                    }
                ]
            },
            new FakeProcessIdentityNative
            {
                Names = { [101] = "chrome", [102] = "tray", [103] = "child" },
                Descriptions = { ["C:\\chrome.exe"] = "Google Chrome" },
                Paths = { [101] = "C:\\chrome.exe" }
            });

        var running = catalog.ListRunningWindowedApplications();
        Assert(running.Count == 1, "Tool windows, non-root windows, and duplicate PIDs must be collapsed.");
        Assert(running[0].NormalizedProcessName == "chrome", "Process name should be normalized.");
        Assert(running[0].DisplayName == "Google Chrome", "File description should win over the window title.");
        return Task.CompletedTask;
    }

    public static Task SkipsApplicationFrameHostWithoutPackageFamilyAsync()
    {
        var catalog = new WindowsApplicationCatalog(
            new FakeForegroundWindowNative { Window = 21, ThreadId = 1, ProcessId = 201 },
            new FakeWindowEnumerationNative
            {
                Windows =
                [
                    new FakeWindow
                    {
                        Handle = 21,
                        Visible = true,
                        Root = 21,
                        ProcessId = 201,
                        Title = "Calculator"
                    }
                ]
            },
            new FakeProcessIdentityNative
            {
                Names = { [201] = "ApplicationFrameHost" }
            });

        Assert(!catalog.TryGetForegroundIdentity(out _),
            "ApplicationFrameHost without a package family is not a stable identity.");
        Assert(catalog.ListRunningWindowedApplications().Count == 0,
            "The same shell process must not appear in the running list.");
        return Task.CompletedTask;
    }

    public static Task UsesPackageFamilyForPackagedAppsAsync()
    {
        var catalog = new WindowsApplicationCatalog(
            new FakeForegroundWindowNative { Window = 31, ThreadId = 2, ProcessId = 301 },
            new FakeWindowEnumerationNative
            {
                Windows =
                [
                    new FakeWindow
                    {
                        Handle = 31,
                        Visible = true,
                        Root = 31,
                        ProcessId = 301,
                        Title = "Calculator"
                    }
                ]
            },
            new FakeProcessIdentityNative
            {
                Names = { [301] = "ApplicationFrameHost" },
                Families = { [301] = "Microsoft.WindowsCalculator_8wekyb3d8bbwe" }
            });

        Assert(catalog.TryGetForegroundIdentity(out var identity), "Packaged apps should resolve.");
        Assert(identity.PackageFamilyName == "Microsoft.WindowsCalculator_8wekyb3d8bbwe", "PFN");
        Assert(identity.DisplayName == "Calculator", "Window title is the fallback display name.");
        Assert(catalog.ListRunningWindowedApplications().Count == 1, "running packaged");
        return Task.CompletedTask;
    }

    public static Task ForegroundReadFailuresStayEmptyAsync()
    {
        var catalog = new WindowsApplicationCatalog(
            new FakeForegroundWindowNative { Window = 0 },
            new FakeWindowEnumerationNative(),
            new FakeProcessIdentityNative());
        Assert(!catalog.TryGetForegroundIdentity(out var identity) && !identity.HasMatchKey,
            "A missing foreground window is an unsuccessful empty read.");
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
        public nint Window { get; init; }

        public uint ThreadId { get; init; } = 1;

        public uint ProcessId { get; init; }

        public nint GetForegroundWindow() => Window;

        public uint GetWindowThreadProcessId(nint window, out uint processId)
        {
            processId = ProcessId;
            return ThreadId;
        }
    }

    private sealed class FakeWindow
    {
        public nint Handle { get; init; }

        public bool Visible { get; init; }

        public nint Root { get; init; }

        public bool ToolWindow { get; init; }

        public uint ProcessId { get; init; }

        public uint ThreadId { get; init; } = 1;

        public string Title { get; init; } = string.Empty;
    }

    private sealed class FakeWindowEnumerationNative : IWindowEnumerationNative
    {
        public List<FakeWindow> Windows { get; init; } = [];

        public void EnumerateTopLevelWindows(Func<nint, bool> visitor)
        {
            foreach (var window in Windows)
            {
                if (!visitor(window.Handle))
                {
                    return;
                }
            }
        }

        public bool IsWindowVisible(nint window) => Find(window)?.Visible == true;

        public nint GetAncestorRoot(nint window) => Find(window)?.Root ?? 0;

        public bool IsToolWindow(nint window) => Find(window)?.ToolWindow == true;

        public uint GetWindowThreadProcessId(nint window, out uint processId)
        {
            var found = Find(window);
            processId = found?.ProcessId ?? 0;
            return found?.ThreadId ?? 0;
        }

        public string GetWindowTitle(nint window) => Find(window)?.Title ?? string.Empty;

        private FakeWindow? Find(nint window) =>
            Windows.FirstOrDefault(candidate => candidate.Handle == window);
    }

    private sealed class FakeProcessIdentityNative : IProcessIdentityNative
    {
        public Dictionary<int, string> Names { get; } = [];

        public Dictionary<int, string> Paths { get; } = [];

        public Dictionary<int, string> Families { get; } = [];

        public Dictionary<string, string> Descriptions { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? GetProcessName(int processId) =>
            Names.TryGetValue(processId, out var name) ? name : null;

        public string? GetExecutablePath(int processId) =>
            Paths.TryGetValue(processId, out var path) ? path : null;

        public string? GetPackageFamilyName(int processId) =>
            Families.TryGetValue(processId, out var family) ? family : null;

        public string? GetFileDescription(string executablePath) =>
            Descriptions.TryGetValue(executablePath, out var description) ? description : null;
    }
}
