using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;

namespace BlenderLibrary;

public partial class ProjectDetailsWindow : AnimatedWindow
{
    private readonly string path;
    private readonly Func<string, bool> save;

    internal ProjectDetailsWindow(Project project, Func<string, bool> save)
    {
        InitializeComponent();
        DataContext = project;
        path = project.Path;
        this.save = save;
        ModifiedValue.Text = project.Modified.ToString("MMM d, yyyy · HH:mm", CultureInfo.InvariantCulture);
        OpenedValue.Text = project.Details.LastOpened?.ToLocalTime()
            .ToString("MMM d, yyyy · HH:mm", CultureInfo.InvariantCulture) ?? "Never";
        Notes.Text = project.Details.Notes;
        SourceInitialized += (_, _) => Appearance.UpdateTitleBar(this);
    }

    private void CopyPath(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(path);
            CopyPathButton.Content = "Copied";
        }
        catch (ExternalException)
        {
            MessageBox.Show(this, "The clipboard is busy. Select the path to copy it manually.", Title,
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void SaveNotes(object sender, RoutedEventArgs e)
    {
        if (save(Notes.Text.Trim()))
            DialogResult = true;
    }
}
