using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using AppLocker.Core;

namespace AppLocker.Manager;

public class PasswordPromptDialog : Window
{
	private readonly PasswordBox _pw;

	private readonly TextBlock _err;

	private readonly TranslateTransform _shake;

	private readonly bool _isLock;

	private readonly string _target;
	private Button? _confirm;
	private System.Windows.Threading.DispatcherTimer? _cd;

	public byte[]? MasterKey { get; private set; }

	public string Password => _pw.Password;

	public PasswordPromptDialog(string prompt, bool isLock = false, string target = "")
	{
		_isLock = isLock;
		_target = target;
		Title = isLock ? "Lock" : "Locker";
		Width = 360.0;
		SizeToContent = SizeToContent.Height;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		WindowStyle = WindowStyle.None;
		AllowsTransparency = true;
		Background = Brushes.Transparent;
		_shake = new TranslateTransform();
		Grid grid = new Grid
		{
			Margin = new Thickness(14.0)
		};
		Border border = new Border
		{
			Background = new SolidColorBrush(Color.FromRgb(16, 15, 12)),
			BorderBrush = new SolidColorBrush(Color.FromRgb(37, 36, 33)),
			BorderThickness = new Thickness(1.0),
			CornerRadius = new CornerRadius(14.0),
			Padding = new Thickness(22.0),
			ClipToBounds = true,
			RenderTransform = _shake,
			Effect = new DropShadowEffect
			{
				BlurRadius = 16.0,
				ShadowDepth = 3.0,
				Opacity = 0.55,
				Color = Colors.Black,
				Direction = 270.0
			}
		};
		StackPanel stackPanel = new StackPanel
		{
			Children = { (UIElement)new TextBlock
			{
				Text = prompt,
				Foreground = new SolidColorBrush(Color.FromRgb(247, 246, 243)),
				FontSize = 13.5,
				FontWeight = FontWeights.SemiBold,
				Margin = new Thickness(0.0, 0.0, 0.0, 12.0)
			} }
		};
		_pw = new PasswordBox
		{
			Height = 36.0,
			Background = new SolidColorBrush(Color.FromRgb(22, 21, 18)),
			Foreground = new SolidColorBrush(Color.FromRgb(247, 246, 243)),
			CaretBrush = Brushes.White,
			BorderBrush = new SolidColorBrush(Color.FromRgb(48, 47, 44)),
			BorderThickness = new Thickness(1.0),
			Padding = new Thickness(10.0, 6.0, 10.0, 6.0),
			FontSize = 13.0,
			Margin = new Thickness(0.0, 0.0, 0.0, 6.0)
		};
		stackPanel.Children.Add(_pw);
		_err = new TextBlock
		{
			Text = "Incorrect Master Password!",
			Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68)),
			FontSize = 11.5,
			Margin = new Thickness(0.0, 0.0, 0.0, 10.0),
			Visibility = Visibility.Collapsed
		};
		stackPanel.Children.Add(_err);
		Grid grid2 = new Grid
		{
			Margin = new Thickness(0.0, 4.0, 0.0, 0.0)
		};
		grid2.ColumnDefinitions.Add(new ColumnDefinition());
		grid2.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(10.0)
		});
		grid2.ColumnDefinitions.Add(new ColumnDefinition());
		Button button = new Button
		{
			Content = "Cancel",
			Height = 34.0,
			Cursor = Cursors.Hand,
			Background = new SolidColorBrush(Color.FromRgb(28, 27, 24)),
			Foreground = new SolidColorBrush(Color.FromRgb(150, 149, 146)),
			BorderBrush = new SolidColorBrush(Color.FromRgb(41, 40, 37)),
			BorderThickness = new Thickness(1.0),
			FontWeight = FontWeights.SemiBold,
			IsCancel = true
		};
		string notifyTarget = !string.IsNullOrEmpty(_target) ? _target : (_isLock ? "Locking" : "Master Password");
		Loaded += (object _, RoutedEventArgs _) =>
		{
			InexIslandBridge.NotifyPrompt(notifyTarget);
		};
		Closed += (object? _, EventArgs _) =>
		{
			if (MasterKey == null)
			{
				InexIslandBridge.NotifyCancel(notifyTarget);
			}
		};
		button.Click += (object _, RoutedEventArgs _) =>
		{
			InexIslandBridge.NotifyCancel(notifyTarget);
			DialogResult = false;
			Close();
		};
		Grid.SetColumn(button, 0);
		Button button2 = new Button
		{
			Content = _isLock ? "Lock" : "Confirm",
			Height = 34.0,
			Cursor = Cursors.Hand,
			Background = Brushes.White,
			Foreground = new SolidColorBrush(Color.FromRgb(12, 11, 8)),
			BorderThickness = new Thickness(0.0),
			FontWeight = FontWeights.Bold,
			IsDefault = true
		};
		_confirm = button2;
		Closed += (object? _, EventArgs _) => _cd?.Stop();
		button2.Click += (object _, RoutedEventArgs _) =>
		{
			byte[]? keyWithPassword = AuthGate.TryPassword(_pw.Password, out int waitSec);
			if (keyWithPassword == null)
			{
				InexIslandBridge.NotifyWrong(notifyTarget);
				if (waitSec > 0)
				{
					ApplyLockoutState();
				}
				else
				{
					_err.Text = "Incorrect Master Password!";
				}
				_err.Visibility = Visibility.Visible;
				_pw.BorderBrush = new SolidColorBrush(Color.FromRgb(239, 68, 68));
				DoubleAnimationUsingKeyFrames animation = new DoubleAnimationUsingKeyFrames
				{
					KeyFrames = 
					{
						(DoubleKeyFrame)new LinearDoubleKeyFrame(-6.0, TimeSpan.FromMilliseconds(40L)),
						(DoubleKeyFrame)new LinearDoubleKeyFrame(6.0, TimeSpan.FromMilliseconds(90L)),
						(DoubleKeyFrame)new LinearDoubleKeyFrame(-4.0, TimeSpan.FromMilliseconds(140L)),
						(DoubleKeyFrame)new LinearDoubleKeyFrame(4.0, TimeSpan.FromMilliseconds(190L)),
						(DoubleKeyFrame)new LinearDoubleKeyFrame(0.0, TimeSpan.FromMilliseconds(240L))
					}
				};
				_shake.BeginAnimation(TranslateTransform.XProperty, animation);
				_pw.SelectAll();
				_pw.Focus();
			}
			else
			{
				if (_isLock)
				{
					InexIslandBridge.NotifyCancel(notifyTarget); // only closes the "Locking" prompt; the real "Locked" toast comes from the caller AFTER the lock finished
				}
				else
				{
					InexIslandBridge.NotifyUnlock("password", notifyTarget);
				}
				MasterKey = keyWithPassword;
				DialogResult = true;
				Close();
			}
		};
		Grid.SetColumn(button2, 2);
		grid2.Children.Add(button);
		grid2.Children.Add(button2);
		stackPanel.Children.Add(grid2);
		border.Child = stackPanel;
		grid.Children.Add(border);
		Content = grid;
		Loaded += (object _, RoutedEventArgs _) =>
		{
			_pw.Focus();
			if (LockoutTracker.SecondsRemaining() > 0)
			{
				ApplyLockoutState();
			}
		};
	}

	// Lockout cholle input bondho + countdown dekhay. Shesh hole abar chalu.
	private void ApplyLockoutState()
	{
		int left = LockoutTracker.SecondsRemaining();
		if (left > 0)
		{
			_err.Text = "Too many incorrect attempts. Try again in " + LockoutTracker.Format(left) + ".";
			_err.Visibility = Visibility.Visible;
			_pw.IsEnabled = false;
			if (_confirm != null)
			{
				_confirm.IsEnabled = false;
			}
			if (_cd == null)
			{
				_cd = new System.Windows.Threading.DispatcherTimer
				{
					Interval = TimeSpan.FromSeconds(1.0)
				};
				_cd.Tick += (object? _, EventArgs _) => ApplyLockoutState();
			}
			_cd.Start();
		}
		else
		{
			_cd?.Stop();
			_pw.IsEnabled = true;
			if (_confirm != null)
			{
				_confirm.IsEnabled = true;
			}
			_err.Visibility = Visibility.Collapsed;
			_pw.Clear();
			_pw.Focus();
		}
	}
}
