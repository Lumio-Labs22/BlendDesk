using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace BlenderLibrary;

internal sealed class ScenePreview : INotifyPropertyChanged
{
    public BitmapSource? Image { get; private set; }
    public bool CanRender { get; private set; }
    public bool IsReady { get; private set; }
    public string Message { get; private set; } = "Loading scene preview…";
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(BitmapSource? image, bool canRender, string message, bool ready = true)
    {
        Image = image;
        CanRender = canRender;
        Message = message;
        IsReady = ready;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}

internal static class ScenePreviews
{
    private static readonly string CacheFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Lumio Labs", "BlenderLibrary", "ScenePreviews");
    private static readonly Guid BlenderThumbnailHandler = new("D45F043D-F17F-4E8A-8435-70971D9FA46D");
    private const int ThumbnailEdge = 256;

    public static BitmapSource? Read(Project project, string? blender)
    {
        try
        {
            if (File.Exists(CachePath(project)))
                return ReadPng(CachePath(project));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or NotSupportedException)
        {
            // A stale cache entry can be replaced by the next render.
        }

        if (blender is null)
            return null;

        try
        {
            return ReadEmbedded(project.Path, Path.Combine(Path.GetDirectoryName(blender)!, "BlendThumb.dll"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            DllNotFoundException or BadImageFormatException or COMException or InvalidCastException)
        {
            return null;
        }
    }

    public static async Task<BitmapSource> Render(Project project, string blender, CancellationToken cancellationToken)
    {
        string destination = CachePath(project);
        Directory.CreateDirectory(CacheFolder);
        string output = Path.Combine(CacheFolder, Guid.NewGuid().ToString("N") + ".png");
        try
        {
            using var resource = typeof(ScenePreviews).Assembly.GetManifestResourceStream("BlendDesk.render_preview.py")!;
            using var script = new StreamReader(resource);
            var start = new ProcessStartInfo(blender)
            {
                WorkingDirectory = project.Folder,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (string argument in new[]
            {
                "--background", "--factory-startup", "--disable-autoexec", project.Path,
                "--python-exit-code", "1", "--python-expr", await script.ReadToEndAsync(cancellationToken)
            })
                start.ArgumentList.Add(argument);
            start.Environment["BLENDDESK_PREVIEW_OUTPUT"] = output;

            using var process = Process.Start(start) ?? throw new IOException("Blender could not start.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            using var stop = timeout.Token.Register(() =>
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
            });
            await process.WaitForExitAsync(timeout.Token);
            cancellationToken.ThrowIfCancellationRequested();
            if (timeout.IsCancellationRequested)
                throw new TimeoutException("Blender took too long to generate the preview.");

            if (process.ExitCode != 0 || !File.Exists(output))
                throw new IOException("Blender could not render an image for this scene.");

            BitmapSource image = ReadPng(output);
            File.Move(output, destination, overwrite: true);
            return image;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Blender took too long to generate the preview.");
        }
        finally
        {
            File.Delete(output);
        }
    }

    private static string CachePath(Project project)
    {
        string key = $"{project.Path.ToUpperInvariant()}\n{project.Modified.ToUniversalTime().Ticks}\n{project.Size}";
        return Path.Combine(CacheFolder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".png");
    }

    private static BitmapSource ReadPng(string path)
    {
        using var file = File.OpenRead(path);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = file;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource? ReadEmbedded(string path, string dll)
    {
        if (!NativeLibrary.TryLoad(dll, typeof(ScenePreviews).Assembly,
            DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32, out nint module))
            return null;

        IClassFactory? factory = null;
        object? provider = null;
        IStream? stream = null;
        nint bitmap = 0;
        try
        {
            if (!NativeLibrary.TryGetExport(module, "DllGetClassObject", out nint export))
                return null;

            var getFactory = Marshal.GetDelegateForFunctionPointer<GetClassObject>(export);
            if (getFactory(BlenderThumbnailHandler, typeof(IClassFactory).GUID, out factory) != 0)
                return null;

            if (factory.CreateInstance(0, typeof(IInitializeWithStream).GUID, out provider) != 0 ||
                SHCreateStreamOnFileEx(path, 0x40, 0, false, 0, out stream) != 0 ||
                ((IInitializeWithStream)provider).Initialize(stream, 0) != 0 ||
                ((IThumbnailProvider)provider).GetThumbnail(ThumbnailEdge, out bitmap, out _) != 0 ||
                bitmap == 0)
                return null;

            var image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        finally
        {
            if (bitmap != 0)
                DeleteObject(bitmap);
            if (provider is not null)
                Marshal.ReleaseComObject(provider);
            if (stream is not null)
                Marshal.ReleaseComObject(stream);
            if (factory is not null)
                Marshal.ReleaseComObject(factory);
            NativeLibrary.Free(module);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int GetClassObject(in Guid clsid, in Guid iid, [MarshalAs(UnmanagedType.Interface)] out IClassFactory factory);

    [ComImport, Guid("00000001-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IClassFactory
    {
        [PreserveSig]
        int CreateInstance(nint outer, in Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, Guid("B824B49D-22AC-4161-AC8A-9916E8FA3F7F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithStream
    {
        [PreserveSig]
        int Initialize(IStream stream, uint mode);
    }

    [ComImport, Guid("E357FCCD-A995-4576-B01F-234630154E96"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IThumbnailProvider
    {
        [PreserveSig]
        int GetThumbnail(uint edge, out nint bitmap, out uint alpha);
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateStreamOnFileEx(string path, uint mode, uint attributes,
        [MarshalAs(UnmanagedType.Bool)] bool create, nint template, out IStream stream);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern bool DeleteObject(nint handle);
}
