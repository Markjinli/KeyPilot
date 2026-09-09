using Microsoft.Win32;
using System.Security;

namespace KeyPilot.Platform.Windows.Actions;

/// <summary>
/// Normalizes web addresses and validates the syntax boundary for registered URL protocols.
/// This type never launches a process and never interprets local paths as URIs.
/// </summary>
public static class ExternalUriNormalizer
{
    public const int MaximumLength = 8_192;

    private static readonly HashSet<string> BlockedSchemes = new(
        new[]
        {
            Uri.UriSchemeFile,
            "shell",
            "cmd",
            "powershell",
            "pwsh",
            "javascript",
            "vbscript",
            "data",
            "mshta"
        },
        StringComparer.OrdinalIgnoreCase);

    public static string Normalize(string value)
    {
        if (!TryNormalize(value, out var normalized, out var error))
        {
            throw new ArgumentException(error, nameof(value));
        }

        return normalized;
    }

    public static bool TryNormalize(
        string? value,
        out string normalized,
        out string error)
    {
        normalized = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "The URI cannot be empty.";
            return false;
        }

        var candidate = value.Trim();
        if (candidate.Length > MaximumLength || candidate.Any(char.IsControl))
        {
            error = $"The URI must contain at most {MaximumLength} non-control characters.";
            return false;
        }

        if (LooksLikeLocalPath(candidate))
        {
            error = "Local and UNC paths are not accepted by the URI action.";
            return false;
        }

        if (!HasExplicitScheme(candidate) && TryNormalizeNakedDomain(candidate, out normalized))
        {
            return true;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || uri is null)
        {
            error = "Enter a domain name or an absolute registered protocol URI.";
            return false;
        }

        if (BlockedSchemes.Contains(uri.Scheme))
        {
            error = $"The URI scheme '{uri.Scheme}' is not allowed.";
            return false;
        }

        if (IsWebScheme(uri.Scheme) && string.IsNullOrWhiteSpace(uri.Host))
        {
            error = "HTTP and HTTPS addresses require a host.";
            return false;
        }

        normalized = uri.AbsoluteUri;
        return true;
    }

    internal static bool IsWebScheme(string scheme) =>
        scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
        scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    private static bool TryNormalizeNakedDomain(string candidate, out string normalized)
    {
        normalized = string.Empty;
        if (candidate.Contains('\\') || candidate.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        if (!Uri.TryCreate($"https://{candidate}", UriKind.Absolute, out var uri) ||
            uri is null ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        var hostKind = Uri.CheckHostName(uri.Host);
        if (hostKind == UriHostNameType.Unknown ||
            (hostKind == UriHostNameType.Dns && !uri.Host.Contains('.')))
        {
            return false;
        }

        normalized = uri.AbsoluteUri;
        return true;
    }

    private static bool HasExplicitScheme(string value)
    {
        var colon = value.IndexOf(':');
        if (colon <= 0)
        {
            return false;
        }

        var delimiter = value.IndexOfAny(['/', '?', '#']);
        if (delimiter >= 0 && colon > delimiter)
        {
            return false;
        }

        var prefix = value[..colon];
        // A dotted prefix followed by a numeric port is a common naked domain form.
        return !prefix.Contains('.') && Uri.CheckSchemeName(prefix);
    }

    private static bool LooksLikeLocalPath(string value)
    {
        if (value.StartsWith('\\') ||
            value.StartsWith('/') ||
            value.StartsWith("./", StringComparison.Ordinal) ||
            value.StartsWith(".\\", StringComparison.Ordinal) ||
            value.StartsWith("../", StringComparison.Ordinal) ||
            value.StartsWith("..\\", StringComparison.Ordinal) ||
            value.StartsWith("~/", StringComparison.Ordinal) ||
            value.StartsWith("~\\", StringComparison.Ordinal))
        {
            return true;
        }

        return value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':';
    }
}

internal interface IUriSchemeRegistration
{
    bool IsRegistered(string scheme);
}

internal sealed class WindowsUriSchemeRegistration : IUriSchemeRegistration
{
    public static WindowsUriSchemeRegistration Instance { get; } = new();

    private WindowsUriSchemeRegistration()
    {
    }

    public bool IsRegistered(string scheme)
    {
        if (!Uri.CheckSchemeName(scheme))
        {
            return false;
        }

        try
        {
            using var protocol = Registry.ClassesRoot.OpenSubKey(scheme, writable: false);
            if (protocol is null ||
                !protocol.GetValueNames().Contains("URL Protocol", StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            using var command = protocol.OpenSubKey(@"shell\open\command", writable: false);
            return command?.GetValue(null) is string commandText &&
                !string.IsNullOrWhiteSpace(commandText);
        }
        catch (Exception exception) when (exception is SecurityException
                                              or UnauthorizedAccessException
                                              or IOException)
        {
            return false;
        }
    }
}
