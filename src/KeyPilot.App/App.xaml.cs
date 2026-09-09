using Microsoft.UI.Xaml;
using KeyPilot.App.Presentation;
using System.Runtime.InteropServices;
using System.Text;

namespace KeyPilot.App;

public partial class App : Application
{
    private SingleInstanceLease? _singleInstanceLease;
    public static Window? MainWindowInstance { get; private set; }

    public App()
    {
        UnhandledException += App_UnhandledException;
        try
        {
            InitializeComponent();
        }
        catch (Exception exception)
        {
            LogStartupFailure("Application.InitializeComponent", exception);
            throw;
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            if (!SingleInstanceGate.TryAcquireForCurrentUser(out _singleInstanceLease))
            {
                Exit();
                return;
            }

            MainWindowInstance = new MainWindow();
            MainWindowInstance.Activate();
        }
        catch (Exception exception)
        {
            LogStartupFailure("Application.OnLaunched", exception);
            throw;
        }
    }

    internal void ReleaseSingleInstance()
    {
        Interlocked.Exchange(ref _singleInstanceLease, null)?.Dispose();
    }

    private static void App_UnhandledException(
        object sender,
        Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        LogStartupFailure("Application.UnhandledException", args.Exception);
    }

    private static void LogStartupFailure(string stage, Exception exception)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "KeyPilot");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "startup-error.log");
            var text = new StringBuilder()
                .AppendLine(DateTimeOffset.Now.ToString("O"))
                .Append("Stage: ").AppendLine(stage)
                .Append("HRESULT: 0x").AppendLine(exception.HResult.ToString("X8"))
                .AppendLine(TryGetRestrictedErrorDetails(exception.HResult))
                .AppendLine(exception.ToString())
                .AppendLine()
                .ToString();
            File.AppendAllText(path, text, Encoding.UTF8);
        }
        catch
        {
            // Startup diagnostics must never replace the original failure.
        }
    }

    private static string TryGetRestrictedErrorDetails(int hresult)
    {
        nint pointer = 0;
        object? instance = null;
        try
        {
            if (RoGetMatchingRestrictedErrorInfo(hresult, out pointer) < 0 || pointer == 0)
            {
                return "Restricted error info: unavailable";
            }

            instance = Marshal.GetObjectForIUnknown(pointer);
            if (instance is not IRestrictedErrorInfo errorInfo ||
                errorInfo.GetErrorDetails(
                    out var description,
                    out var error,
                    out var restrictedDescription,
                    out var capabilitySid) < 0)
            {
                return "Restricted error info: unreadable";
            }

            return $"Restricted HRESULT: 0x{error:X8}{Environment.NewLine}" +
                $"Description: {description}{Environment.NewLine}" +
                $"Restricted description: {restrictedDescription}{Environment.NewLine}" +
                $"Capability SID: {capabilitySid}";
        }
        catch (Exception diagnosticFailure)
        {
            return $"Restricted error info failed: {diagnosticFailure.Message}";
        }
        finally
        {
            if (instance is not null && Marshal.IsComObject(instance))
            {
                _ = Marshal.FinalReleaseComObject(instance);
            }
            if (pointer != 0)
            {
                Marshal.Release(pointer);
            }
        }
    }

    [DllImport("combase.dll")]
    private static extern int RoGetMatchingRestrictedErrorInfo(
        int hresult,
        out nint restrictedErrorInfo);

    [ComImport]
    [Guid("82BA7092-4C88-427D-A7BC-16DD93FEB67E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IRestrictedErrorInfo
    {
        [PreserveSig]
        int GetErrorDetails(
            [MarshalAs(UnmanagedType.BStr)] out string description,
            out int error,
            [MarshalAs(UnmanagedType.BStr)] out string restrictedDescription,
            [MarshalAs(UnmanagedType.BStr)] out string capabilitySid);

        [PreserveSig]
        int GetReference([MarshalAs(UnmanagedType.BStr)] out string reference);
    }
}
