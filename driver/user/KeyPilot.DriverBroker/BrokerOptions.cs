using System.Globalization;
using System.Security.Cryptography;
using KeyPilot.DriverBroker.Protocol;

namespace KeyPilot.DriverBroker;

internal sealed record BrokerOptions(BrokerLaunchParameters Launch)
{
    public static BrokerOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length != 8)
        {
            throw new ArgumentException("Exactly four broker options are required.", nameof(args));
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            var option = args[index];
            if (option is not ("--pipe" or "--token" or "--parent-pid" or
                "--parent-start-utc-ticks") || !values.TryAdd(option, args[index + 1]))
            {
                throw new ArgumentException("Broker options are unknown, duplicated or malformed.", nameof(args));
            }
        }

        byte[] token;
        try
        {
            token = Convert.FromHexString(values["--token"]);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Broker token must be hexadecimal.", nameof(args), exception);
        }

        try
        {
            if (!uint.TryParse(values["--parent-pid"], NumberStyles.None, CultureInfo.InvariantCulture, out var parentPid) ||
                !long.TryParse(values["--parent-start-utc-ticks"], NumberStyles.None,
                    CultureInfo.InvariantCulture, out var parentStartTicks))
            {
                throw new ArgumentException("Broker parent identity is malformed.", nameof(args));
            }

            var launch = new BrokerLaunchParameters(
                values["--pipe"],
                token,
                parentPid,
                parentStartTicks);
            launch.Validate();
            return new BrokerOptions(launch);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(token);
            throw;
        }
    }
}
