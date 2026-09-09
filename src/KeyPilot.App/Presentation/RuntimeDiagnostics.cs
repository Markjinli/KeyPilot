using System.Text;

namespace KeyPilot.App.Presentation;

/// <summary>Small bounded local runtime log for backend and action failures.</summary>
internal static class RuntimeDiagnostics
{
    private const long MaximumLogBytes = 512 * 1024;
    private static readonly object Gate = new();

    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KeyPilot",
        "runtime.log");

    public static void Write(string component, string message, Exception? exception = null)
    {
        try
        {
            lock (Gate)
            {
                var directory = Path.GetDirectoryName(LogPath);
                if (string.IsNullOrWhiteSpace(directory))
                {
                    return;
                }

                Directory.CreateDirectory(directory);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length >= MaximumLogBytes)
                {
                    // One rollover bounds disk use while retaining the immediately preceding run.
                    File.Move(LogPath, LogPath + ".1", overwrite: true);
                }

                var line = new StringBuilder()
                    .Append(DateTimeOffset.Now.ToString("O"))
                    .Append(" [")
                    .Append(Normalize(component))
                    .Append("] ")
                    .AppendLine(Normalize(message));
                if (exception is not null)
                {
                    line.AppendLine(exception.ToString());
                }

                File.AppendAllText(LogPath, line.ToString(), new UTF8Encoding(false));
            }
        }
        catch
        {
            // Diagnostics must never affect input capture, suppression, or shutdown.
        }
    }

    private static string Normalize(string value) =>
        (value ?? string.Empty).Replace('\0', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
}
