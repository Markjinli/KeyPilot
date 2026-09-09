namespace KeyPilot.Core.Configuration;

public sealed record AppearanceThemePalette
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required string Description { get; init; }

    public required bool IsDark { get; init; }

    public required string Background { get; init; }

    public required string Sidebar { get; init; }

    public required string Panel { get; init; }

    public required string PanelSecondary { get; init; }

    public required string Line { get; init; }

    public required string Text { get; init; }

    public required string Muted { get; init; }

    public required string Dim { get; init; }

    public required string Accent { get; init; }

    public required string OnAccent { get; init; }

    public required string OnAccentDeep { get; init; }

    public required string Blue { get; init; }

    public required string BlueBright { get; init; }

    public required string Amber { get; init; }

    public required string AmberBright { get; init; }

    public required string Red { get; init; }

    public required string Selection { get; init; }
}

public static class AppearanceThemeCatalog
{
    public const string DefaultId = "night-pilot";
    public const int MaximumIdLength = 64;

    public static IReadOnlyList<AppearanceThemePalette> All { get; } =
    [
        new()
        {
            Id = DefaultId,
            DisplayName = "夜航",
            Description = "薄荷色深色工作台",
            IsDark = true,
            Background = "#080D13",
            Sidebar = "#0D141D",
            Panel = "#111A24",
            PanelSecondary = "#16212D",
            Line = "#273645",
            Text = "#EDF4F7",
            Muted = "#8D9BA8",
            Dim = "#627180",
            Accent = "#62E3C5",
            OnAccent = "#052019",
            OnAccentDeep = "#05231B",
            Blue = "#91A6FF",
            BlueBright = "#C1CAFF",
            Amber = "#FFC276",
            AmberBright = "#EBCDA8",
            Red = "#FF838D",
            Selection = "#F5F7FA"
        },
        new()
        {
            Id = "mist",
            DisplayName = "雾白",
            Description = "冷灰浅色，适合明亮环境",
            IsDark = false,
            Background = "#F3F6F8",
            Sidebar = "#E7EEF2",
            Panel = "#FFFFFF",
            PanelSecondary = "#E4EBEF",
            Line = "#C5D0D8",
            Text = "#1B2730",
            Muted = "#5B6B76",
            Dim = "#7A8A95",
            Accent = "#0F8F78",
            OnAccent = "#F4FFFB",
            OnAccentDeep = "#E8FFF6",
            Blue = "#3B5BDB",
            BlueBright = "#2F4AB8",
            Amber = "#C67A12",
            AmberBright = "#9A5C0A",
            Red = "#C23D4A",
            Selection = "#1B2730"
        },
        new()
        {
            Id = "obsidian",
            DisplayName = "曜石",
            Description = "纯黑底与金色强调",
            IsDark = true,
            Background = "#050505",
            Sidebar = "#0C0C0C",
            Panel = "#121212",
            PanelSecondary = "#1A1A1A",
            Line = "#2E2E2E",
            Text = "#F4F1EA",
            Muted = "#A39E93",
            Dim = "#6F6A62",
            Accent = "#E2B657",
            OnAccent = "#1A1406",
            OnAccentDeep = "#140F04",
            Blue = "#8BA4D4",
            BlueBright = "#C5D4EE",
            Amber = "#E89A4A",
            AmberBright = "#F0D9A0",
            Red = "#E07A7A",
            Selection = "#F4F1EA"
        },
        new()
        {
            Id = "dusk",
            DisplayName = "暮紫",
            Description = "靛紫暮色与丁香强调",
            IsDark = true,
            Background = "#120C18",
            Sidebar = "#191221",
            Panel = "#1F1729",
            PanelSecondary = "#271E34",
            Line = "#3D314C",
            Text = "#F0E8F7",
            Muted = "#A392B5",
            Dim = "#716380",
            Accent = "#C9A6FF",
            OnAccent = "#1B1028",
            OnAccentDeep = "#140C1E",
            Blue = "#9BB0FF",
            BlueBright = "#C9D3FF",
            Amber = "#F0B87A",
            AmberBright = "#F5D0A8",
            Red = "#FF8FA0",
            Selection = "#F5EEFA"
        },
        new()
        {
            Id = "harbor",
            DisplayName = "海雾",
            Description = "青蓝航海风格",
            IsDark = true,
            Background = "#07131A",
            Sidebar = "#0B1A24",
            Panel = "#102430",
            PanelSecondary = "#16303E",
            Line = "#27485A",
            Text = "#E6F4FA",
            Muted = "#87A8B8",
            Dim = "#5E7E8E",
            Accent = "#4FD0E8",
            OnAccent = "#042028",
            OnAccentDeep = "#031820",
            Blue = "#7EB6FF",
            BlueBright = "#B7D6FF",
            Amber = "#FFC56D",
            AmberBright = "#FFE0A8",
            Red = "#FF8B8B",
            Selection = "#EAF7FC"
        },
        new()
        {
            Id = "ember",
            DisplayName = "赤霞",
            Description = "暖红余烬，夜间偏暖",
            IsDark = true,
            Background = "#140A08",
            Sidebar = "#1C100C",
            Panel = "#241510",
            PanelSecondary = "#2E1C15",
            Line = "#4A3228",
            Text = "#F8EDE6",
            Muted = "#B89A8C",
            Dim = "#86685C",
            Accent = "#FF7A59",
            OnAccent = "#2A0D08",
            OnAccentDeep = "#1E0906",
            Blue = "#8FB4FF",
            BlueBright = "#C4D6FF",
            Amber = "#FFB060",
            AmberBright = "#FFD2A0",
            Red = "#FF6B7A",
            Selection = "#F8EDE6"
        },
        new()
        {
            Id = "bamboo",
            DisplayName = "竹影",
            Description = "墨绿底与青竹强调",
            IsDark = true,
            Background = "#08110C",
            Sidebar = "#0D1812",
            Panel = "#122018",
            PanelSecondary = "#182A1F",
            Line = "#2A4536",
            Text = "#EAF6EC",
            Muted = "#8DAB94",
            Dim = "#62806A",
            Accent = "#7DDB8F",
            OnAccent = "#06200C",
            OnAccentDeep = "#041808",
            Blue = "#86C4C0",
            BlueBright = "#B8E0DC",
            Amber = "#E6C36A",
            AmberBright = "#F3DDA0",
            Red = "#E88989",
            Selection = "#EEF8F0"
        },
        new()
        {
            Id = "paper",
            DisplayName = "宣纸",
            Description = "暖纸浅色，长时间阅读更柔和",
            IsDark = false,
            Background = "#F6F0E6",
            Sidebar = "#EFE6D8",
            Panel = "#FFFBF4",
            PanelSecondary = "#E9DFD0",
            Line = "#D2C4B0",
            Text = "#2A241C",
            Muted = "#6B5E4E",
            Dim = "#8A7B68",
            Accent = "#1F7A62",
            OnAccent = "#F4FFF8",
            OnAccentDeep = "#E8F8EE",
            Blue = "#3D5A99",
            BlueBright = "#2C4478",
            Amber = "#B86A12",
            AmberBright = "#8C500C",
            Red = "#B43B3B",
            Selection = "#2A241C"
        }
    ];

    public static AppearanceThemePalette Default => All[0];

    public static AppearanceThemePalette Resolve(string? themeId)
    {
        if (string.IsNullOrWhiteSpace(themeId))
        {
            return Default;
        }

        foreach (var theme in All)
        {
            if (string.Equals(theme.Id, themeId.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return theme;
            }
        }

        return Default;
    }

    public static bool TryParseRgb(string? hex, out byte red, out byte green, out byte blue)
    {
        red = 0;
        green = 0;
        blue = 0;
        if (hex is not { Length: 7 } || hex[0] != '#')
        {
            return false;
        }

        return byte.TryParse(hex.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber, null, out red)
            && byte.TryParse(hex.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber, null, out green)
            && byte.TryParse(hex.AsSpan(5, 2), System.Globalization.NumberStyles.HexNumber, null, out blue);
    }
}
