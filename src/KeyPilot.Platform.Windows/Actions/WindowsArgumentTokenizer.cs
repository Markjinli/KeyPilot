using System.Text;

namespace KeyPilot.Platform.Windows.Actions;

/// <summary>
/// Converts the user-facing Windows argument text into discrete ArgumentList entries. No shell
/// expansion, environment-variable expansion, or command substitution is performed.
/// </summary>
internal static class WindowsArgumentTokenizer
{
    private const int MaximumCommandLength = 30_000;
    private const int MaximumArgumentCount = 256;

    public static IReadOnlyList<string> Tokenize(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return Array.Empty<string>();
        }

        if (arguments.Length > MaximumCommandLength)
        {
            throw new ArgumentException(
                $"Arguments exceed the {MaximumCommandLength}-character safety limit.",
                nameof(arguments));
        }

        if (arguments.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0)
        {
            throw new ArgumentException("Arguments cannot contain NUL or line breaks.", nameof(arguments));
        }

        var result = new List<string>();
        var index = 0;
        while (index < arguments.Length)
        {
            while (index < arguments.Length && char.IsWhiteSpace(arguments[index]))
            {
                index++;
            }

            if (index == arguments.Length)
            {
                break;
            }

            var token = new StringBuilder();
            var inQuotes = false;
            var tokenStarted = false;
            while (index < arguments.Length)
            {
                var current = arguments[index];
                if (!inQuotes && char.IsWhiteSpace(current))
                {
                    break;
                }

                if (current == '\\')
                {
                    var slashStart = index;
                    while (index < arguments.Length && arguments[index] == '\\')
                    {
                        index++;
                    }

                    var slashCount = index - slashStart;
                    if (index < arguments.Length && arguments[index] == '"')
                    {
                        token.Append('\\', slashCount / 2);
                        if ((slashCount & 1) == 1)
                        {
                            token.Append('"');
                            index++;
                        }
                        else
                        {
                            inQuotes = !inQuotes;
                            index++;
                        }

                        tokenStarted = true;
                        continue;
                    }

                    token.Append('\\', slashCount);
                    tokenStarted = true;
                    continue;
                }

                if (current == '"')
                {
                    if (inQuotes
                        && index + 1 < arguments.Length
                        && arguments[index + 1] == '"')
                    {
                        token.Append('"');
                        index += 2;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                        index++;
                    }

                    tokenStarted = true;
                    continue;
                }

                token.Append(current);
                tokenStarted = true;
                index++;
            }

            if (inQuotes)
            {
                throw new ArgumentException("Arguments contain an unmatched quote.", nameof(arguments));
            }

            if (tokenStarted)
            {
                result.Add(token.ToString());
                if (result.Count > MaximumArgumentCount)
                {
                    throw new ArgumentException(
                        $"Arguments exceed the {MaximumArgumentCount}-item safety limit.",
                        nameof(arguments));
                }
            }
        }

        return result.AsReadOnly();
    }
}
