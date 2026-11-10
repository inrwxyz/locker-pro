using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using AppLocker.Core;

namespace AppLocker.Manager.Guard;

public partial class LockChipWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;

    public event Action? LockClicked;
    public string FolderPath { get; }
    private const double ChipGap = 8;
    private int _slotIndex;
    private bool _isClosing;

    public LockChipWindow(string folderPath = "", int slotIndex = 0)
    {
        FolderPath = folderPath;
        _slotIndex = Math.Max(0, slotIndex);
        InitializeComponent();
        Loaded += LockChipWindow_Loaded;
    }

    private void LockChipWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // Don't steal focus from user applications
        var h = new WindowInteropHelper(this).Handle;
        if (h != IntPtr.Zero)
        {
            SetWindowLong(h, GWL_EXSTYLE,
                GetWindowLong(h, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
            SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        // Position at top right corner in stacked slot
        var work = SystemParameters.WorkArea;
        Left = work.Right - Width - 20;
        Top = ComputeSlotTop(_slotIndex, work);

        // Fade in
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
    }

    private double ComputeSlotTop(int slotIndex, Rect work)
    {
        double raw = work.Top + 20 + Math.Max(0, slotIndex) * (Height + ChipGap);
        double maxTop = Math.Max(work.Top + 8, work.Bottom - Height - 8);
        return Math.Min(raw, maxTop);
    }

    public void SetSlot(int slotIndex, bool animate = true)
    {
        _slotIndex = Math.Max(0, slotIndex);
        if (!IsLoaded || _isClosing) return;
        double targetTop = ComputeSlotTop(_slotIndex, SystemParameters.WorkArea);
        if (Math.Abs(Top - targetTop) < 0.5) return;

        if (animate)
        {
            var anim = new DoubleAnimation(Top, targetTop, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            BeginAnimation(TopProperty, anim);
        }
        else
        {
            BeginAnimation(TopProperty, null);
            Top = targetTop;
        }
    }

    public void SetFolder(string name)
    {
        ChipLabel.Text = name;
        ChipLabel.ToolTip = name + " is unlocked";
    }

    public void ShowBusy(string text)
    {
        LockBtn.IsEnabled = false;
        LockBtn.Content = "Locking...";
        ChipLabel.Text = text;
    }

    public void ShowProgress(FolderProgress p)
    {
        int pct = (int)Math.Round(p.Percent);
        LockBtn.IsEnabled = false;
        LockBtn.Content = p.TotalFiles > 0 ? $"{pct}%" : "Locking...";
        ChipLabel.Text = "Locking...";
        ChipLabel.ToolTip = p.TotalFiles > 0
            ? $"{p.FilesDone}/{p.TotalFiles} ({FolderProgress.FormatSize(p.BytesDone)}/{FolderProgress.FormatSize(p.TotalBytes)}) • {p.CurrentFile}"
            : "Locking...";
    }

    public void ResetReady(string name)
    {
        LockBtn.IsEnabled = true;
        LockBtn.Content = "Lock now";
        SetFolder(name);
    }

    public async void ShowLockedAndClose()
    {
        if (_isClosing) return;
        _isClosing = true;

        ChipIcon.Text = "\uE72E"; // Lock glyph
        ChipLabel.Text = "Locked";
        LockBtn.Visibility = Visibility.Collapsed;

        await Task.Delay(750);
        await FadeOutAndClose();
    }

    public async Task FadeOutAndClose()
    {
        if (_isClosing && Opacity == 0) return;
        _isClosing = true;

        var tcs = new TaskCompletionSource();
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(200));
        fade.Completed += (_, _) =>
        {
            try { Close(); } catch { }
            tcs.TrySetResult();
        };
        BeginAnimation(OpacityProperty, fade);
        await tcs.Task;
    }

    private void LockBtn_Click(object sender, RoutedEventArgs e)
    {
        LockBtn.IsEnabled = false;
        LockClicked?.Invoke();
    }
}
