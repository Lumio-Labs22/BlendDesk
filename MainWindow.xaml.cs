using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace BlenderLibrary;

public partial class MainWindow : AnimatedWindow
{
    private readonly List<Project> projects = [];
    private readonly ICollectionView view;
    private LibrarySettings settings = new();
    private Task backgroundLoad = Task.CompletedTask;
    private BitmapSource? savedBackground;
    private CancellationTokenSource? scan;
    private CancellationTokenSource? previewScan;
    private readonly CancellationTokenSource closing = new();
    private readonly SemaphoreSlim renderQueue = new(1);
    private int skippedFolders;
    private int missingFiles;
    private string query = "";
    private Selector ActiveProjects => Cards.Visibility == Visibility.Visible ? Cards : Projects;
    private Project? SelectedProject => ActiveProjects.SelectedItem as Project;

    public MainWindow()
    {
        InitializeComponent();
        view = new ListCollectionView(projects);
        ApplySort(ProjectSort.Modified);
        view.Filter = MatchesFilter;
        Projects.ItemsSource = view;
        Cards.ItemsSource = view;
        SourceInitialized += (_, _) => Appearance.UpdateTitleBar(this);
        Loaded += LoadLibrary;
    }

    private async void LoadLibrary(object sender, RoutedEventArgs e)
    {
        Loaded -= LoadLibrary;
        try
        {
            settings = Library.Load();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            ShowError("Settings could not be loaded. You can add your library sources again.", error);
        }

        Appearance.Apply(settings.LiquidGlass, settings.ColorMode);
        ApplySort(settings.Sort);
        UpdateSources();
        ApplyProjectView(settings.ShowCards);
        backgroundLoad = RestoreBackground();
        await Task.WhenAll(backgroundLoad, ScanLibrary());
    }

    private async Task RestoreBackground()
    {
        if (settings.BackgroundPath is not string path)
            return;

        try
        {
            var image = await Task.Run(() => BackgroundImage.Load(path));
            if (!closing.IsCancellationRequested)
            {
                savedBackground = image;
                DisplayBackground(image);
            }
        }
        catch (Exception error) when (BackgroundImage.IsLoadError(error))
        {
            // Keep the theme background if the saved image is unavailable.
        }
    }

    private void DisplayBackground(BitmapSource? image)
    {
        bool changed = !ReferenceEquals(BackdropImage.Source, image);
        BackdropImage.Source = image;
        CustomBackdrop.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
        if (image is not null && changed)
            Motion.FadeIn(CustomBackdrop, .55);
    }

    protected override void OnClosed(EventArgs e)
    {
        scan?.Cancel();
        previewScan?.Cancel();
        closing.Cancel();
        base.OnClosed(e);
    }

    private async void AddFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose Blender project folders",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != true)
            return;

