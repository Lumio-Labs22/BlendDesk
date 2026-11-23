using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace BlenderLibrary;

internal static class BackgroundImage
{
    private const int MaxEdge = 2560;

    public static BitmapSource Load(string path)
    {
        using var stream = File.OpenRead(path);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand).Frames[0];
        int width = frame.PixelWidth;
        int height = frame.PixelHeight;
        stream.Position = 0;

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        if (width >= height && width > MaxEdge)
            bitmap.DecodePixelWidth = MaxEdge;
        else if (height > MaxEdge)
            bitmap.DecodePixelHeight = MaxEdge;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    public static bool IsLoadError(Exception error) =>
        error is IOException or UnauthorizedAccessException or FormatException or ArgumentException or
            NotSupportedException or InvalidOperationException or COMException;
}
