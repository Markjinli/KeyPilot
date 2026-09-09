using System.Diagnostics;
using KeyPilot.Core.Actions;

namespace KeyPilot.Platform.Windows.Actions;

/// <summary>
/// Starts explicitly selected local files, web addresses, and registered URL protocols.
/// Executables and script runners use ProcessStartInfo.ArgumentList, so user arguments are never
/// concatenated into an implicit command line. File/URI association is used only when no arguments
/// are allowed.
/// </summary>
public sealed class WindowsExternalActionBackend : IExternalActionBackend
{
    private static readonly HashSet<string> ScriptExtensions = new(
        new[] { ".bat", ".cmd", ".ps1" },
        StringComparer.OrdinalIgnoreCase);

    private static readonly char[] CmdMetacharacters =
        { '&', '|', '<', '>', '^', '(', ')', '%', '!' };

    private readonly IProcessStarter _processStarter;
    private readonly IUriSchemeRegistration _uriSchemeRegistration;

    public WindowsExternalActionBackend()
        : this(SystemProcessStarter.Instance, WindowsUriSchemeRegistration.Instance)
    {
    }

    internal WindowsExternalActionBackend(IProcessStarter processStarter)
        : this(processStarter, WindowsUriSchemeRegistration.Instance)
    {
    }

    internal WindowsExternalActionBackend(
        IProcessStarter processStarter,
        IUriSchemeRegistration uriSchemeRegistration)
    {
        _processStarter = processStarter ?? throw new ArgumentNullException(nameof(processStarter));
        _uriSchemeRegistration = uriSchemeRegistration ??
            throw new ArgumentNullException(nameof(uriSchemeRegistration));
    }

    public void ValidateLaunchProgram(LaunchProgramOperation operation) =>
        _ = PrepareLaunchProgram(operation);

    public void ValidateUri(OpenUriOperation operation) =>
        _ = PrepareUri(operation);

    public void ValidateScript(RunScriptOperation operation) =>
        _ = PrepareScript(operation);

    public ValueTask LaunchProgramAsync(
        LaunchProgramOperation operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();

        var startInfo = PrepareLaunchProgram(operation);

        cancellationToken.ThrowIfCancellationRequested();
        _processStarter.Start(startInfo);
        return ValueTask.CompletedTask;
    }

    public ValueTask OpenUriAsync(
        OpenUriOperation operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();

        var startInfo = PrepareUri(operation);
        cancellationToken.ThrowIfCancellationRequested();
        _processStarter.Start(startInfo);
        return ValueTask.CompletedTask;
    }

    public ValueTask RunScriptAsync(
        RunScriptOperation operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();

        var startInfo = PrepareScript(operation);

        cancellationToken.ThrowIfCancellationRequested();
        _processStarter.Start(startInfo);
        return ValueTask.CompletedTask;
    }

    private static ProcessStartInfo PrepareLaunchProgram(LaunchProgramOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var filePath = RequireExistingAbsoluteFile(operation.FilePath, "program or file");
        var extension = Path.GetExtension(filePath);
        if (ScriptExtensions.Contains(extension))
        {
            throw new NotSupportedException(
                "BAT, CMD, and PowerShell files must use the Run script action.");
        }

        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return CreateDirectStartInfo(
                filePath,
                WindowsArgumentTokenizer.Tokenize(operation.Arguments),
                ResolveWorkingDirectory(operation.WorkingDirectory, filePath));
        }

        if (!string.IsNullOrWhiteSpace(operation.Arguments))
        {
            throw new NotSupportedException(
                "Arguments are accepted only for directly launched EXE files.");
        }

