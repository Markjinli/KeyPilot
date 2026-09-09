using KeyPilot.Core.Configuration;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace KeyPilot.App.Presentation;

internal static class AppearanceThemeApplier
{
    public static void Apply(AppearanceThemePalette theme, FrameworkElement? root)
    {
        ArgumentNullException.ThrowIfNull(theme);

        var resources = Application.Current.Resources;
        var background = Parse(theme.Background);
        var sidebar = Parse(theme.Sidebar);
        var panel = Parse(theme.Panel);
        var panelSecondary = Parse(theme.PanelSecondary);
        var line = Parse(theme.Line);
        var text = Parse(theme.Text);
        var muted = Parse(theme.Muted);
        var dim = Parse(theme.Dim);
        var accent = Parse(theme.Accent);
        var onAccent = Parse(theme.OnAccent);
        var onAccentDeep = Parse(theme.OnAccentDeep);
        var blue = Parse(theme.Blue);
        var blueBright = Parse(theme.BlueBright);
        var amber = Parse(theme.Amber);
        var amberBright = Parse(theme.AmberBright);
        var red = Parse(theme.Red);
        var selection = Parse(theme.Selection);
        var hairline = Overlay(theme.IsDark, 0x0F);
        var subtleStroke = Overlay(theme.IsDark, 0x1A);
        var subtleFill = Overlay(theme.IsDark, 0x0E);
        var faintFill = Overlay(theme.IsDark, 0x12);
        var accentSoftAlpha = theme.IsDark ? (byte)0x21 : (byte)0x28;
        var accentFillAlpha = theme.IsDark ? (byte)0x1C : (byte)0x24;

        SetColor(resources, "KpBackgroundColor", background);
        SetColor(resources, "KpSidebarColor", sidebar);
        SetColor(resources, "KpPanelColor", panel);
        SetColor(resources, "KpPanelSecondaryColor", panelSecondary);
        SetColor(resources, "KpLineColor", line);
        SetColor(resources, "KpTextColor", text);
        SetColor(resources, "KpMutedColor", muted);
        SetColor(resources, "KpDimColor", dim);
        SetColor(resources, "KpMintColor", accent);
        SetColor(resources, "KpBlueColor", blue);
        SetColor(resources, "KpAmberColor", amber);
        SetColor(resources, "KpRedColor", red);

        SetBrush(resources, "KpBackgroundBrush", background);
        SetBrush(resources, "KpSidebarBrush", sidebar);
        SetBrush(resources, "KpPanelBrush", panel);
        SetBrush(resources, "KpPanelSecondaryBrush", panelSecondary);
        SetBrush(resources, "KpLineBrush", line);
        SetBrush(resources, "KpTextBrush", text);
        SetBrush(resources, "KpMutedBrush", muted);
        SetBrush(resources, "KpDimBrush", dim);
        SetBrush(resources, "KpMintBrush", accent);
        SetBrush(resources, "KpBlueBrush", blue);
        SetBrush(resources, "KpAmberBrush", amber);
        SetBrush(resources, "KpRedBrush", red);
        SetBrush(resources, "KpOnAccentBrush", onAccent);
        SetBrush(resources, "KpOnAccentDeepBrush", onAccentDeep);
        SetBrush(resources, "KpBlueBrightBrush", blueBright);
        SetBrush(resources, "KpAmberBrightBrush", amberBright);
        SetBrush(resources, "KpSelectionBrush", selection);
        SetBrush(resources, "KpHairlineBrush", hairline);
        SetBrush(resources, "KpSubtleStrokeBrush", subtleStroke);
        SetBrush(resources, "KpSubtleFillBrush", subtleFill);
        SetBrush(resources, "KpFaintFillBrush", faintFill);
        SetBrush(resources, "KpAccentSoftBrush", WithAlpha(accent, accentSoftAlpha));
        SetBrush(resources, "KpAccentSoftBorderBrush", WithAlpha(accent, 0x2E));
        SetBrush(resources, "KpAccentRingBrush", WithAlpha(accent, 0x4D));
        SetBrush(resources, "KpAccentFillBrush", WithAlpha(accent, accentFillAlpha));
        SetBrush(resources, "KpAccentWashBrush", WithAlpha(accent, 0x08));
        SetBrush(resources, "KpBlueSoftBrush", WithAlpha(blue, 0x0B));
        SetBrush(resources, "KpBlueSoftBorderBrush", WithAlpha(blue, 0x21));
        SetBrush(resources, "KpBlueBadgeBrush", WithAlpha(blue, 0x1F));
        SetBrush(resources, "KpBlueBadgeBorderBrush", WithAlpha(blue, 0x33));
        SetBrush(resources, "KpAmberSoftBrush", WithAlpha(amber, 0x0A));
        SetBrush(resources, "KpAmberSoftBorderBrush", WithAlpha(amber, 0x26));
        SetBrush(resources, "KpTopBarBrush", WithAlpha(background, 0xD6));
        SetBrush(resources, "KpFooterBrush", panelSecondary);
        SetBrush(resources, "KpScrimBrush", theme.IsDark
            ? ColorHelper.FromArgb(0xAA, 3, 6, 9)
            : ColorHelper.FromArgb(0x99, 28, 24, 18));
        SetBrush(resources, "KpDeviceShellBrush", panelSecondary);
        SetBrush(resources, "KpDeviceShellLineBrush", line);

        if (root is not null)
        {
            root.RequestedTheme = theme.IsDark ? ElementTheme.Dark : ElementTheme.Light;
        }
    }

    public static Color Parse(string hex)
    {
        if (!AppearanceThemeCatalog.TryParseRgb(hex, out var red, out var green, out var blue))
        {
            throw new ArgumentException($"Invalid theme color '{hex}'.", nameof(hex));
        }

        return ColorHelper.FromArgb(255, red, green, blue);
    }

    private static Color WithAlpha(Color color, byte alpha) =>
        ColorHelper.FromArgb(alpha, color.R, color.G, color.B);

    private static Color Overlay(bool isDark, byte alpha) =>
        isDark
            ? ColorHelper.FromArgb(alpha, 255, 255, 255)
            : ColorHelper.FromArgb(alpha, 0, 0, 0);

    private static void SetColor(ResourceDictionary resources, string key, Color color) =>
        resources[key] = color;

    private static void SetBrush(ResourceDictionary resources, string key, Color color)
    {
        if (resources[key] is SolidColorBrush brush)
        {
            brush.Color = color;
            return;
        }

        resources[key] = new SolidColorBrush(color);
    }
}
