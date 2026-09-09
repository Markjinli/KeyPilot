using System.Text.Json;

namespace KeyPilot.Platform.Windows.Storage;

/// <summary>
/// Display names for connected-device microphones. These aliases never rewrite Windows endpoint
/// identities; they only change what KeyPilot shows.
/// </summary>
public sealed class MicrophoneAliasStore
{
    public const int MaximumLength = 40;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly object _gate = new();
    private readonly string _path;
    private Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);

    public MicrophoneAliasStore(string? path = null)
    {
        _path = Path.GetFullPath(path ?? GetDefaultPath());
        Load();
    }

    public string PathName => _path;

    public IReadOnlyDictionary<string, string> Snapshot()
    {
        lock (_gate)
        {
            return new Dictionary<string, string>(_aliases, StringComparer.OrdinalIgnoreCase);
        }
    }

    public void Set(string id, string? alias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var cleaned = Normalize(alias);
        lock (_gate)
        {
            if (cleaned is null)
            {
                _aliases.Remove(id);
            }
            else
            {
                _aliases[id] = cleaned;
            }

            Save_NoLock();
        }
    }

    public static string? Normalize(string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            return null;
        }

        var trimmed = new string(alias.Trim().Where(ch => !char.IsControl(ch)).ToArray());
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        return trimmed.Length <= MaximumLength ? trimmed : trimmed[..MaximumLength];
    }

    public static string GetDefaultPath()
    {
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new InvalidOperationException("Windows did not provide LocalApplicationData.");
        }

        return System.IO.Path.Combine(localApplicationData, "KeyPilot", "microphone-aliases.json");
    }

    private void Load()
    {
        try
        {
            var json = File.ReadAllText(_path);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions);
            if (parsed is null)
            {
                return;
            }

            _aliases = parsed
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && Normalize(pair.Value) is not null)
                .ToDictionary(
                    pair => pair.Key.Trim(),
                    pair => Normalize(pair.Value)!,
                    StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            _aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Save_NoLock()
    {
        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(_aliases, JsonOptions);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, _path, overwrite: true);
    }
}
