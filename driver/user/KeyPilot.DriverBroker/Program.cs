using System.Security.Principal;

namespace KeyPilot.DriverBroker;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsWindows() || !IsAdministrator())
            {
                return 10;
            }
            var options = BrokerOptions.Parse(args);
            using var shutdown = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                shutdown.Cancel();
            };
            await new BrokerServer().RunAsync(options, shutdown.Token).ConfigureAwait(false);
            return 0;
        }
        catch (ArgumentException)
        {
            return 2;
        }
        catch (UnauthorizedAccessException)
        {
            return 3;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch
        {
            // Detailed errors are sent over the authenticated pipe when possible. The elevated
            // process intentionally does not emit paths or protocol material to a console/log.
            return 1;
        }
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
