using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AppLocker.Core;

namespace AppLocker.UI;

// Ekta-i window: password form -> Verifying (dots) -> tick/error -> fade.
// Folder unlock hole choto "chip" hoye thake (Lock now button shoho).
public partial class MainWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int value);

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private TaskCompletionSource<byte[]?>? _ask;
    private bool _showPw;
    private bool _formMode;
    private bool _chipMode;
    private bool _closing;

    // Chip er "Lock now" button chapa hole
    public event Action? LockNowClicked;

    public MainWindow(string target)
    {
        InitializeComponent();
        ItemName.Text = System.IO.Path.GetFileName((target ?? "").TrimEnd('\\'));

        Loaded += (_, _) =>
        {
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)));
            if (_formMode)
            {
                PwBox.Focus();
                UpdateLockout();
            }
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            if (LockerSettings.Current.InexIslandAnimation && _ask != null && !_ask.Task.IsCompleted)
            {
                InexIslandBridge.NotifyCancel(ItemName.Text);
            }
            _ask?.TrySetResult(null);
        };
        _timer.Tick += (_, _) => UpdateLockout();
    }

    // ---------- Public API (GuardHost eta chalay) ----------

    // Password chay. Sothik hole key return kore (window khola thake), cancel hole null (window nijei bondho).
    public Task<byte[]?> AskAsync()
    {
        _ask = new TaskCompletionSource<byte[]?>();
        _formMode = true;
        _timer.Start();
        Show();
        Activate();
        if (LockerSettings.Current.InexIslandAnimation)
        {
            InexIslandBridge.NotifyPrompt(ItemName.Text);
        }
        return _ask.Task;
    }

    // Button/status text bodlano (lock hole "Lock", unlock hole "Unlock")
    public void SetPrompt(string button, string status)
    {
        UnlockBtn.Content = button;
        Status.Text = status;
    }
    // Verifying / Locking... : dots animation
    public void ShowBusy(string text)
    {
        _formMode = false;
        _timer.Stop();
        FormPanel.Visibility = Visibility.Collapsed;
        GlyphIcon.Visibility = Visibility.Collapsed;
        DotsPanel.Visibility = Visibility.Visible;
        LockNowBtn.Visibility = Visibility.Collapsed;
        StartDots();

        if (_chipMode) Card.ToolTip = text;
        else Status.Text = text;
    }

    // Icon + message dekhay (tick / error / locked)
    public void SetStatus(string glyph, string title, string sub)
    {
        _formMode = false;
        _timer.Stop();
        StopDots();
        FormPanel.Visibility = Visibility.Collapsed;
        DotsPanel.Visibility = Visibility.Collapsed;
        GlyphIcon.Visibility = Visibility.Visible;
        GlyphIcon.Text = glyph;

        if (_chipMode)
        {
            LockNowBtn.Visibility = Visibility.Collapsed;
            Card.ToolTip = string.IsNullOrEmpty(title) ? sub : title + " - " + sub;
        }
        else
        {
            ItemName.Text = title;
            Status.Text = sub;
            ItemName.Visibility = Visibility.Visible;
            Status.Visibility = Visibility.Visible;
        }
        PopIcon();
    }

    // Message dekhiye fade hoye bondho
    public async Task ShowResult(string glyph, string title, string sub, int ms = 1100)
    {
        SetStatus(glyph, title, sub);
        await Task.Delay(ms);
        await CloseAsync();
    }

    // Shudhu notice (form chhara), window.Show() tumi korbe
    public void ShowNoticeMode(string glyph, string title, string sub) => SetStatus(glyph, title, sub);

    // Folder unlock: choto chip, top-center e, Lock now button shoho
    public void ShowChip(string label)
    {
        EnterChipMode();
        StopDots();
        DotsPanel.Visibility = Visibility.Collapsed;
        GlyphIcon.Visibility = Visibility.Visible;
        GlyphIcon.Text = "\uE785";
        LockNowBtn.IsEnabled = true;
        LockNowBtn.Visibility = Visibility.Visible;
        Card.ToolTip = label + " - Unlocked";
        PopIcon();
    }

    public async Task CloseAsync()
    {
        if (_closing) return;
        _closing = true;
        _timer.Stop();

        var tcs = new TaskCompletionSource();
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(220));
        fade.Completed += (_, _) =>
        {
            try { Close(); } catch { }
            tcs.TrySetResult();
        };
        BeginAnimation(OpacityProperty, fade);
        await tcs.Task;
    }


    public void SetChipVisible(bool visible)
    {
        if (!_chipMode || _closing) return;
        if (visible)
        {
            if (Visibility != Visibility.Visible || Opacity < 0.95)
            {
                Visibility = Visibility.Visible;
                BeginAnimation(OpacityProperty, new DoubleAnimation(Opacity, 1, TimeSpan.FromMilliseconds(200)));
            }
        }
        else
        {
            if (Visibility == Visibility.Visible && Opacity > 0)
            {
                var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(200));
                fade.Completed += (_, _) =>
                {
                    if (Opacity <= 0.05) Visibility = Visibility.Collapsed;
                };
                BeginAnimation(OpacityProperty, fade);
            }
        }
    }

    // ---------- chip mode ----------

    private void EnterChipMode()
    {
        if (_chipMode) return;
        _chipMode = true;
        _formMode = false;
        _timer.Stop();

        FormPanel.Visibility = Visibility.Collapsed;
        ItemName.Visibility = Visibility.Collapsed;
        Status.Visibility = Visibility.Collapsed;
        IconGrid.Width = 24;
        IconGrid.Height = 24;
        GlyphIcon.FontSize = 16;
        Body.Margin = new Thickness(14, 8, 14, 8); Card.Background = new SolidColorBrush(Color.FromArgb(0x59, 0, 0, 0));

        // Focus churi korbe na (button click tobuo kaj kore)
        var h = new WindowInteropHelper(this).Handle;
        if (h != IntPtr.Zero)
            SetWindowLong(h, GWL_EXSTYLE,
                GetWindowLong(h, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

        double w = 116;
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 };
        var ms = TimeSpan.FromMilliseconds(320);
        BeginAnimation(WidthProperty, new DoubleAnimation(ActualWidth, w, ms) { EasingFunction = ease });
        BeginAnimation(LeftProperty,
            new DoubleAnimation(Left, SystemParameters.WorkArea.Right - w + 12, ms) { EasingFunction = ease });
        BeginAnimation(TopProperty, new DoubleAnimation(Top, -12, ms) { EasingFunction = ease });
    }

    private void LockNowBtn_Click(object sender, RoutedEventArgs e)
    {
        LockNowBtn.IsEnabled = false; // double click thekano
        LockNowClicked?.Invoke();
    }

    // ---------- animations ----------

    private void StartDots()
    {
        var els = new[] { D1, D2, D3 };
        for (int i = 0; i < els.Length; i++)
        {
            els[i].BeginAnimation(OpacityProperty,
                new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(350))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    BeginTime = TimeSpan.FromMilliseconds(i * 150)
                });
        }
    }

    private void StopDots()
    {
        foreach (var d in new[] { D1, D2, D3 })
        {
            d.BeginAnimation(OpacityProperty, null);
            d.Opacity = 1;
        }
    }

    private void PopIcon()
    {
        var anim = new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(380))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.8 }
        };
        IconScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        IconScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private void Shake()
    {
        var anim = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(400) };
        double[] xs = { 0, -12, 12, -9, 9, -5, 5, 0 };
        for (int i = 0; i < xs.Length; i++)
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(xs[i],
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(i * 50))));
        ShakeTransform.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    // ---------- password form ----------

    private void UpdateLockout()
    {
        if (!_formMode) return;
        int left = LockoutTracker.SecondsRemaining();
        bool locked = left > 0;
        UnlockBtn.IsEnabled = !locked;
        PwBox.IsEnabled = PwText.IsEnabled = !locked;
        if (locked) Status.Text = $"Bhul password. {left}s por abar try koro";
        else if (Status.Text.StartsWith("Bhul password.")) Status.Text = "Password dao";
    }

    private string CurrentPw => _showPw ? PwText.Text : PwBox.Password;

    private void EyeBtn_Click(object sender, RoutedEventArgs e)
    {
        _showPw = !_showPw;
        if (_showPw)
        {
            PwText.Text = PwBox.Password;
            PwText.Visibility = Visibility.Visible;
            PwBox.Visibility = Visibility.Collapsed;
            PwText.Focus();
        }
        else
        {
            PwBox.Password = PwText.Text;
            PwBox.Visibility = Visibility.Visible;
            PwText.Visibility = Visibility.Collapsed;
            PwBox.Focus();
        }
    }

    private void UnlockBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!_formMode || LockoutTracker.SecondsRemaining() > 0) return;

        byte[]? key = MasterConfig.GetKeyWithPassword(CurrentPw);
        if (key == null)
        {
            LockoutTracker.RecordFail();
            Status.Text = "Bhul password";
            Shake();
            PwBox.Clear(); PwText.Clear();
            UpdateLockout();
            if (LockerSettings.Current.InexIslandAnimation)
            {
                InexIslandBridge.NotifyWrong(ItemName.Text);
            }
            return;
        }

        LockoutTracker.RecordSuccess();
        UnlockBtn.IsEnabled = false;
        _timer.Stop();
        _ask?.TrySetResult(key); // window khola thake, GuardHost state bodlay
    }

    private async void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        if (LockerSettings.Current.InexIslandAnimation)
        {
            InexIslandBridge.NotifyCancel(ItemName.Text);
        }
        _ask?.TrySetResult(null);
        await CloseAsync();
    }
}
