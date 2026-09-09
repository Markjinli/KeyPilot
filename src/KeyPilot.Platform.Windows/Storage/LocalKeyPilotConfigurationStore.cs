using System.Text;
using System.Text.Json;
using KeyPilot.Core.Configuration;
using KeyPilot.Core.Serialization;
using KeyPilot.Core.Validation;

namespace KeyPilot.Platform.Windows.Storage;

/// <summary>
/// Stores configuration locally, committing fully flushed temporary files atomically.
/// </summary>
public sealed class LocalKeyPilotConfigurationStore : IKeyPilotConfigurationStore
{
    private const string ApplicationDirectoryName = "KeyPilot";
    private const string ConfigurationFileName = "config.json";
    private const string BackupSuffix = ".bak";

    public LocalKeyPilotConfigurationStore(string? configurationPath = null)
    {
        if (configurationPath is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        }

        ConfigurationPath = Path.GetFullPath(configurationPath ?? GetDefaultConfigurationPath());
    }

    public string ConfigurationPath { get; }

    public string BackupPath => ConfigurationPath + BackupSuffix;

    public static string GetDefaultConfigurationPath()
    {
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);

        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new InvalidOperationException(
                "Windows did not provide a LocalApplicationData directory for KeyPilot configuration.");
        }

        return Path.Combine(
            localApplicationData,
            ApplicationDirectoryName,
            ConfigurationFileName);
    }

    public async Task<KeyPilotConfiguration> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            string json;
            try
            {
                json = await File.ReadAllTextAsync(ConfigurationPath, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                return KeyPilotConfiguration.CreateDefault();
            }
            catch (DirectoryNotFoundException)
            {
                return KeyPilotConfiguration.CreateDefault();
            }

            return KeyPilotJson.Deserialize(json);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsInvalidConfiguration(exception))
        {
            throw new KeyPilotConfigurationStoreException(
                $"The KeyPilot configuration at '{ConfigurationPath}' is invalid. " +
                $"The original file was left unchanged; the last replacement backup, if present, is '{BackupPath}'.",
                ConfigurationPath,
                exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new KeyPilotConfigurationStoreException(
                $"The KeyPilot configuration at '{ConfigurationPath}' could not be read.",
                ConfigurationPath,
                exception);
        }
    }

    public async Task SaveAsync(
        KeyPilotConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        cancellationToken.ThrowIfCancellationRequested();

        string json;
        try
        {
            json = KeyPilotJson.Serialize(configuration);
        }
        catch (Exception exception)
        {
            throw new KeyPilotConfigurationStoreException(
                $"The KeyPilot configuration is invalid and was not saved: {exception.Message}",
                ConfigurationPath,
                exception);
        }

        var directoryPath = Path.GetDirectoryName(ConfigurationPath)
            ?? throw new KeyPilotConfigurationStoreException(
                $"The configuration path '{ConfigurationPath}' has no parent directory.",
                ConfigurationPath,
                new DirectoryNotFoundException(ConfigurationPath));
        var temporaryPath = Path.Combine(
            directoryPath,
            $".{Path.GetFileName(ConfigurationPath)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");

        try
        {
            Directory.CreateDirectory(directoryPath);

            var bytes = Encoding.UTF8.GetBytes(json);
            await using (var stream = new FileStream(
                temporaryPath,
                new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    BufferSize = 4096,
                    Options = FileOptions.Asynchronous | FileOptions.WriteThrough
                }))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            CommitTemporaryFile(temporaryPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new KeyPilotConfigurationStoreException(
                $"The KeyPilot configuration could not be saved to '{ConfigurationPath}'.",
                ConfigurationPath,
                exception);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private static bool IsInvalidConfiguration(Exception exception) =>
        exception is JsonException or ConfigurationValidationException or ArgumentException;

    private void CommitTemporaryFile(string temporaryPath)
    {
        if (File.Exists(ConfigurationPath))
        {
            File.Replace(
                temporaryPath,
                ConfigurationPath,
                destinationBackupFileName: BackupPath,
                ignoreMetadataErrors: true);
            return;
        }

        try
        {
            File.Move(temporaryPath, ConfigurationPath);
        }
        catch (IOException) when (File.Exists(ConfigurationPath))
        {
            // Another writer created the destination after the existence check.
            File.Replace(
                temporaryPath,
                ConfigurationPath,
                destinationBackupFileName: BackupPath,
                ignoreMetadataErrors: true);
        }
    }

    private static void TryDeleteTemporaryFile(string temporaryPath)
    {
        try
        {
            File.Delete(temporaryPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Cleanup is best effort; the committed configuration is already durable.
        }
    }
}
