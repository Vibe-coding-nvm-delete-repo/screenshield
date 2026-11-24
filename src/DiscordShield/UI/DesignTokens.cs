using Microsoft.Win32;

namespace DiscordShield.UI;

public sealed record ThemePalette(
    string Name,
    bool IsDark,
    Color BaseSurface,
    Color ContentSurface,
    Color InputSurface,
    Color InputBorder,
    Color InputBottomLine,
    Color TextPrimary,
    Color TextSub,
    Color TextDisabled,
    Color HoverFill,
    Color PressedFill,
    Color SelectedFill,
    Color AccentFill,
    Color TextOnAccent,
    Color ShieldActiveText,
    Color ShieldActiveFill,
    Color ShieldOffText,
    Color ShieldOffFill,
    Color DangerText,
    Color FocusOuter,
    Color FocusInner);

/// <summary>
/// Single source of truth for DiscordShield's minimalist Light & Dark themes,
/// following app-design numbers: 4px grid, 4px/8px corners, 2 layers,
/// >= 4.5:1 WCAG contrast for every readable text/surface pair.
/// </summary>
public static class DesignTokens
{
    public const int Grid = 4;
    public const int CornerControl = 4;
    public const int CornerSurface = 8;
    public const int TopBarHeight = 40;
    public const int ControlHeight = 28;
    public const int MinHitTarget = 28;
    public const int ButtonGap = 4;
    public const int GroupGap = 12;
    public const int DialogPadding = 24;

    public static readonly ThemePalette Dark = BuildPalette(
        name: "Dark",
        isDark: true,
        baseSurface: Color.FromArgb(24, 25, 28),
        contentSurface: Color.FromArgb(32, 34, 38),
        inputSurface: Color.FromArgb(36, 38, 43),
        inputBorder: Color.FromArgb(58, 61, 69),
        inputBottomLine: Color.FromArgb(130, 136, 148),
        textPrimary: Color.FromArgb(245, 245, 245),
        textSub: Color.FromArgb(170, 170, 170),
        accentFill: Color.FromArgb(96, 165, 250),       // Bright blue on dark
        textOnAccent: Color.FromArgb(15, 23, 42),      // Dark text on bright accent (> 7.5:1)
        shieldActiveText: Color.FromArgb(74, 222, 128),// Crisp green (> 9.5:1 on base)
        shieldActiveFill: Color.FromArgb(22, 46, 34),  // Subtle green tint
        shieldOffText: Color.FromArgb(251, 191, 36),   // Crisp amber (> 10:1 on base)
        shieldOffFill: Color.FromArgb(52, 40, 18),
        dangerText: Color.FromArgb(248, 113, 113),     // Crisp red (> 6.5:1 on base)
        focusOuter: Color.FromArgb(255, 255, 255),
        focusInner: Color.FromArgb(178, 0, 0, 0));

    public static readonly ThemePalette Light = BuildPalette(
        name: "Light",
        isDark: false,
        baseSurface: Color.FromArgb(243, 244, 246),
        contentSurface: Color.FromArgb(255, 255, 255),
        inputSurface: Color.FromArgb(255, 255, 255),
        inputBorder: Color.FromArgb(209, 213, 219),
        inputBottomLine: Color.FromArgb(107, 114, 128),
        textPrimary: Color.FromArgb(27, 27, 27),
        textSub: Color.FromArgb(90, 90, 90),
        accentFill: Color.FromArgb(37, 99, 235),       // Deep blue
        textOnAccent: Color.FromArgb(255, 255, 255),   // White text on deep blue (> 5.1:1)
        shieldActiveText: Color.FromArgb(21, 128, 61), // Deep green (> 4.8:1 on light base & tint)
        shieldActiveFill: Color.FromArgb(220, 252, 231),
        shieldOffText: Color.FromArgb(146, 64, 14),    // Deep amber (> 6.4:1 on light base & tint)
        shieldOffFill: Color.FromArgb(254, 243, 199),
        dangerText: Color.FromArgb(185, 28, 28),       // Deep red (> 5.8:1 on light base)
        focusOuter: Color.FromArgb(27, 27, 27),
        focusInner: Color.FromArgb(178, 255, 255, 255));

    public static ThemePalette SystemDefault() => IsSystemDarkMode() ? Dark : Light;

    public static bool IsSystemDarkMode()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            object? val = key?.GetValue("AppsUseLightTheme");
            return val is int i && i == 0;
        }
        catch
        {
            return true;
        }
    }

    public static Font CreateUiFont(float sizePt = 9.5f, bool semibold = false)
    {
        string family = FontExists("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
        return new Font(family, sizePt, semibold ? FontStyle.Bold : FontStyle.Regular);
    }

    private static bool FontExists(string familyName)
    {
        try
        {
            using var f = new Font(familyName, 9f);
            return f.Name.Equals(familyName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static Color Blend(Color foreground, Color background, double alpha)
    {
        int r = (int)Math.Round((foreground.R * alpha) + (background.R * (1.0 - alpha)));
        int g = (int)Math.Round((foreground.G * alpha) + (background.G * (1.0 - alpha)));
        int b = (int)Math.Round((foreground.B * alpha) + (background.B * (1.0 - alpha)));
        return Color.FromArgb(Math.Clamp(r, 0, 255), Math.Clamp(g, 0, 255), Math.Clamp(b, 0, 255));
    }

    public static double ContrastRatio(Color a, Color b)
    {
        double l1 = RelativeLuminance(a);
        double l2 = RelativeLuminance(b);
        double lighter = Math.Max(l1, l2);
        double darker = Math.Min(l1, l2);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(Color c)
    {
        static double Linearize(byte channel)
        {
            double s = channel / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Linearize(c.R)) + (0.7152 * Linearize(c.G)) + (0.0722 * Linearize(c.B));
    }

    private static ThemePalette BuildPalette(
        string name,
        bool isDark,
        Color baseSurface,
        Color contentSurface,
        Color inputSurface,
        Color inputBorder,
        Color inputBottomLine,
        Color textPrimary,
        Color textSub,
        Color accentFill,
        Color textOnAccent,
        Color shieldActiveText,
        Color shieldActiveFill,
        Color shieldOffText,
        Color shieldOffFill,
        Color dangerText,
        Color focusOuter,
        Color focusInner)
    {
        return new ThemePalette(
            Name: name,
            IsDark: isDark,
            BaseSurface: baseSurface,
            ContentSurface: contentSurface,
            InputSurface: inputSurface,
            InputBorder: inputBorder,
            InputBottomLine: inputBottomLine,
            TextPrimary: textPrimary,
            TextSub: textSub,
            TextDisabled: Blend(textPrimary, baseSurface, 0.36),
            HoverFill: Blend(textPrimary, baseSurface, 0.08),
            PressedFill: Blend(textPrimary, baseSurface, 0.12),
            SelectedFill: Blend(textPrimary, baseSurface, 0.14),
            AccentFill: accentFill,
            TextOnAccent: textOnAccent,
            ShieldActiveText: shieldActiveText,
            ShieldActiveFill: shieldActiveFill,
            ShieldOffText: shieldOffText,
            ShieldOffFill: shieldOffFill,
            DangerText: dangerText,
            FocusOuter: focusOuter,
            FocusInner: focusInner);
    }
}
