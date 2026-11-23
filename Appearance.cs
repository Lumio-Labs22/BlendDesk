using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace BlenderLibrary;

[JsonConverter(typeof(JsonStringEnumConverter<ColorMode>))]
internal enum ColorMode
{
    Light,
    Dark,
    System
}

internal static class Appearance
{
    private const uint ImmersiveDarkMode = 20;
    private const uint CaptionColor = 35;
    private const uint CaptionTextColor = 36;
    private static ColorMode colorMode;
    private static bool liquidGlass;
    private static (bool Glass, bool Dark)? applied;

    public static void Apply(bool useGlass, ColorMode mode)
    {
        if (colorMode != mode)
        {
            StopFollowingWindows();
            colorMode = mode;
            if (colorMode == ColorMode.System)
                SystemEvents.UserPreferenceChanged += SystemPreferenceChanged;
        }

        liquidGlass = useGlass;
        ApplyPalette();
    }

    public static void StopFollowingWindows()
    {
        if (colorMode == ColorMode.System)
            SystemEvents.UserPreferenceChanged -= SystemPreferenceChanged;
    }

    private static void SystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (Application.Current?.Dispatcher is { HasShutdownStarted: false } dispatcher)
            dispatcher.BeginInvoke(() =>
            {
                if (colorMode == ColorMode.System)
                    ApplyPalette();
            });
    }

    private static void ApplyPalette()
    {
        bool dark = colorMode == ColorMode.Dark || (colorMode == ColorMode.System && WindowsPrefersDark());
        if (applied == (liquidGlass, dark))
            return;

        string style = liquidGlass ? "LiquidGlass" : "Clean";
        var palette = new ResourceDictionary();
        palette.MergedDictionaries.Add(Load(style));
        if (dark)
            palette.MergedDictionaries.Add(Load(style + "Dark"));

        var app = Application.Current;
        app.Resources.MergedDictionaries[0] = palette;
        applied = (liquidGlass, dark);
        foreach (Window window in app.Windows)
            UpdateTitleBar(window);
    }

    private static ResourceDictionary Load(string name) => new()
    {
        Source = new Uri($"pack://application:,,,/BlendDesk;component/Themes/{name}.xaml", UriKind.Absolute)
    };

    private static bool WindowsPrefersDark()
    {
        const string personalization = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        try
        {
            return Registry.GetValue(personalization, "AppsUseLightTheme", 1) is 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    public static void UpdateTitleBar(Window window)
    {
        nint handle = new WindowInteropHelper(window).Handle;
        if (handle == 0 || !OperatingSystem.IsWindowsVersionAtLeast(10))
            return;

        bool dark = applied?.Dark != false;
        uint mode = dark ? 1u : 0u;
        DwmSetWindowAttribute(handle, ImmersiveDarkMode, in mode, sizeof(uint));
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            uint caption = dark ? 0x0F1114u : 0xC8DAECu;
            uint text = dark ? 0xE4F1FFu : 0x121825u;
            DwmSetWindowAttribute(handle, CaptionColor, in caption, sizeof(uint));
            DwmSetWindowAttribute(handle, CaptionTextColor, in text, sizeof(uint));
        }
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(nint handle, uint attribute, in uint value, uint size);
}
