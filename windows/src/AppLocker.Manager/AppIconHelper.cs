using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AppLocker.Manager;

public static class AppIconHelper
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static readonly Dictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? GetIcon(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        lock (_cache)
        {
            if (_cache.TryGetValue(path, out var cached)) return cached;
        }

        ImageSource? src = null;
        try
        {
            if (File.Exists(path))
            {
                using var sysIcon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (sysIcon != null)
                {
                    var bs = Imaging.CreateBitmapSourceFromHIcon(
                        sysIcon.Handle,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    bs.Freeze();
                    src = bs;
                }
            }
        }
        catch { }

        lock (_cache)
        {
            _cache[path] = src;
        }
        return src;
    }
}
