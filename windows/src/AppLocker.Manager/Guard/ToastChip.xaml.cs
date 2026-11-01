using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AppLocker.Manager.Guard;

/// <summary>
/// Lightweight corner toast chip — auto-closes after a delay.
/// Shows in the bottom-right corner of the Locker app window (or screen if no window).
/// Does NOT steal focus.
/// </summary>
public partial class ToastChip : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;

    private Window? _owner;

    public ToastChip(Window? owner = null)
    {
        _owner = owner;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // No focus steal
        var h = new WindowInteropHelper(this).Handle;
        if (h != IntPtr.Zero)
        {
            SetWindowLong(h, GWL_EXSTYLE,
                GetWindowLong(h, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
            SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        UpdateLayout();
        PositionNearOwner();

        // Fade in
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
    }

    private void PositionNearOwner()
    {
        const double margin = 12;
        Window? w = _owner ?? Application.Current?.MainWindow;

        if (w != null && w.IsVisible && w.WindowState != WindowState.Minimized)
        {
            // Bottom-right corner of the owner window, just inside the border
            Left = w.Left + w.ActualWidth - ActualWidth - margin;
            Top  = w.Top  + w.ActualHeight - ActualHeight - margin;
        }
        else
        {
            // Fallback: bottom-right of work area (above taskbar)
            var work = SystemParameters.WorkArea;
            Left = work.Right - ActualWidth - margin;
            Top  = work.Bottom - ActualHeight - margin;
        }
    }

    // ---- Static helper: show and auto-dismiss ----

    /// <summary>Show a toast near the given owner window (or MainWindow if null).</summary>
    public static void Show(string glyph, string title, string sub = "",
                            Color? accentColor = null, int autoCloseMs = 3000,
                            Window? owner = null)
    {
        var toast = new ToastChip(owner);
        toast.GlyphIcon.Text = glyph;
        toast.TitleText.Text = title;
        toast.SubText.Text = sub;
        toast.SubText.Visibility = string.IsNullOrEmpty(sub) ? Visibility.Collapsed : Visibility.Visible;

        if (accentColor.HasValue)
            toast.GlyphIcon.Foreground = new SolidColorBrush(accentColor.Value);

        toast.Show();

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(autoCloseMs)
        };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            try { await toast.FadeAndClose(); } catch { }
        };
        timer.Start();
    }

    private async Task FadeAndClose()
    {
        var tcs = new TaskCompletionSource();
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) => { try { Close(); } catch { } tcs.TrySetResult(); };
        BeginAnimation(OpacityProperty, fade);
        await tcs.Task;
    }
}
