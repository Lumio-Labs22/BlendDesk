using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace BlenderLibrary;

public partial class SettingsWindow : AnimatedWindow
{
    private readonly string? detectedBlender;
    private bool closed;
    internal string? BlenderPath { get; private set; }
    internal string? BackgroundPath { get; private set; }
    internal BitmapSource? SelectedBackground { get; private set; }
    internal event Action<BitmapSource?>? BackgroundChanged;
    internal bool LiquidGlass => GlassChoice.IsChecked == true;
    internal ColorMode SelectedColorMode => DarkChoice.IsChecked == true ? ColorMode.Dark
        : SystemChoice.IsChecked == true ? ColorMode.System : ColorMode.Light;

    internal SettingsWindow(LibrarySettings settings, string? detectedBlender, BitmapSource? background)
    {
        InitializeComponent();
        MaxHeight = Math.Min(790, SystemParameters.WorkArea.Height);
        this.detectedBlender = detectedBlender;
        BlenderPath = settings.BlenderPath;
        GlassChoice.IsChecked = settings.LiquidGlass;
        CleanChoice.IsChecked = !settings.LiquidGlass;
        DarkChoice.IsChecked = settings.ColorMode == ColorMode.Dark;
        SystemChoice.IsChecked = settings.ColorMode == ColorMode.System;
        LightChoice.IsChecked = settings.ColorMode is not (ColorMode.Dark or ColorMode.System);
        SourceInitialized += (_, _) => Appearance.UpdateTitleBar(this);
        UpdateLocation();
        UpdateBackground(settings.BackgroundPath, background);
    }

    protected override void OnClosed(EventArgs e)
    {
        closed = true;
        base.OnClosed(e);
    }

    private void PreviewAppearance(object sender, RoutedEventArgs e)
    {
        Appearance.Apply(LiquidGlass, SelectedColorMode);
        if (Content is UIElement surface)
            Motion.FadeIn(surface, .78);
    }

    private async void ChooseImage(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a background image",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
            return;

        string path = dialog.FileName;
        ChooseImageButton.IsEnabled = DefaultBackgroundButton.IsEnabled = DoneButton.IsEnabled = false;
        try
        {
            BitmapSource image = await Task.Run(() => BackgroundImage.Load(path));
            if (!closed)
            {
                UpdateBackground(path, image);
                BackgroundChanged?.Invoke(image);
            }
        }
        catch (Exception error) when (BackgroundImage.IsLoadError(error))
        {
            if (!closed)
                MessageBox.Show(this, "That image could not be opened. Choose a PNG, JPEG, or BMP file.",
                    Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            if (!closed)
            {
                ChooseImageButton.IsEnabled = DoneButton.IsEnabled = true;
                DefaultBackgroundButton.IsEnabled = BackgroundPath is not null;
            }
        }
    }

    private void UseDefaultBackground(object sender, RoutedEventArgs e)
    {
        UpdateBackground(null, null);
        BackgroundChanged?.Invoke(null);
    }

    private void UpdateBackground(string? path, BitmapSource? image)
    {
        BackgroundPath = path;
        SelectedBackground = image;
        BackgroundThumbnail.ImageSource = image;
        DefaultThumbnail.Visibility = image is null ? Visibility.Visible : Visibility.Collapsed;
        BackgroundName.Text = path is null ? "Default background"
            : image is null ? "Image unavailable" : Path.GetFileName(path);
        BackgroundName.ToolTip = path;
        DefaultBackgroundButton.IsEnabled = path is not null;
    }

    private void BrowseBlender(object sender, RoutedEventArgs e)
    {
        if (PickBlender(this, BlenderPath ?? detectedBlender) is string path)
        {
            BlenderPath = path;
            UpdateLocation();
        }
    }

    private void UseAutomatic(object sender, RoutedEventArgs e)
    {
        BlenderPath = null;
        UpdateLocation();
    }

    private void UpdateLocation()
    {
        LocationMode.Text = BlenderPath is null ? "Automatic detection" : "Custom executable";
        Location.Text = BlenderPath ?? detectedBlender ?? "Blender installation not found";
        AutomaticButton.IsEnabled = BlenderPath is not null;
    }

    private void Done(object sender, RoutedEventArgs e) => DialogResult = true;

    internal static string? PickBlender(Window owner, string? suggested)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose the Blender executable",
            Filter = "Blender (blender.exe)|blender.exe",
            CheckFileExists = true,
            FileName = suggested ?? "blender.exe"
        };
        if (dialog.ShowDialog(owner) != true)
            return null;

        if (Path.GetFileName(dialog.FileName).Equals("blender.exe", StringComparison.OrdinalIgnoreCase))
            return dialog.FileName;

        MessageBox.Show(owner, "Choose the blender.exe file.", owner.Title, MessageBoxButton.OK, MessageBoxImage.Information);
        return null;
    }
}
