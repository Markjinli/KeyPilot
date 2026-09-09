using KeyPilot.Core.Configuration;

namespace KeyPilot.Platform.Windows.Storage;

/// <summary>Loads and persists the local KeyPilot configuration.</summary>
public interface IKeyPilotConfigurationStore
{
    string ConfigurationPath { get; }

    Task<KeyPilotConfiguration> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(
        KeyPilotConfiguration configuration,
        CancellationToken cancellationToken = default);
}
