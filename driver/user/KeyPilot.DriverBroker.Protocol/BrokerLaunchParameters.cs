using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace KeyPilot.DriverBroker.Protocol;

public sealed record BrokerLaunchParameters(
    string PipeName,
    byte[] Token,
    uint ParentProcessId,
    long ParentStartTimeUtcTicks)
{
    private const string PipePrefix = "KeyPilot.Broker.";

    public static BrokerLaunchParameters CreateForCurrentProcess()
    {
        using var current = Process.GetCurrentProcess();
        var pipeEntropy = RandomNumberGenerator.GetBytes(BrokerProtocol.TokenSize);
        var pipeSuffix = Convert.ToBase64String(pipeEntropy)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return new BrokerLaunchParameters(
            PipePrefix + pipeSuffix,
            RandomNumberGenerator.GetBytes(BrokerProtocol.TokenSize),
            checked((uint)Environment.ProcessId),
            current.StartTime.ToUniversalTime().Ticks);
    }

    public void Validate()
    {
        if (PipeName is null || !PipeName.StartsWith(PipePrefix, StringComparison.Ordinal) ||
            PipeName.Length != PipePrefix.Length + 43 ||
            PipeName.AsSpan(PipePrefix.Length).IndexOfAnyExcept(
                "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_".AsSpan()) >= 0 ||
            Token is null || Token.Length != BrokerProtocol.TokenSize ||
            ParentProcessId == 0 || ParentStartTimeUtcTicks <= 0)
        {
            throw new ArgumentException("Broker launch identity is invalid.");
        }
    }

    public IReadOnlyList<string> ToArgumentList()
    {
        Validate();
        return new[]
        {
            "--pipe", PipeName,
            "--token", Convert.ToHexString(Token),
            "--parent-pid", ParentProcessId.ToString(CultureInfo.InvariantCulture),
            "--parent-start-utc-ticks", ParentStartTimeUtcTicks.ToString(CultureInfo.InvariantCulture),
        };
    }

    public string ToShellArguments()
    {
        // Every generated value is restricted to alphanumerics plus '.'/'-'/'_'; quoting is
        // still applied so ProcessStartInfo receives an unambiguous token sequence.
        return string.Join(' ', ToArgumentList().Select(value => $"\"{value}\""));
    }
}

public static class BrokerProcessLauncher
{
    private const uint GenericWrite = 0x40000000;
    private const uint Delete = 0x00010000;
    private const uint WriteDac = 0x00040000;
    private const uint WriteOwner = 0x00080000;
    private const uint FileAddFile = 0x00000002;
    private const uint FileAddSubdirectory = 0x00000004;
    private const uint FileDeleteChild = 0x00000040;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    public static Process StartElevated(
        string brokerExecutablePath,
        ReadOnlySpan<byte> expectedSha256,
        BrokerLaunchParameters parameters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brokerExecutablePath);
        ArgumentNullException.ThrowIfNull(parameters);
        parameters.Validate();

        var fullPath = Path.GetFullPath(brokerExecutablePath);
        if (!Path.IsPathFullyQualified(fullPath) ||
            !string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(fullPath))
        {
            throw new FileNotFoundException("The KeyPilot broker executable was not found.", fullPath);
        }
        var protectedRoot = FindProtectedProgramFilesRoot(fullPath);
        if (protectedRoot is null)
        {
            throw new UnauthorizedAccessException(
                "The elevated broker must be launched from a protected Program Files directory.");
        }
        if (expectedSha256.Length != 32)
        {
            throw new ArgumentException("A complete SHA-256 broker digest is required.", nameof(expectedSha256));
        }
        ValidateProtectedPath(fullPath, protectedRoot);

        using var executable = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        var actualHash = SHA256.HashData(executable);
        if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedSha256))
        {
            throw new UnauthorizedAccessException("The elevated broker executable hash does not match.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = fullPath,
            Arguments = parameters.ToShellArguments(),
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(fullPath)!,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        /* Keep the verified file object open without write/delete sharing until ShellExecute
         * has created the elevated process. This closes the hash-to-launch replacement race. */
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Broker process did not start.");
    }

    private static string? FindProtectedProgramFilesRoot(string path)
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetEnvironmentVariable("ProgramW6432") ?? string.Empty,
        };
        return roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(root => IsStrictDescendant(path, root));
    }

    private static void ValidateProtectedPath(string executablePath, string protectedRoot)
    {
        var executableDirectory = Path.GetDirectoryName(executablePath) ??
            throw new InvalidDataException("The broker executable has no parent directory.");
        var relativeDirectory = Path.GetRelativePath(protectedRoot, executableDirectory);
        var current = protectedRoot;
        ValidateDirectory(current);
        if (relativeDirectory != ".")
        {
            foreach (var component in relativeDirectory.Split(
                         Path.DirectorySeparatorChar,
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, component);
                ValidateDirectory(current);
            }
        }

        if ((File.GetAttributes(executablePath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException("The elevated broker executable cannot be a reparse point.");
        }
        RejectAnyGrantedAccess(executablePath, directory: false,
            GenericWrite, Delete, WriteDac, WriteOwner);
    }

    private static void ValidateDirectory(string directory)
    {
        var attributes = File.GetAttributes(directory);
        if ((attributes & FileAttributes.Directory) == 0 ||
            (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException(
                "The elevated broker path cannot traverse a reparse point.");
        }
        RejectAnyGrantedAccess(directory, directory: true,
            FileAddFile, FileAddSubdirectory, FileDeleteChild, Delete, WriteDac, WriteOwner);
    }

    private static void RejectAnyGrantedAccess(string path, bool directory, params uint[] accessMasks)
    {
        foreach (var accessMask in accessMasks)
        {
            using var handle = NativeMethods.CreateFile(
                path,
                accessMask,
                FileShare.ReadWrite | FileShare.Delete,
                IntPtr.Zero,
                OpenExisting,
                directory ? FileFlagBackupSemantics : 0,
                IntPtr.Zero);
            if (!handle.IsInvalid)
            {
                throw new UnauthorizedAccessException(
                    "The elevated broker path is writable by the unelevated caller.");
            }
        }
    }

    private static bool IsStrictDescendant(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative.Length > 0 && relative != "." && !Path.IsPathRooted(relative) &&
            !relative.Equals("..", StringComparison.Ordinal) &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
            CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            FileShare shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);
    }
}