        await AddSources(dialog.FolderNames, []);
    }

    private async void AddFile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Add Blender projects",
            Filter = "Blender projects (*.blend)|*.blend",
            CheckFileExists = true,
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != true)
            return;

        if (!dialog.FileNames.Any(IsBlendFile))
        {
            MessageBox.Show(this, "Choose one or more .blend files.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await AddSources([], dialog.FileNames);
    }

    private async Task AddSources(IEnumerable<string> folders, IEnumerable<string> files)
    {
        var updated = settings with
        {
            Folders = settings.Folders.Concat(folders.Select(Path.TrimEndingDirectorySeparator))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Files = settings.Files.Concat(files.Where(IsBlendFile)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };
        if (updated.Folders.Count != settings.Folders.Count || updated.Files.Count != settings.Files.Count)
            await ChangeSources(updated);
    }

    private static bool IsBlendFile(string path) =>
        Path.GetExtension(path).Equals(".blend", StringComparison.OrdinalIgnoreCase);

    private void ShowDropTarget(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop, autoConvert: false))
            return;

        e.Effects = e.AllowedEffects.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.Visibility = e.Effects == DragDropEffects.Copy ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void HideDropTarget(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
    }

    private async void ImportDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(DataFormats.FileDrop, autoConvert: false) is not string[] paths)
            return;
        e.Handled = true;

        var (folders, files) = await Task.Run(() => (
            paths.Where(Directory.Exists).ToArray(),
            paths.Where(path => IsBlendFile(path) && File.Exists(path)).ToArray()));
        if (closing.IsCancellationRequested)
            return;
        if (folders.Length + files.Length == 0)
        {
            MessageBox.Show(this, "Drop .blend files or folders containing Blender projects.", Title,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await AddSources(folders, files);
    }

    private async void RemoveSource(object sender, RoutedEventArgs e)
    {
        if (Sources.SelectedItem is not LibrarySource source)
            return;

        LibrarySettings updated = source.IsFile
            ? settings with { Files = settings.Files.Where(path => path != source.Path).ToList() }
            : settings with { Folders = settings.Folders.Where(path => path != source.Path).ToList() };
        await ChangeSources(updated);
    }

    private async Task ChangeSources(LibrarySettings updated)
    {
        if (!SaveSettings(updated))
            return;

        UpdateSources();
        await ScanLibrary();
    }

    private void UpdateSources() => Sources.ItemsSource = settings.Folders.Select(path => new LibrarySource(path, false))
        .Concat(settings.Files.Select(path => new LibrarySource(path, true))).ToArray();

    private void SourceSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RemoveSourceButton.IsEnabled = Sources.SelectedItem is LibrarySource;

    private async void RefreshLibrary(object sender, RoutedEventArgs e) => await ScanLibrary();

    private void ViewChanged(object sender, RoutedEventArgs e)
    {
        bool showCards = ReferenceEquals(sender, CardViewOption);
        if (settings.ShowCards != showCards)
            SaveSettings(settings with { ShowCards = showCards });

        ApplyProjectView(settings.ShowCards, animate: Motion.Enabled);
    }

    private void ApplyProjectView(bool showCards, bool animate = false)
    {
        Selector previousView = ActiveProjects;
        Project? selected = SelectedProject;
        ListViewOption.IsChecked = !showCards;
        CardViewOption.IsChecked = showCards;
        Projects.Visibility = showCards ? Visibility.Collapsed : Visibility.Visible;
        Cards.Visibility = showCards ? Visibility.Visible : Visibility.Collapsed;
        ActiveProjects.SelectedItem = selected;
        if (selected is not null)
        {
            if (showCards)
                Cards.ScrollIntoView(selected);
            else
                Projects.ScrollIntoView(selected);
        }
        UpdateProjectActions();

        if (previousView != ActiveProjects)
        {
            previousView.BeginAnimation(OpacityProperty, null);
            previousView.RenderTransform = Transform.Identity;
            if (animate)
                AnimateProjectView(ActiveProjects, showCards ? -26 : 26);
        }

        RefreshCardPreviews();
    }

    private static void AnimateProjectView(Selector projectView, double offset)
    {
        var slide = new TranslateTransform();
        projectView.RenderTransform = slide;
        slide.BeginAnimation(TranslateTransform.XProperty, Motion.Ease(offset, 0, 225));
        projectView.BeginAnimation(OpacityProperty, Motion.Ease(.62, 1, 225));
    }

    private async Task ScanLibrary()
    {
        scan?.Cancel();
        using var current = new CancellationTokenSource();
        scan = current;
        UpdateStatus();

        try
        {
            string[] folders = settings.Folders.ToArray();
            string[] files = settings.Files.ToArray();
            var result = await Task.Run(() => Library.Scan(folders, files, current.Token), current.Token);
            if (current.IsCancellationRequested)
                return;

            string? selectedPath = SelectedProject?.Path;
            var previous = projects.ToDictionary(project => project.Path, StringComparer.OrdinalIgnoreCase);
            projects.Clear();
            foreach (Project project in result.Projects)
            {
                Project updated = project with
                {
                    Favorite = settings.Favorites.Contains(project.Path),
                    Details = settings.Details.GetValueOrDefault(project.Path) ?? new()
                };
                if (previous.TryGetValue(project.Path, out Project? old) &&
                    old.Modified == project.Modified && old.Size == project.Size)
                    updated = updated with { Preview = old.Preview };
                projects.Add(updated);
            }
            view.Refresh();
            ActiveProjects.SelectedItem = projects.FirstOrDefault(project =>
                project.Path.Equals(selectedPath, StringComparison.OrdinalIgnoreCase));
            skippedFolders = result.SkippedFolders;
            missingFiles = result.MissingFiles;
            RefreshCardPreviews();
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested) { }
        finally
        {
            if (scan == current)
            {
                scan = null;
                if (!current.IsCancellationRequested)
                    UpdateStatus();
            }
        }
    }

    private bool MatchesFilter(object item) =>
        item is Project project &&
        (FavoritesOnly.IsChecked != true || project.Favorite) &&
        (RecentlyOpened.IsChecked != true || project.Details.LastOpened is not null) &&
        (project.Path.Contains(query, StringComparison.OrdinalIgnoreCase) ||
         project.Details.Notes.Contains(query, StringComparison.OrdinalIgnoreCase));

    private void FilterChanged(object sender, RoutedEventArgs e)
    {
        if (view is null)
            return;

        Project? selected = SelectedProject;
        query = Search.Text.Trim();
        view.Refresh();
        if (selected is not null && view.Contains(selected))
            ActiveProjects.SelectedItem = selected;
        UpdateStatus();
        RefreshCardPreviews();
    }

    private void ProjectFilterChanged(object sender, RoutedEventArgs e)
    {
        ApplySort(RecentlyOpened.IsChecked == true ? ProjectSort.LastOpened : settings.Sort);
        FilterChanged(sender, e);
        Motion.FadeIn(ActiveProjects, .72);
    }

    private void ClearFilters(object sender, RoutedEventArgs e)
    {
        AllProjects.IsChecked = true;
        Search.Clear();
        ProjectFilterChanged(sender, e);
        Search.Focus();
    }

    private void OpenSortMenu(object sender, RoutedEventArgs e)
    {
        SortDescription primary = view.SortDescriptions.FirstOrDefault();
        foreach (MenuItem item in SortButton.ContextMenu.Items)
        {
            SortDescription order = SortOrder((ProjectSort)item.Tag);
            item.IsChecked = order.PropertyName == primary.PropertyName && order.Direction == primary.Direction;
        }
        SortButton.ContextMenu.PlacementTarget = SortButton;
        SortButton.ContextMenu.IsOpen = true;
    }

    private void SortChanged(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: ProjectSort sort } && SaveSettings(settings with { Sort = sort }))
            ApplySort(sort);
    }

    private void ApplySort(ProjectSort sort)
    {
        Project? selected = SelectedProject;
        SortDescription order = SortOrder(sort);
        using (view.DeferRefresh())
        {
            view.SortDescriptions.Clear();
            view.SortDescriptions.Add(order);
        }
        if (selected is not null && view.Contains(selected))
            ActiveProjects.SelectedItem = selected;
        foreach (DataGridColumn column in Projects.Columns)
            column.SortDirection = column.SortMemberPath == order.PropertyName ? order.Direction : null;
    }

    private static SortDescription SortOrder(ProjectSort sort) => sort switch
    {
        ProjectSort.Name => new(nameof(Project.Name), ListSortDirection.Ascending),
        ProjectSort.Size => new(nameof(Project.Size), ListSortDirection.Descending),
        ProjectSort.LastOpened => new(nameof(Project.LastOpened), ListSortDirection.Descending),
        _ => new(nameof(Project.Modified), ListSortDirection.Descending)
    };

    private void UpdateStatus()
    {
        int shown = ActiveProjects.Items.Count;
        EmptyState.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
        string count = shown == projects.Count
            ? $"{shown:N0} {(shown == 1 ? "project" : "projects")}"
            : $"{shown:N0} of {projects.Count:N0} projects";
        Status.Text = scan is not null ? "Scanning sources…" : count;
        if (scan is null && skippedFolders + missingFiles > 0)
        {
            int unavailable = skippedFolders + missingFiles;
            Status.Text += $" · {unavailable:N0} unavailable";
            Status.ToolTip = $"{skippedFolders:N0} folders could not be fully scanned. {missingFiles:N0} added files are unavailable.";
        }
        else
        {
            Status.ToolTip = null;
        }

        bool hasSources = settings.Folders.Count + settings.Files.Count > 0;
        (EmptyTitle.Text, EmptyDetail.Text) = scan is not null ? ("Scanning your library", "")
            : !hasSources ? ("Start your project library", "Drop .blend files or folders here to get started.")
            : projects.Count == 0 ? ("No projects found", "Check your sources or add another file.")
            : query.Length > 0 ? ("No matching projects", "Try searching by file name, folder, or note.")
            : RecentlyOpened.IsChecked == true ? ("No projects opened yet", "Projects you launch from BlendDesk will appear here.")
            : ("No favorites yet", "Select any project and choose Add favorite.");
        EmptyAddButton.Visibility = scan is null && projects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearFiltersButton.Visibility = scan is null && projects.Count > 0 && shown == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ProjectSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (view is not null && ReferenceEquals(sender, ActiveProjects))
            UpdateProjectActions();
    }

    private void UpdateProjectActions()
    {
        Project? project = SelectedProject;
        OpenButton.IsEnabled = RevealButton.IsEnabled = FavoriteButton.IsEnabled = DetailsButton.IsEnabled = project is not null;
        FavoriteButton.Content = project?.Favorite == true ? "Remove favorite" : "Add favorite";
    }

    private void ToggleFavorite(object sender, RoutedEventArgs e)
    {
        if (SelectedProject is not Project project)
            return;

        var favorites = new HashSet<string>(settings.Favorites, StringComparer.OrdinalIgnoreCase);
        if (project.Favorite)
            favorites.Remove(project.Path);
        else
            favorites.Add(project.Path);

        if (!SaveSettings(settings with { Favorites = favorites }))
            return;

        ReplaceProject(project, project with { Favorite = !project.Favorite });
    }

    private void ReplaceProject(Project project, Project updated)
    {
        projects[projects.IndexOf(project)] = updated;
        view.Refresh();
        if (view.Contains(updated))
            ActiveProjects.SelectedItem = updated;
        UpdateStatus();
        RefreshCardPreviews();
    }

    private bool SaveProjectDetails(string path, ProjectDetails details)
    {
        var entries = new Dictionary<string, ProjectDetails>(settings.Details, StringComparer.OrdinalIgnoreCase);
        if (details.Notes.Length == 0 && details.LastOpened is null)
            entries.Remove(path);
        else
            entries[path] = details;

        if (!SaveSettings(settings with { Details = entries }))
            return false;

        Project? project = projects.Find(candidate => candidate.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (project is not null)
            ReplaceProject(project, project with { Details = details });
        return true;
    }

    private void ShowProjectDetails(object sender, RoutedEventArgs e)
    {
        if (SelectedProject is not Project project)
            return;

        var dialog = new ProjectDetailsWindow(project, notes =>
        {
            ProjectDetails current = settings.Details.GetValueOrDefault(project.Path) ?? project.Details;
            return current.Notes == notes || SaveProjectDetails(project.Path, current with { Notes = notes });
        }) { Owner = this };
        dialog.ShowDialog();
    }

    private async void OpenSettings(object sender, RoutedEventArgs e)
    {
        if (!backgroundLoad.IsCompleted)
        {
            SettingsButton.IsEnabled = false;
            await backgroundLoad;
            SettingsButton.IsEnabled = true;
        }
        if (closing.IsCancellationRequested)
            return;

        string? detected;
        try { detected = Library.FindBlender(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { detected = null; }

        var dialog = new SettingsWindow(settings, detected, savedBackground) { Owner = this };
        dialog.BackgroundChanged += DisplayBackground;
        try
        {
            if (dialog.ShowDialog() != true)
                return;

            bool blenderChanged = !string.Equals(settings.BlenderPath, dialog.BlenderPath, StringComparison.OrdinalIgnoreCase);
            if (SaveSettings(settings with
            {
                BlenderPath = dialog.BlenderPath,
                LiquidGlass = dialog.LiquidGlass,
                ColorMode = dialog.SelectedColorMode,
                BackgroundPath = dialog.BackgroundPath
            }))
            {
                savedBackground = dialog.SelectedBackground;
                if (blenderChanged)
                    RetryMissingPreviews();
            }
        }
        finally
        {
            dialog.BackgroundChanged -= DisplayBackground;
            Appearance.Apply(settings.LiquidGlass, settings.ColorMode);
            DisplayBackground(savedBackground);
        }
    }

    private string? PickBlender()
    {
        string? path = SettingsWindow.PickBlender(this, settings.BlenderPath);
        if (path is null || !SaveSettings(settings with { BlenderPath = path }))
            return null;

        RetryMissingPreviews();
        return path;
    }

    private void RetryMissingPreviews()
    {
        foreach (Project project in projects.Where(project => project.Preview.CanRender))
            project.Preview.Update(null, false, "Loading scene preview…", ready: false);
        RefreshCardPreviews();
    }

    private string? ResolveBlender() => File.Exists(settings.BlenderPath) ? settings.BlenderPath : Library.FindBlender();

    private void OpenProject(object sender, RoutedEventArgs e)
    {
        if (SelectedProject is not Project project || !ProjectExists(project))
            return;

        try
        {
            string? executable = ResolveBlender();
            executable ??= PickBlender();
            if (executable is null)
                return;

            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = project.Folder,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            start.ArgumentList.Add(project.Path);
            using var process = Process.Start(start);
            if (process is not null)
                SaveProjectDetails(project.Path, project.Details with { LastOpened = DateTimeOffset.UtcNow });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
        {
            ShowError("The project could not be opened in Blender.", error);
        }
    }

    private async void RefreshCardPreviews()
    {
        previewScan?.Cancel();
        if (Cards.Visibility != Visibility.Visible || closing.IsCancellationRequested)
            return;

        Project[] pending = Cards.Items.OfType<Project>().Where(project => !project.Preview.IsReady).ToArray();
        if (pending.Length == 0)
            return;

        using var current = new CancellationTokenSource();
        previewScan = current;
        try
        {
            string? blender;
            try { blender = ResolveBlender(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { blender = null; }

            foreach (Project project in pending)
            {
                var image = await Task.Run(() => ScenePreviews.Read(project, blender), current.Token);
                if (current.IsCancellationRequested)
                    return;

                project.Preview.Update(image, image is null, "No saved scene preview");
            }
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested) { }
        finally
        {
            if (previewScan == current)
                previewScan = null;
        }
    }

    private async void RenderScenePreview(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: Project project } || !project.Preview.CanRender || !ProjectExists(project))
            return;

        var preview = project.Preview;
        preview.Update(null, false, "Waiting to generate preview…");
        bool queueEntered = false;
        try
        {
            string? blender = ResolveBlender() ?? PickBlender();
            if (blender is null)
            {
                preview.Update(null, true, "No saved scene preview");
                return;
            }

            await renderQueue.WaitAsync(closing.Token);
            queueEntered = true;
            preview.Update(null, false, "Generating scene preview…");
            var image = await Task.Run(() => ScenePreviews.Render(project, blender, closing.Token), closing.Token);
            preview.Update(image, false, "");
        }
        catch (OperationCanceledException) when (closing.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            Win32Exception or TimeoutException or FormatException or NotSupportedException)
        {
            preview.Update(null, true, "Preview could not be generated");
            ShowError("The scene preview could not be generated.", error);
        }
        finally
        {
            if (queueEntered)
                renderQueue.Release();
        }
    }

    private void RevealProject(object sender, RoutedEventArgs e)
    {
        if (SelectedProject is not Project project || !ProjectExists(project))
            return;

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{project.Path}\"")
            {
                UseShellExecute = false
            })?.Dispose();
        }
        catch (Win32Exception error)
        {
            ShowError("The folder could not be opened.", error);
        }
    }

    private bool ProjectExists(Project project)
    {
        if (File.Exists(project.Path))
            return true;

        MessageBox.Show(this, "This file is unavailable. Check its location and refresh the library.",
            Title, MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    private void OpenProjectItem(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.OriginalSource is not Visual source)
            return;

        DependencyObject? item = ItemsControl.ContainerFromElement(ActiveProjects, source);
        if (item is not (DataGridRow or ListBoxItem))
            return;

        for (DependencyObject? element = source; element is Visual && element != item; element = VisualTreeHelper.GetParent(element))
            if (element is Button)
                return;

        OpenProject(sender, e);
    }

    private void ProjectKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.OriginalSource is not Button && SelectedProject is not null)
        {
            e.Handled = true;
            OpenProject(sender, e);
        }
    }

    private async void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            e.Handled = true;
            await ScanLibrary();
        }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            Search.Focus();
            Search.SelectAll();
        }
        else if (e.Key == Key.I && Keyboard.Modifiers == ModifierKeys.Control && SelectedProject is not null)
        {
            e.Handled = true;
            ShowProjectDetails(sender, e);
        }
    }

    private bool SaveSettings(LibrarySettings updated)
    {
        try
        {
            Library.Save(updated);
            settings = updated;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowError("Settings could not be saved.", error);
            return false;
        }
    }

    private void ShowError(string message, Exception error) =>
        MessageBox.Show(this, $"{message}\n\n{error.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
}
