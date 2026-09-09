namespace KeyPilot.Platform.Windows.Storage;

public sealed class KeyPilotConfigurationStoreException : Exception
{
    public KeyPilotConfigurationStoreException(
        string message,
        string configurationPath,
        Exception innerException)
        : base(message, innerException)
    {
        ConfigurationPath = configurationPath;
    }

    public string ConfigurationPath { get; }
}
