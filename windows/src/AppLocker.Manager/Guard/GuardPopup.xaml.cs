using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AppLocker.Core;

namespace AppLocker.Manager.Guard;

public partial class GuardPopup : Window
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
    private bool _closing;
    private Action<string, string>? _biometricHandler;

    public string AuthType { get; set; } = "password";
    public bool IsLockOperation { get; set; } = false;

    public GuardPopup(string target, bool isLock = false)
    {
        IsLockOperation = isLock;
        InitializeComponent();
        ItemName.Text = System.IO.Path.GetFileName((target ?? "").TrimEnd('\\'));
        if (isLock)
        {
            UnlockBtn.Content = "Lock";
            Status.Text = "Enter password to lock";
        }

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
            if (_biometricHandler != null)
            {
                PhoneCompanionService.OnBiometricApproved -= _biometricHandler;
                _biometricHandler = null;
            }
            if (_ask != null && !_ask.Task.IsCompleted)
            {
                InexIslandBridge.NotifyCancel(ItemName.Text);
            }
            _ask?.TrySetResult(null);
        };
        _timer.Tick += (_, _) => UpdateLockout();
    }

    public Task<byte[]?> AskAsync()
    {
        _ask = new TaskCompletionSource<byte[]?>();
        _formMode = true;
        _timer.Start();
        Show();
        Activate();
        InexIslandBridge.NotifyPrompt(ItemName.Text);


        _ = TryPhoneBiometricAuthAsync(ItemName.Text, IsLockOperation);
        return _ask.Task;
    }

    // Fingerprint path: the phone must open RSA(U) with its fingerprint-bound key; only then the PC can open the
    // master key. No fingerprint key yet (or phone unreachable) = password, and the key is linked right after.
    private async Task TryPhoneBiometricAuthAsync(string target, bool isLock)
    {
        try
        {
            var settings = LockerSettings.Load();
            if (!settings.PhoneBiometricsEnabled || settings.PairedPhone == null) return;
            if (!MasterConfig.HasBiometricVaultV2()) return;
            string phoneIp = settings.PairedPhone.LastIpAddress;
            if (string.IsNullOrEmpty(phoneIp) || phoneIp == "127.0.0.1") return;

            Dispatcher.Invoke(() =>
            {
                Status.Text = "Checking phone biometrics...";
            });

            var res = await PhoneCompanionService.RequestVaultKeyAsync(target, isLock, 15000);
            if (res.Key != null)
            {
                Dispatcher.Invoke(() =>
                {
                    if (_ask != null && !_ask.Task.IsCompleted)
                    {
                        AuthType = !string.IsNullOrEmpty(res.AuthType) ? res.AuthType : "fingerprint";
                        LockoutTracker.RecordSuccess();
                        UnlockBtn.IsEnabled = false;
                        _timer.Stop();
                        _ask.TrySetResult(res.Key);
                    }
                    else
                    {
                        Array.Clear(res.Key);
                    }
                });
            }
            else if (res.Reason is "key_invalid" or "no_key" or "vault_mismatch")
            {
                Dispatcher.Invoke(() =>
                {
                    Status.Text = "Phone fingerprints changed. Enter Master Password to re-link";
                    PwBox.Focus();
                });
            }
        }
        catch { }
    }

    public void SetPrompt(string button, string status, bool isLock = false)
    {
        IsLockOperation = isLock;
        UnlockBtn.Content = button;
        Status.Text = status;
    }

    public void ShowBusy(string text)
    {
        _formMode = false;
        _timer.Stop();
        FormPanel.Visibility = Visibility.Collapsed;
        GlyphIcon.Visibility = Visibility.Collapsed;
        DotsPanel.Visibility = Visibility.Visible;
        OpProgressBar.Value = 0;
        ProgressDetail.Text = "";
        ProgressPanel.Visibility = Visibility.Collapsed;
        StartDots();
        Status.Text = text;
    }

    public void UpdateProgress(string verb, FolderProgress p)
    {
        ProgressPanel.Visibility = Visibility.Visible;
        OpProgressBar.Value = p.Percent;
        int pct = (int)Math.Round(p.Percent);
        Status.Text = p.TotalFiles > 0 ? $"{verb}... {pct}%" : $"{verb}...";
        string sizeInfo = p.TotalBytes > 0
            ? $" • {FolderProgress.FormatSize(p.BytesDone)}/{FolderProgress.FormatSize(p.TotalBytes)}"
            : "";
        string filePart = string.IsNullOrEmpty(p.CurrentFile) ? "" : $" • {p.CurrentFile}";
        ProgressDetail.Text = p.TotalFiles > 0 ? $"{p.FilesDone}/{p.TotalFiles}{sizeInfo}{filePart}" : "";
    }

    public void SetStatus(string glyph, string title, string sub)
    {
        _formMode = false;
        _timer.Stop();
        StopDots();
        FormPanel.Visibility = Visibility.Collapsed;
        DotsPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Collapsed;
        GlyphIcon.Visibility = Visibility.Visible;
        GlyphIcon.Text = glyph;

        ItemName.Text = title;
        Status.Text = sub;
        ItemName.Visibility = Visibility.Visible;
        Status.Visibility = Visibility.Visible;
        PopIcon();
    }

    public async Task ShowResult(string glyph, string title, string sub, int ms = 900)
    {
        SetStatus(glyph, title, sub);
        await Task.Delay(ms);
        await CloseAsync();
    }

    public void ShowNoticeMode(string glyph, string title, string sub) => SetStatus(glyph, title, sub);

    public async Task CloseAsync()
    {
        if (_closing) return;
        _closing = true;
        _timer.Stop();
        if (_biometricHandler != null)
        {
            PhoneCompanionService.OnBiometricApproved -= _biometricHandler;
            _biometricHandler = null;
        }

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

    private void UpdateLockout()
    {
        if (!_formMode) return;
        int left = LockoutTracker.SecondsRemaining();
        bool locked = left > 0;
        UnlockBtn.IsEnabled = !locked;
        PwBox.IsEnabled = PwText.IsEnabled = !locked;
        if (locked) Status.Text = $"Incorrect password. Try again in {left}s";
        else if (Status.Text.StartsWith("Incorrect password.")) Status.Text = IsLockOperation ? "Enter password to lock" : "Enter password";
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
            Status.Text = "Incorrect password";
            Shake();
            PwBox.Clear(); PwText.Clear();
            UpdateLockout();
            InexIslandBridge.NotifyWrong(ItemName.Text);
            return;
        }

        // password was right: if the phone fingerprint is not linked yet (or fingers changed) link it now
        PhoneCompanionService.TryAutoEnroll(key);

        LockoutTracker.RecordSuccess();
        UnlockBtn.IsEnabled = false;
        _timer.Stop();
        _ask?.TrySetResult(key);
    }

    private async void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        InexIslandBridge.NotifyCancel(ItemName.Text);
        _ask?.TrySetResult(null);
        await CloseAsync();
    }
}