        return CreateShellOpenStartInfo(
            filePath,
            ResolveOptionalWorkingDirectory(operation.WorkingDirectory));
    }

    private ProcessStartInfo PrepareUri(OpenUriOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var normalized = ExternalUriNormalizer.Normalize(operation.Uri);
        var uri = new Uri(normalized, UriKind.Absolute);
        if (!ExternalUriNormalizer.IsWebScheme(uri.Scheme) &&
            !_uriSchemeRegistration.IsRegistered(uri.Scheme))
        {
            throw new ArgumentException(
                $"The URL protocol '{uri.Scheme}' is not registered for the current user or machine.",
                nameof(operation));
        }

        return CreateShellOpenStartInfo(normalized, workingDirectory: null);
    }

    private static ProcessStartInfo PrepareScript(RunScriptOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var scriptPath = RequireExistingAbsoluteFile(operation.ScriptPath, "script");
        var extension = Path.GetExtension(scriptPath);
        var arguments = WindowsArgumentTokenizer.Tokenize(operation.Arguments);
        var workingDirectory = Path.GetDirectoryName(scriptPath)
            ?? throw new InvalidOperationException("The script has no parent directory.");

        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return CreateDirectStartInfo(scriptPath, arguments, workingDirectory);
        }

        if (extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            var powerShellPath = RequireSystemTool(
                Path.Combine(
                    Environment.SystemDirectory,
                    "WindowsPowerShell",
                    "v1.0",
                    "powershell.exe"));
            return CreateDirectStartInfo(
                powerShellPath,
                new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-File", scriptPath }
                    .Concat(arguments),
                workingDirectory,
                createNoWindow: true);
        }

        if (extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            var cmdPath = RequireSystemTool(Path.Combine(Environment.SystemDirectory, "cmd.exe"));
            var command = BuildCmdCommand(scriptPath, arguments);
            return CreateDirectStartInfo(
                cmdPath,
                new[] { "/d", "/s", "/v:off", "/c", command },
                workingDirectory,
                createNoWindow: true);
        }

        throw new NotSupportedException(
            "Run script accepts only existing .bat, .cmd, .ps1, and .exe files.");
    }

    private static ProcessStartInfo CreateDirectStartInfo(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        bool createNoWindow = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
            CreateNoWindow = createNoWindow
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static ProcessStartInfo CreateShellOpenStartInfo(
        string fileName,
        string? workingDirectory) =>
        new()
        {
            FileName = fileName,
            Verb = "open",
            UseShellExecute = true,
            WorkingDirectory = workingDirectory ?? string.Empty
        };

    private static string RequireExistingAbsoluteFile(string path, string description)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0)
        {
            throw new ArgumentException(
                $"The {description} path must be a fully qualified local or UNC file path.",
                nameof(path));
        }

        var expandedPath = Environment.ExpandEnvironmentVariables(path);
        if (string.IsNullOrWhiteSpace(expandedPath)
            || expandedPath.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0
            || !Path.IsPathFullyQualified(expandedPath))
        {
            throw new ArgumentException(
                $"The {description} path must be a fully qualified local or UNC file path.",
                nameof(path));
        }

        var fullPath = Path.GetFullPath(expandedPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"The selected {description} does not exist.", fullPath);
        }

        return fullPath;
    }

    private static string ResolveWorkingDirectory(string? requested, string filePath) =>
        ResolveOptionalWorkingDirectory(requested)
        ?? Path.GetDirectoryName(filePath)
        ?? throw new InvalidOperationException("The executable has no parent directory.");

    private static string? ResolveOptionalWorkingDirectory(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return null;
        }

        var expanded = Environment.ExpandEnvironmentVariables(requested);
        if (string.IsNullOrWhiteSpace(expanded)
            || expanded.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0
            || !Path.IsPathFullyQualified(expanded))
        {
            throw new ArgumentException(
                "The working directory must be fully qualified.",
                nameof(requested));
        }

        var fullPath = Path.GetFullPath(expanded);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException(
                $"The selected working directory does not exist: {fullPath}");
        }

        return fullPath;
    }

    private static string RequireSystemTool(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The required Windows script runner was not found.", path);
        }

        return path;
    }

    /// <summary>
    /// Builds the single command operand required after cmd.exe /c. The extra outer quote pair is
    /// the documented cmd form for invoking a quoted executable/batch path. Every dynamic token
    /// is independently quoted after rejecting characters with cmd parsing or expansion meaning.
    /// </summary>
    internal static string BuildCmdCommand(
        string scriptPath,
        IReadOnlyList<string> arguments)
    {
        ValidateCmdToken(scriptPath, "script path");

        var quotedTokens = new List<string>(arguments.Count + 1)
        {
            QuoteCmdToken(scriptPath)
        };
        foreach (var argument in arguments)
        {
            ValidateCmdToken(argument, "batch argument");
            quotedTokens.Add(QuoteCmdToken(argument));
        }

        return $"\"{string.Join(' ', quotedTokens)}\"";
    }

    private static string QuoteCmdToken(string value) => $"\"{value}\"";

    private static void ValidateCmdToken(string value, string description)
    {
        if (value.Length == 0
            || value.Contains('"')
            || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"The {description} cannot be represented safely in a CMD /c command.",
                nameof(value));
        }

        RejectCmdMetacharacters(value, description);
    }

    private static void RejectCmdMetacharacters(string value, string description)
    {
        if (value.IndexOfAny(CmdMetacharacters) >= 0)
        {
            throw new ArgumentException(
                $"The {description} contains CMD metacharacters that cannot be passed safely.",
                nameof(value));
        }
    }
}

internal interface IProcessStarter
{
    void Start(ProcessStartInfo startInfo);
}

internal sealed class SystemProcessStarter : IProcessStarter
{
    public static SystemProcessStarter Instance { get; } = new();

    private SystemProcessStarter()
    {
    }

    public void Start(ProcessStartInfo startInfo)
    {
        var process = Process.Start(startInfo);
        CompleteStart(startInfo, process);
    }

    internal static void CompleteStart(ProcessStartInfo startInfo, Process? process)
    {
        if (process is null)
        {
            // ShellExecute may successfully hand a URL or associated file to Windows without
            // returning a process handle. Direct CreateProcess-style starts must return one.
            if (!startInfo.UseShellExecute)
            {
                throw new InvalidOperationException(
                    "Windows did not create a process for the direct action.");
            }

            return;
        }

        process.Dispose();
    }
}
