using Avalonia;
using Avalonia.Styling;

namespace AiCodeAgent.App.Services;

/// <summary>
/// Applies and reports the application theme. Modes are persisted in
/// <c>UiConfiguration.Theme</c> as "dark", "light" or "system".
/// </summary>
public static class ThemeService
{
    public const string Dark = "dark";
    public const string Light = "light";
    public const string System = "system";

    public static string Normalize(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        Light => Light,
        System => System,
        _ => Dark
    };

    /// <summary>Switches the whole application to <paramref name="mode"/>.</summary>
    public static void Apply(string? mode)
    {
        var app = Application.Current;
        if (app == null)
            return;

        app.RequestedThemeVariant = Normalize(mode) switch
        {
            Light => ThemeVariant.Light,
            System => ThemeVariant.Default,
            _ => ThemeVariant.Dark
        };
    }

    /// <summary>True when the theme currently in effect is dark (resolves "system").</summary>
    public static bool IsDark => Application.Current?.ActualThemeVariant != ThemeVariant.Light;
}
