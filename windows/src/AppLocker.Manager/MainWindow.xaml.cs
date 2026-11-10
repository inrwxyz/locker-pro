using System.Text;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using AppLocker.Manager.Guard;
using AppLocker.Core;
using Microsoft.Win32;

namespace AppLocker.Manager;

public partial class MainWindow : Window, IComponentConnector, IStyleConnector
{
	private List<AppItem>? _allApps;

	private byte[]? _masterKey;

	private bool _loadingSettings;




	private bool _isVaultAuthenticated = false;
	private bool _suppressNav = false;
	private bool _folderOpBusy = false;
	private string _currentUnlockVaultPath = "";
	private readonly DispatcherTimer _phoneSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
	private string _lastPhoneSnapshot = "";

	public MainWindow()
	{
		_loadingSettings = true;
		InitializeComponent();
		_loadingSettings = false;
		Loaded += MainWindow_Loaded;
		_phoneSyncTimer.Tick += PhoneSyncTimer_Tick;
		PhoneCompanionService.OnPhoneUnpaired += HandleRemotePhoneUnpaired;
		Closed += (_, _) =>
		{
			_phoneSyncTimer.Stop();
			PhoneCompanionService.OnPhoneUnpaired -= HandleRemotePhoneUnpaired;
		};
	}

	private static string GetPhoneStateSnapshot(LockerSettingsData data)
	{
		var p = data.PairedPhone;
		if (p == null) return "none";
		return $"{p.DeviceName}|{p.LastIpAddress}|{p.PairedAt.Ticks}|{MasterConfig.HasBiometricVaultV2()}";
	}

	private void PhoneSyncTimer_Tick(object? sender, EventArgs e)
	{
		try
		{
			LockerSettingsData current = LockerSettings.Current;
			string snapshot = GetPhoneStateSnapshot(current);
			if (snapshot == _lastPhoneSnapshot) return;

			bool wasPaired = _lastPhoneSnapshot != "" && _lastPhoneSnapshot != "none";
			LoadPhoneTab();
			if (wasPaired && current.PairedPhone == null)
			{
				Guard.ToastChip.Show("\uE72F", "Phone Disconnected", "Unpaired from phone", System.Windows.Media.Color.FromRgb(150, 144, 134), 2800, this);
			}
		}
		catch { }
	}

	private void HandleRemotePhoneUnpaired()
	{
		Dispatcher.InvokeAsync(() =>
		{
			LoadPhoneTab();
			Guard.ToastChip.Show("\uE72F", "Phone Disconnected", "Unpaired from phone", System.Windows.Media.Color.FromRgb(150, 144, 134), 2800, this);
		});
	}

	private void MainWindow_Loaded(object sender, RoutedEventArgs e)
	{
		if (!MasterConfig.IsSetup())
		{
			SetupOverlay.Visibility = Visibility.Visible;
			SetupInputPanel.Visibility = Visibility.Visible;
			SetupSuccessPanel.Visibility = Visibility.Collapsed;
			SetupPw1.Focus();
		}
		else
		{
			RefreshAll();
		}
		_phoneSyncTimer.Start();
	}

	private void RefreshAll()
	{
		LoadList();
		LoadPhoneTab();
		LoadAboutTab();
	}

	private void Nav_Checked(object sender, RoutedEventArgs e)
	{
		if (_suppressNav || TabLockedPanel == null) return;

		TabLockedPanel.Visibility = Visibility.Collapsed;
		TabSecurityPanel.Visibility = Visibility.Collapsed;
		TabPhonePanel.Visibility = Visibility.Collapsed;
		TabAboutPanel.Visibility = Visibility.Collapsed;
		if (TabVaultPanel != null) TabVaultPanel.Visibility = Visibility.Collapsed;

		if (NavLocked.IsChecked == true)
		{
			_isVaultAuthenticated = false; // Require Master Password when re-entering Vault!
			TabLockedPanel.Visibility = Visibility.Visible;
			HeaderTitle.Text = "LOCKED ITEMS";
			LoadList();
		}
		else if (NavSecurity.IsChecked == true)
		{
			_isVaultAuthenticated = false; // Require Master Password when re-entering Vault!
			TabSecurityPanel.Visibility = Visibility.Visible;
			HeaderTitle.Text = "SECURITY";
		}
		else if (NavPhone.IsChecked == true)
		{
			_isVaultAuthenticated = false; // Require Master Password when re-entering Vault!
			TabPhonePanel.Visibility = Visibility.Visible;
			HeaderTitle.Text = "PHONE LINK";
			LoadPhoneTab();
		}
		else if (NavVault != null && NavVault.IsChecked == true)
		{
			if (!_isVaultAuthenticated)
			{
				byte[]? vaultKey = AcquireKey("Enter Master Password to access Vault Locker:");
				if (vaultKey == null)
				{
					_suppressNav = true;
					NavLocked.IsChecked = true;
					_suppressNav = false;
					TabLockedPanel.Visibility = Visibility.Visible;
					HeaderTitle.Text = "LOCKED ITEMS";
					return;
				}
				Array.Clear(vaultKey);
				_isVaultAuthenticated = true;
			}
			TabVaultPanel!.Visibility = Visibility.Visible;
			HeaderTitle.Text = "VAULT LOCKER";
			LoadVaultsList();
		}
		else if (NavAbout.IsChecked == true)
		{
			_isVaultAuthenticated = false; // Require Master Password when re-entering Vault!
			TabAboutPanel.Visibility = Visibility.Visible;
			HeaderTitle.Text = "ABOUT & SYSTEM";
			LoadAboutTab();
		}
	}

	private void Window_MouseDown(object sender, MouseButtonEventArgs e)
	{
		if (e.ChangedButton == MouseButton.Left)
		{
			try
			{
				DragMove();
			}
			catch
			{
			}
		}
	}

	private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
	{
		if (e.ChangedButton == MouseButton.Left)
		{
			try
			{
				DragMove();
			}
			catch
			{
			}
		}
	}

	private void Minimize_Click(object sender, RoutedEventArgs e)
	{
		WindowState = WindowState.Minimized;
	}

	private void Close_Click(object sender, RoutedEventArgs e)
	{
		Close();
	}

	private void LoadList()
	{
		List<DisplayItem> list = new List<DisplayItem>();
		try
		{
			foreach (string item in LockRegistry.List())
			{
				list.Add(new DisplayItem
				{
					Name = Path.GetFileName(item.TrimEnd('\\')),
					Path = item,
					IsFolder = true
				});
			}
		}
		catch
		{
		}
		try
		{
			foreach (string item2 in AppRegistry.List())
			{
				list.Add(new DisplayItem
				{
					Name = Path.GetFileNameWithoutExtension(item2),
					Path = item2,
					IsFolder = false,
					IconSource = AppIconHelper.GetIcon(item2)
				});
			}
		}
		catch
		{
		}
		ItemList.ItemsSource = list;
		ItemCountText.Text = $"{list.Count} items locked";
		EmptyState.Visibility = ((list.Count != 0) ? Visibility.Collapsed : Visibility.Visible);
	}

	private IProgress<FolderProgress> BeginFolderProgress(string title, string folderPath, string verb)
	{
		FolderOpTitle.Text = title;
		FolderOpPathText.Text = folderPath;
		FolderOpProgressBar.Value = 0;
		FolderOpProgressText.Text = $"{verb}...";
		FolderOpBytesText.Text = "";
		FolderOpOverlay.Visibility = Visibility.Visible;

		return new Progress<FolderProgress>(p =>
		{
			FolderOpProgressBar.Value = p.Percent;
			int pct = (int)Math.Round(p.Percent);
			string filePart = string.IsNullOrEmpty(p.CurrentFile) ? "" : $" • {p.CurrentFile}";
			FolderOpProgressText.Text = p.TotalFiles > 0
				? $"{verb} {pct}% ({p.FilesDone}/{p.TotalFiles}){filePart}"
				: $"{verb}...";
			FolderOpBytesText.Text = p.TotalBytes > 0
				? $"{FolderProgress.FormatSize(p.BytesDone)} / {FolderProgress.FormatSize(p.TotalBytes)}"
				: "";
		});
	}

	private void EndFolderProgress()
	{
		FolderOpOverlay.Visibility = Visibility.Collapsed;
	}

	private async void RemoveItem_Click(object sender, RoutedEventArgs e)
	{
		if (_folderOpBusy) return;
		if (!(sender is Button { Tag: var tag }))
		{
			return;
		}
		string? pathOrNull = tag as string;
		if (pathOrNull == null)
		{
			return;
		}
		string path = pathOrNull;
		bool flag = Directory.Exists(path) || path.EndsWith(".aplk", StringComparison.OrdinalIgnoreCase);
		byte[]? keyOrNull = AcquireKey("Enter Master Password to unlock or remove:");
		if (keyOrNull == null)
		{
			return;
		}
		byte[] key = keyOrNull;
		_folderOpBusy = true;
		try
		{
			if (flag)
			{
				var progress = BeginFolderProgress("Unlocking Folder", path, "Decrypting");
				bool ok = await Task.Run(() => FolderLocker.Unlock(path, key, progress));
				if (!ok)
				{
					MessageBox.Show("Unlock failed: Incorrect key or folder is not locked.", "Error", MessageBoxButton.OK, MessageBoxImage.Hand);
					return;
				}
				LockRegistry.Remove(path);
			}
			else
			{
				AppRegistry.Remove(path);
				_ = Task.Run(() => GuardPipe.Send("relock|" + path));
			}
			InexIslandBridge.NotifyRemoved(flag ? Path.GetFileName(path.TrimEnd('\\')) : Path.GetFileNameWithoutExtension(path));
			LoadList();
		}
		catch (Exception ex)
		{
			MessageBox.Show("Unlock failed: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
		finally
		{
			EndFolderProgress();
			_folderOpBusy = false;
			Array.Clear(key);
		}
	}

	private async void AddFolder_Click(object sender, RoutedEventArgs e)
	{
		if (_folderOpBusy) return;
		OpenFolderDialog openFolderDialog = new OpenFolderDialog
		{
			Title = "Select folder to lock"
		};
		if (openFolderDialog.ShowDialog(this) != true)
		{
			return;
		}
		string folder = openFolderDialog.FolderName;
		if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
		{
			return;
		}
		if (FolderLocker.IsLocked(folder))
		{
			Guard.ToastChip.Show("\uE72E", "Already Locked", "Folder is already locked", System.Windows.Media.Color.FromRgb(251, 191, 36), 2500, this);
			return;
		}
		if (!LockSafety.IsSafeFolder(folder, out string why))
		{
			MessageBox.Show(why, "Locker", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		string folderName = Path.GetFileName(folder.TrimEnd('\\'));
		PasswordPromptDialog passwordPromptDialog = new PasswordPromptDialog("Enter Master Password to lock folder:", isLock: true, target: folderName)
		{
			Owner = this
		};
		if (passwordPromptDialog.ShowDialog() != true || passwordPromptDialog.MasterKey == null)
		{
			return;
		}
		byte[] key = passwordPromptDialog.MasterKey;
		_folderOpBusy = true;
		try
		{
			var progress = BeginFolderProgress("Locking Folder", folder, "Encrypting");
			await Task.Run(() =>
			{
				FolderLocker.Lock(folder, key, progress);
			});
			LockRegistry.Add(folder);
			_ = Task.Run(() => GuardPipe.Send("watch|" + folder));
			InexIslandBridge.NotifyLock(folderName);
			LoadList();
		}
		catch (Exception ex)
		{
			MessageBox.Show("Lock failed: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
		finally
		{
			EndFolderProgress();
			_folderOpBusy = false;
			Array.Clear(key);
		}
	}

	private async void AddApp_Click(object sender, RoutedEventArgs e)
	{
		byte[]? appKey = AcquireKey("Enter Master Password to lock app:", isLock: true, target: "App");
		if (appKey != null)
		{
			Array.Clear(appKey);
			PickerOverlay.Visibility = Visibility.Visible;
			PickerSearch.Text = "";
			PickerStatus.Text = "Loading apps...";
			AppList.ItemsSource = null;
			if (_allApps == null)
			{
				_allApps = await Task.Run((Func<List<AppItem>>)LoadAllApps);
			}
			PickerStatus.Text = $"{_allApps.Count} apps found";
			ApplyAppFilter();
		}
	}

	private void ApplyAppFilter()
	{
		if (_allApps != null)
		{
			string q = PickerSearch.Text.Trim();
			AppList.ItemsSource = _allApps.Where((AppItem a) => q.Length == 0 || a.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || a.Path.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
		}
	}

	private void PickerSearch_Changed(object sender, TextChangedEventArgs e)
	{
		ApplyAppFilter();
	}

	private void PickerCancel_Click(object sender, RoutedEventArgs e)
	{
		PickerOverlay.Visibility = Visibility.Collapsed;
	}

	private void PickerLock_Click(object sender, RoutedEventArgs e)
	{
		object selectedItem = AppList.SelectedItem;
		AppItem? a = selectedItem as AppItem;
		if (a == null)
		{
			PickerStatus.Text = "Please select an app first.";
			return;
		}
		PickerOverlay.Visibility = Visibility.Collapsed;
		string appPath = a.Path;
		AppRegistry.Add(appPath);
		AppGuard.KillAllProcesses(appPath);
		Task.Run(() => GuardPipe.Send("relock|" + appPath));
		InexIslandBridge.NotifyLock(a.Name);
		LoadList();
	}

	private void AppList_DoubleClick(object sender, MouseButtonEventArgs e)
	{
		PickerLock_Click(sender, e);
	}

	private void PickerBrowse_Click(object sender, RoutedEventArgs e)
	{
		OpenFileDialog dlg = new OpenFileDialog
		{
			Title = "Select app (.exe) to lock",
			Filter = "Executables (*.exe)|*.exe",
			DereferenceLinks = true
		};
		if (dlg.ShowDialog(this) == true)
		{
			PickerOverlay.Visibility = Visibility.Collapsed;
			AppRegistry.Add(dlg.FileName);
			AppGuard.KillAllProcesses(dlg.FileName);
			Task.Run(() => GuardPipe.Send("relock|" + dlg.FileName));
			InexIslandBridge.NotifyLock(Path.GetFileNameWithoutExtension(dlg.FileName));
			LoadList();
		}
	}

	private static List<AppItem> LoadCleanApps()
	{
		Dictionary<string, AppItem> dictionary = new Dictionary<string, AppItem>(StringComparer.OrdinalIgnoreCase);
		string[] source = new string[16]
		{
			"uninstall", "remove", "reset", "cache", "help", "manual", "documentation", "readme", "license", "settings",
			"profile", "troubleshoot", "updater", "update", "report", "diagnostics"
		};
		string[] array = new string[4]
		{
			Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
			Environment.GetFolderPath(Environment.SpecialFolder.Programs),
			Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
			Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
		};
		EnumerationOptions enumerationOptions = new EnumerationOptions
		{
			RecurseSubdirectories = true,
			IgnoreInaccessible = true
		};
		string[] array2 = array;
		foreach (string text in array2)
		{
			if (string.IsNullOrEmpty(text) || !Directory.Exists(text))
			{
				continue;
			}
			foreach (string item in Directory.EnumerateFiles(text, "*.lnk", enumerationOptions))
			{
				try
				{
					string name = Path.GetFileNameWithoutExtension(item);
					if (source.Any((string w) => name.Contains(w, StringComparison.OrdinalIgnoreCase)))
					{
						continue;
					}
					string text2 = AppResolver.ResolveShortcut(item);
					if (string.IsNullOrEmpty(text2) || !text2.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(text2) || AppRegistry.IsBlocked(text2))
					{
						continue;
					}
					if (dictionary.TryGetValue(text2, out var value))
					{
						if (name.Length < value.Name.Length)
						{
							value.Name = name;
						}
					}
					else
					{
						ImageSource? icon = AppIconHelper.GetIcon(text2);
						dictionary[text2] = new AppItem
						{
							Name = name,
							Path = text2,
							IconSource = icon
						};
					}
				}
				catch
				{
				}
			}
		}
		return dictionary.Values.OrderBy((AppItem a) => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
	}

	private static List<AppItem> LoadAllApps()
	{
		var clean = LoadCleanApps();
		var store = LoadStoreApps();

		var dict = new Dictionary<string, AppItem>(StringComparer.OrdinalIgnoreCase);
		foreach (var a in clean)
		{
			dict[a.Path] = a;
		}
		foreach (var s in store)
		{
			if (!dict.ContainsKey(s.Path))
			{
				dict[s.Path] = s;
			}
		}

		return dict.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
	}

	[DllImport("shlwapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern int SHLoadIndirectString(string pszSource, StringBuilder pszOutBuf, int cchOutBuf, IntPtr ppvReserved);

	private static string ResolveIndirectString(string? res)
	{
		if (string.IsNullOrWhiteSpace(res) || !res.StartsWith("@")) return res ?? "";
		try
		{
			var sb = new StringBuilder(1024);
			int hr = SHLoadIndirectString(res, sb, sb.Capacity, IntPtr.Zero);
			if (hr == 0 && sb.Length > 0)
			{
				string resolved = sb.ToString().Trim();
				if (!resolved.StartsWith("@") && !resolved.StartsWith("ms-resource:"))
					return resolved;
			}
		}
		catch { }
		return "";
	}

	private static List<AppItem> LoadStoreApps()
	{
		var list = new List<AppItem>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		try
		{
			using var repoKey = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
			if (repoKey == null) return list;

			string[] junkWords = { "uninstall", "remove", "reset", "cache", "help", "manual", "documentation", "readme", "license", "updater", "update", "diagnostics" };

			foreach (string pkgName in repoKey.GetSubKeyNames())
			{
				try
				{
					using var pkgKey = repoKey.OpenSubKey(pkgName);
					if (pkgKey == null) continue;

					string? rootFolder = pkgKey.GetValue("PackageRootFolder") as string;
					if (string.IsNullOrWhiteSpace(rootFolder) || !Directory.Exists(rootFolder))
						continue;

					string manifestPath = Path.Combine(rootFolder, "AppxManifest.xml");
					if (!File.Exists(manifestPath))
						continue;

					string? pkgDisplayName = pkgKey.GetValue("DisplayName") as string;

					var doc = XDocument.Load(manifestPath);
					var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;
					var appNodes = doc.Descendants(ns + "Application");

					foreach (var app in appNodes)
					{
						string? exeRel = app.Attribute("Executable")?.Value;
						if (string.IsNullOrWhiteSpace(exeRel)) continue;

						var ve = app.Elements().FirstOrDefault(e => e.Name.LocalName == "VisualElements");
						string? appListEntry = ve?.Attribute("AppListEntry")?.Value;
						if (string.Equals(appListEntry, "none", StringComparison.OrdinalIgnoreCase))
							continue;

						string fullExe = Path.Combine(rootFolder, exeRel);
						if (!File.Exists(fullExe) || AppRegistry.IsBlocked(fullExe)) continue;

						string name = "";
						string? veName = ve?.Attribute("DisplayName")?.Value;
						if (!string.IsNullOrWhiteSpace(veName) && !veName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) && !veName.StartsWith("@"))
						{
							name = veName;
						}
						else if (!string.IsNullOrWhiteSpace(pkgDisplayName) && !pkgDisplayName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) && !pkgDisplayName.StartsWith("@"))
						{
							name = pkgDisplayName;
						}
						else
						{
							string appId = app.Attribute("Id")?.Value ?? "";
							if (!string.IsNullOrEmpty(appId))
							{
								using var appSubKey = pkgKey.OpenSubKey($@"{appId}\Capabilities");
								string? capName = appSubKey?.GetValue("ApplicationName") as string;
								if (!string.IsNullOrWhiteSpace(capName))
								{
									string resolved = ResolveIndirectString(capName);
									if (!string.IsNullOrWhiteSpace(resolved) && !resolved.StartsWith("@") && !resolved.StartsWith("ms-resource:"))
									{
										name = resolved;
									}
								}
							}
						}

						if (string.IsNullOrWhiteSpace(name) || name.StartsWith("@") || name.StartsWith("ms-resource:"))
						{
							name = Path.GetFileNameWithoutExtension(fullExe);
						}

						if (junkWords.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase)))
							continue;

						// Special case: Spotify has Spotify.exe alongside SpotifyLauncher/Migrator
						string spotifyExe = Path.Combine(rootFolder, "Spotify.exe");
						if (pkgName.StartsWith("SpotifyAB.SpotifyMusic", StringComparison.OrdinalIgnoreCase) && File.Exists(spotifyExe) && seen.Add(spotifyExe))
						{
							list.Add(new AppItem
							{
								Name = "Spotify",
								Path = spotifyExe,
								IconSource = AppIconHelper.GetIcon(spotifyExe)
							});
						}

						if (seen.Add(fullExe))
						{
							list.Add(new AppItem
							{
								Name = name,
								Path = fullExe,
								IconSource = AppIconHelper.GetIcon(fullExe)
							});
						}
					}
				}
				catch { }
			}
		}
		catch { }

		return list;
	}

	private void UpdatePassword_Click(object sender, RoutedEventArgs e)
	{
		string password = OldPwBox.Password;
		string password2 = NewPwBox.Password;
		string password3 = ConfirmPwBox.Password;
		string? policyErr = PasswordPolicy.Validate(password2);
		if (policyErr != null)
		{
			PassStatusMsg.Text = policyErr;
			PassStatusMsg.Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113));
			PassStatusMsg.Visibility = Visibility.Visible;
		}
		else if (password2 != password3)
		{
			PassStatusMsg.Text = "New password and Confirm password do not match.";
			PassStatusMsg.Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113));
			PassStatusMsg.Visibility = Visibility.Visible;
		}
		else
		{
			byte[]? oldCheck = AuthGate.TryPassword(password, out int changeWait);
			if (oldCheck == null)
			{
				PassStatusMsg.Text = changeWait > 0
					? "Too many incorrect attempts. Try again in " + LockoutTracker.Format(changeWait) + "."
					: "Current password is incorrect.";
				PassStatusMsg.Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113));
				PassStatusMsg.Visibility = Visibility.Visible;
			}
			else
			{
				Array.Clear(oldCheck);
				if (MasterConfig.ChangePassword(password, password2))
				{
					PassStatusMsg.Text = "Master Password successfully updated!";
					PassStatusMsg.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
					PassStatusMsg.Visibility = Visibility.Visible;
					OldPwBox.Clear();
					NewPwBox.Clear();
					ConfirmPwBox.Clear();
				}
				else
				{
					PassStatusMsg.Text = "Current password is incorrect.";
					PassStatusMsg.Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113));
					PassStatusMsg.Visibility = Visibility.Visible;
				}
			}
		}
	}

	private void RevealRecoveryKey_Click(object sender, RoutedEventArgs e)
	{
		var confirm = MessageBox.Show(
			"For security, your previous Recovery Key is not saved on disk.\n\nGenerating a new Recovery Key will invalidate the previous one. Generate a new key?",
			"Generate New Recovery Key",
			MessageBoxButton.YesNo,
			MessageBoxImage.Question);
		if (confirm != MessageBoxResult.Yes) return;

		PasswordPromptDialog passwordPromptDialog = new PasswordPromptDialog("Enter Master Password to generate a new Recovery Key:")
		{
			Owner = this
		};
		if (passwordPromptDialog.ShowDialog() != true)
		{
			return;
		}
		try
		{
			string? newKey = MasterConfig.RotateRecoveryKey(passwordPromptDialog.Password);
			if (newKey == null)
			{
				MessageBox.Show("Incorrect password.", "Locker", MessageBoxButton.OK, MessageBoxImage.Exclamation);
				return;
			}
			RecoveryKeyDisplay.Text = newKey;
			Clipboard.SetText(newKey);
			MessageBox.Show("New Recovery Key generated and copied to clipboard. Save it in a safe place!", "Locker", MessageBoxButton.OK, MessageBoxImage.Information);
		}
		catch (Exception ex)
		{
			MessageBox.Show("Error: " + ex.Message, "Locker", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
	}

	private void LoadPhoneTab()
	{
		_loadingSettings = true;
		try
		{
			LockerSettingsData current = LockerSettings.Current;
			if (current.PairedPhone != null)
			{
				PhoneDeviceName.Text = current.PairedPhone.DeviceName;
				bool vaultReady = MasterConfig.HasBiometricVaultV2();
				PhoneStatusDetail.Text = vaultReady 
					? $"Paired & Active (Fingerprint Linked) • IP: {current.PairedPhone.LastIpAddress}" 
					: $"Paired on {current.PairedPhone.PairedAt.ToLocalTime():yyyy-MM-dd} • IP: {current.PairedPhone.LastIpAddress} (Fingerprint not linked - press Sync Key)";
				if (SyncBiometricBtn != null)
				{
					SyncBiometricBtn.Visibility = vaultReady ? Visibility.Collapsed : Visibility.Visible;
				}
				PhonePairBtn.Content = "Unpair Phone";
			}
			else
			{
				if (SyncBiometricBtn != null) SyncBiometricBtn.Visibility = Visibility.Collapsed;
				PhoneDeviceName.Text = "No Phone Paired";
				PhoneStatusDetail.Text = "Pair your Android companion app to unlock via Fingerprint & Face.";
				PhonePairBtn.Content = "Pair New Phone";
			}
			if (ToggleSameWiFi != null)
			{
				ToggleSameWiFi.IsChecked = current.RequireSameWiFi;
			}
			if (ToggleBluetooth != null)
			{
				ToggleBluetooth.IsChecked = current.RequireBluetooth;
			}
			_lastPhoneSnapshot = GetPhoneStateSnapshot(current);

		}
		finally
		{
			_loadingSettings = false;
		}
	}

	private async void SyncBiometric_Click(object sender, RoutedEventArgs e)
	{
		LockerSettingsData current = LockerSettings.Current;
		if (current.PairedPhone == null) return;

		PasswordPromptDialog dlg = new PasswordPromptDialog("Enter Master Password to link fingerprint:")
		{
			Owner = this
		};
		if (dlg.ShowDialog() == true && dlg.MasterKey != null)
		{
			byte[] key = (byte[])dlg.MasterKey.Clone();
			Array.Clear(dlg.MasterKey);
			Guard.ToastChip.Show("\uE72B", "Confirm fingerprint on phone", "Touch the fingerprint sensor", System.Windows.Media.Color.FromRgb(251, 191, 36), 4000, this);
			var (ok, reason) = await PhoneCompanionService.EnrollBiometricAsync(key);
			Array.Clear(key);
			LoadPhoneTab();
			if (ok)
				Guard.ToastChip.Show("\uE72B", "Fingerprint Linked!", "This phone's fingerprint is ready", System.Windows.Media.Color.FromRgb(52, 211, 153), 3000, this);
			else
				Guard.ToastChip.Show("\uE72F", "Link failed", reason, System.Windows.Media.Color.FromRgb(248, 113, 113), 3500, this);
		}
	}

	// Asks for the Master Password every time (no timer, nothing cached). Caller owns the array and clears it.
	private byte[]? AcquireKey(string prompt, bool isLock = false, string target = "")
	{
		var dlg = new PasswordPromptDialog(prompt, isLock, target)
		{
			Owner = this
		};
		if (dlg.ShowDialog() != true || dlg.MasterKey == null) return null;
		return dlg.MasterKey;
	}

	private async void EnrollAfterPair(byte[] key)
	{
		try
		{
			var (ok, reason) = await PhoneCompanionService.EnrollBiometricAsync(key, 1500);
			LoadPhoneTab();
			if (ok)
				Guard.ToastChip.Show("\uE72B", "Fingerprint Linked!", "This phone's fingerprint is ready", System.Windows.Media.Color.FromRgb(52, 211, 153), 3000, this);
			else
				Guard.ToastChip.Show("\uE72F", "Fingerprint link failed", "Press Sync Key in Phone Link (" + reason + ")", System.Windows.Media.Color.FromRgb(251, 191, 36), 4000, this);
		}
		catch { }
		finally { Array.Clear(key); }
	}

private void StartPhonePair_Click(object sender, RoutedEventArgs e)
	{
		//IL_0147: Unknown result type (might be due to invalid IL or missing references)
		//IL_014c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0162: Expected Obj, but got Unknown
		LockerSettingsData current = LockerSettings.Current;
		if (current.PairedPhone != null)
		{
			PasswordPromptDialog passwordPromptDialog = new PasswordPromptDialog("Enter Master Password to unpair phone:")
			{
				Owner = this
			};
			if (passwordPromptDialog.ShowDialog() == true && passwordPromptDialog.MasterKey != null)
			{
				Array.Clear(passwordPromptDialog.MasterKey);
				string unpairedName = current.PairedPhone.DeviceName;
				string phoneIp = current.PairedPhone.LastIpAddress;
				string phoneSecret = current.PairedPhone.SharedSecretKey;
				current.PairedPhone = null;
				current.PhoneBiometricsEnabled = false;
				MasterConfig.DeleteBiometricVault();
				LockerSettings.Save(current);
				LoadPhoneTab();
				InexIslandBridge.NotifyPhoneUnpaired(unpairedName);
				_ = PhoneCompanionService.NotifyUnpairToPhoneAsync(phoneIp, phoneSecret);
				Guard.ToastChip.Show("\uE72F", "Phone Unpaired", "Phone disconnected", System.Windows.Media.Color.FromRgb(150, 144, 134), 2800, this);
			}
			return;
		}
		PasswordPromptDialog passwordPromptDialog2 = new PasswordPromptDialog("Enter Master Password to pair a new phone:")
		{
			Owner = this
		};
		if (passwordPromptDialog2.ShowDialog() != true || passwordPromptDialog2.MasterKey == null)
		{
			return;
		}
		byte[] masterKeyCopy = (byte[])passwordPromptDialog2.MasterKey.Clone();
		Array.Clear(passwordPromptDialog2.MasterKey);
		bool handled = false;
		var session = PhoneCompanionService.GeneratePairingSession((PairedPhoneInfo info) =>
		{
			((DispatcherObject)this).Dispatcher.Invoke((Action)(() =>
			{
				if (!handled)
				{
					handled = true;
					EnrollAfterPair((byte[])masterKeyCopy.Clone());
					Array.Clear(masterKeyCopy);
					PairingOverlay.Visibility = Visibility.Collapsed;
					LoadPhoneTab();
					InexIslandBridge.NotifyPhonePaired(info.DeviceName);
					Guard.ToastChip.Show("\uE72B", info.DeviceName + " Paired!", "Confirm fingerprint on phone", System.Windows.Media.Color.FromRgb(52, 211, 153), 3000, this);
				}
			}));
		});
		try
		{
			using var ms = new MemoryStream(session.QrPngBytes);
			var bmp = new BitmapImage();
			bmp.BeginInit();
			bmp.CacheOption = BitmapCacheOption.OnLoad;
			bmp.StreamSource = ms;
			bmp.EndInit();
			bmp.Freeze();
			PairingQrImage.Source = bmp;
		}
		catch { }
		PairingIpText.Text = $"{PhoneCompanionService.GetLocalIpAddress()}:{58820}";
		PairingOverlay.Visibility = Visibility.Visible;
		InexIslandBridge.NotifyPrompt("Pairing phone"); // island shows this for as long as the pairing screen is open
		DispatcherTimer checkPairTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(500L)
		};
		checkPairTimer.Tick += (object? s2, EventArgs e2) =>
		{
			if ((PairingOverlay.Visibility != Visibility.Visible) | handled)
			{
				checkPairTimer.Stop();
			}
			else
			{
				LockerSettingsData current2 = LockerSettings.Current;
				if (current2.PairedPhone != null)
				{
					handled = true;
					checkPairTimer.Stop();
					EnrollAfterPair((byte[])masterKeyCopy.Clone());
					Array.Clear(masterKeyCopy);
					PairingOverlay.Visibility = Visibility.Collapsed;
					LoadPhoneTab();
					InexIslandBridge.NotifyPhonePaired(current2.PairedPhone.DeviceName);
					Guard.ToastChip.Show("\uE72B", current2.PairedPhone.DeviceName + " Paired!", "Confirm fingerprint on phone", System.Windows.Media.Color.FromRgb(52, 211, 153), 3000, this);
				}
			}
		};
		checkPairTimer.Start();
	}

	private void PairingCancel_Click(object sender, RoutedEventArgs e)
	{
		PhoneCompanionService.CancelPairing();
		PairingOverlay.Visibility = Visibility.Collapsed;
		InexIslandBridge.NotifyCancel("Pairing phone");
	}

	private void PhoneSetting_Changed(object sender, RoutedEventArgs e)
	{
		if (_loadingSettings || ToggleSameWiFi == null || ToggleBluetooth == null)
		{
			return;
		}
		LockerSettingsData current = LockerSettings.Current;
		current.RequireSameWiFi = ToggleSameWiFi.IsChecked == true;
		current.RequireBluetooth = ToggleBluetooth.IsChecked == true;
		LockerSettings.Save(current);
	}

	private async void TestPhoneAuth_Click(object sender, RoutedEventArgs e)
	{
		if (LockerSettings.Current.PairedPhone == null)
		{
			Guard.ToastChip.Show("\uE72F", "No Phone Paired", "Pair a phone first", System.Windows.Media.Color.FromRgb(251, 191, 36), 2500, this);
		}
		else if (await PhoneCompanionService.RequestBiometricUnlockAsync("Test Verification"))
		{
			Guard.ToastChip.Show("\uE72B", "Biometric Verified!", "Phone auth works", System.Windows.Media.Color.FromRgb(52, 211, 153), 2500, this);
		}
		else
		{
			Guard.ToastChip.Show("\uE7BA", "Phone Timeout", "Check companion app & WiFi", System.Windows.Media.Color.FromRgb(248, 113, 113), 3000, this);
		}
	}

	private void LoadAboutTab()
	{
		_loadingSettings = true;
		try
		{
			LockerSettingsData current = LockerSettings.Current;
			bool flag = InexIslandBridge.IsInstalled();
			bool flag2 = InexIslandBridge.IsRunning();
			if (ToggleIsland == null)
			{
				return;
			}
			if (!flag)
			{
				ToggleIsland.IsEnabled = false;
				ToggleIsland.IsChecked = false;
				IslandBadgeText.Text = "Not Installed";
				IslandBadgeText.Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113));
				IslandBadge.Background = new SolidColorBrush(Color.FromArgb(51, 239, 68, 68));
				return;
			}
			ToggleIsland.IsEnabled = true;
			ToggleIsland.IsChecked = current.InexIslandAnimation;
			if (flag2)
			{
				IslandBadgeText.Text = "Running";
				IslandBadgeText.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
				IslandBadge.Background = new SolidColorBrush(Color.FromArgb(51, 16, 185, 129));
			}
			else
			{
				IslandBadgeText.Text = "Installed";
				IslandBadgeText.Foreground = new SolidColorBrush(Color.FromRgb(138, 137, 134));
				IslandBadge.Background = new SolidColorBrush(Color.FromArgb(34, byte.MaxValue, byte.MaxValue, byte.MaxValue));
			}
		}
		finally
		{
			_loadingSettings = false;
		}
	}

	private void IslandToggle_Changed(object sender, RoutedEventArgs e)
	{
		if (!_loadingSettings && ToggleIsland != null)
		{
			LockerSettingsData current = LockerSettings.Current;
			current.InexIslandAnimation = ToggleIsland.IsChecked == true;
			LockerSettings.Save(current);
		}
	}

	private void OpenGitHub_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			Process.Start(new ProcessStartInfo("https://github.com/inrwxyz")
			{
				UseShellExecute = true
			});
		}
		catch
		{
		}
	}

	private void StartUninstall_Click(object sender, RoutedEventArgs e)
	{
		UninstallPwBox.Clear();
		UninstallError.Visibility = Visibility.Collapsed;
		UninstallOverlay.Visibility = Visibility.Visible;
		UninstallPwBox.Focus();
	}

	private void UninstallCancel_Click(object sender, RoutedEventArgs e)
	{
		UninstallOverlay.Visibility = Visibility.Collapsed;
	}

	private async void ConfirmUninstall_Click(object sender, RoutedEventArgs e)
	{
		string password = UninstallPwBox.Password;
		if (string.IsNullOrEmpty(password))
		{
			UninstallError.Text = "Enter Master Password.";
			UninstallError.Visibility = Visibility.Visible;
			return;
		}
		byte[]? array = AuthGate.TryPassword(password, out int uninstallWait, allowRecovery: true);
		if (array == null)
		{
			UninstallError.Text = uninstallWait > 0
				? "Too many incorrect attempts. Try again in " + LockoutTracker.Format(uninstallWait) + "."
				: "Incorrect Master Password or Recovery Key.";
			UninstallError.Visibility = Visibility.Visible;
			return;
		}
		Array.Clear(array);
		UninstallOverlay.Visibility = Visibility.Collapsed;
		string text = Path.Combine(AppContext.BaseDirectory, "uninstall.exe");
		if (File.Exists(text))
		{
			try
			{
				string handoffTok = Core.Uninstaller.CreateUninstallHandoff(password);
				Process.Start(new ProcessStartInfo(text, "--handoff " + handoffTok)
				{
					UseShellExecute = true
				});
			}
			catch
			{
			}
		}
		Close();
		Application.Current.Shutdown();
	}

	private void SetupCancel_Click(object sender, RoutedEventArgs e)
	{
		Close();
	}

	private void SetupCreate_Click(object sender, RoutedEventArgs e)
	{
		string password = SetupPw1.Password;
		string password2 = SetupPw2.Password;
		string? setupPolicyErr = PasswordPolicy.Validate(password);
		if (setupPolicyErr != null)
		{
			SetupError.Text = setupPolicyErr;
			SetupError.Visibility = Visibility.Visible;
			return;
		}
		if (password != password2)
		{
			SetupError.Text = "Passwords must match.";
			SetupError.Visibility = Visibility.Visible;
			return;
		}
		try
		{
			string text = MasterConfig.Setup(password);
			_masterKey = MasterConfig.GetKeyWithPassword(password);
			SetupRecoveryKeyText.Text = text;
			SetupInputPanel.Visibility = Visibility.Collapsed;
			SetupSuccessPanel.Visibility = Visibility.Visible;
		}
		catch (Exception ex)
		{
			SetupError.Text = ex.Message;
			SetupError.Visibility = Visibility.Visible;
		}
	}

	private void SetupDone_Click(object sender, RoutedEventArgs e)
	{
		SetupOverlay.Visibility = Visibility.Collapsed;
		RefreshAll();
	}

	// ===================================================================================
	// VAULT LOCKER LOGIC & METHODS
	// Password-only. The chosen folder is encrypted in place (no clone, no zip, no phone).
	// ===================================================================================
	public class VaultDisplayItem
	{
		public string VaultId { get; set; } = "";
		public string FolderPath { get; set; } = "";
		public string FolderName { get; set; } = "";
		public bool IsLocked { get; set; }
		public string StatusText => IsLocked ? "LOCKED" : "UNLOCKED";
		public System.Windows.Media.Brush StatusBadgeBg => IsLocked ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(39, 14, 21)) : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(26, 25, 22));
		public System.Windows.Media.Brush StatusTextFg => IsLocked ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 113, 113)) : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(52, 211, 153));
		public string ActionButtonText => IsLocked ? "Unlock" : "Lock";
	}

	private bool _vaultRelockMode = false;

	private void LoadVaultsList()
	{
		var items = new List<VaultDisplayItem>();
		var settings = LockerSettings.Load();
		foreach (var v in settings.SavedVaults)
		{
			bool isLocked = PortableVaultService.IsVaultLocked(v.FolderPath);
			items.Add(new VaultDisplayItem
			{
				VaultId = v.VaultId,
				FolderPath = v.FolderPath,
				FolderName = v.FolderName,
				IsLocked = isLocked
			});
		}
		VaultListBox.ItemsSource = items;
	}

	private void VaultBrowseLock_Click(object sender, RoutedEventArgs e)
	{
		var dlg = new Microsoft.Win32.OpenFolderDialog
		{
			Title = "Select folder to lock"
		};
		if (dlg.ShowDialog(this) == true && Directory.Exists(dlg.FolderName))
		{
			HandleVaultFolderSelected(dlg.FolderName);
		}
	}

	private void VaultBrowseUnlock_Click(object sender, RoutedEventArgs e)
	{
		var dlg = new Microsoft.Win32.OpenFolderDialog
		{
			Title = "Select Vault folder to unlock"
		};
		if (dlg.ShowDialog(this) == true && Directory.Exists(dlg.FolderName))
		{
			HandleVaultFolderSelected(dlg.FolderName);
		}
	}

	private void HandleVaultFolderSelected(string folderPath)
	{
		if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)) return;

		if (PortableVaultService.IsVault(folderPath))
		{
			VaultMetadata? meta = null;
			try { meta = PortableVaultService.LoadMetadata(folderPath); } catch { }

			if (meta != null && meta.IsLocked)
			{
				_currentUnlockVaultPath = folderPath;
				VaultUnlockTitle.Text = "Unlock Vault: " + meta.FolderName;
				VaultUnlockFolderText.Text = "Folder: " + folderPath;
				VaultUnlockPw.Password = "";
				VaultUnlockError.Visibility = Visibility.Collapsed;
				VaultUnlockProgressPanel.Visibility = Visibility.Collapsed;
				VaultUnlockOverlay.Visibility = Visibility.Visible;
				VaultUnlockPw.Focus();
			}
			else
			{
				var res = MessageBox.Show($"This vault folder ({Path.GetFileName(folderPath.TrimEnd('\\', '/'))}) is currently UNLOCKED. Re-lock it now?", "Vault Locker", MessageBoxButton.YesNo, MessageBoxImage.Question);
				if (res == MessageBoxResult.Yes)
				{
					ShowVaultCreateOverlay(folderPath, relock: true);
				}
				else
				{
					System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = folderPath, UseShellExecute = true });
				}
			}
		}
		else
		{
			// New folder: encrypt it right here (in place)
			ShowVaultCreateOverlay(folderPath, relock: false);
		}
	}

	private void ShowVaultCreateOverlay(string folderPath, bool relock)
	{
		_vaultRelockMode = relock;
		_currentUnlockVaultPath = folderPath;
		VaultCreateFolderPath.Text = folderPath;
		VaultCreatePw1.Password = "";
		VaultCreatePw2.Password = "";
		VaultCreateError.Visibility = Visibility.Collapsed;
		VaultCreateProgressPanel.Visibility = Visibility.Collapsed;
		VaultCreateProgressBar.Value = 0;

		VaultCreateTitle.Text = relock ? "Re-lock Vault" : "Lock Folder with Password";
		VaultCreateNote.Visibility = relock ? Visibility.Collapsed : Visibility.Visible;
		VaultCreatePwLabel1.Text = relock ? "Vault Password:" : "Vault Password (independent from Locker Pro):";
		VaultCreatePw2Panel.Visibility = relock ? Visibility.Collapsed : Visibility.Visible;
		BtnVaultCreateConfirm.Content = relock ? "Lock Vault" : "Encrypt & Lock";

		VaultCreateOverlay.Visibility = Visibility.Visible;
		VaultCreatePw1.Focus();
	}

	private void VaultCreateCancel_Click(object sender, RoutedEventArgs e)
	{
		VaultCreateOverlay.Visibility = Visibility.Collapsed;
	}

	private async void VaultCreateConfirm_Click(object sender, RoutedEventArgs e)
	{
		string folder = VaultCreateFolderPath.Text;
		string pw1 = VaultCreatePw1.Password;
		string pw2 = VaultCreatePw2.Password;

		if (_vaultRelockMode)
		{
			if (string.IsNullOrEmpty(pw1))
			{
				VaultCreateError.Text = "Enter vault password.";
				VaultCreateError.Visibility = Visibility.Visible;
				return;
			}
		}
		else
		{
			if (!LockSafety.IsSafeVaultFolder(folder, out string why))
			{
				VaultCreateError.Text = why;
				VaultCreateError.Visibility = Visibility.Visible;
				return;
			}
			if (string.IsNullOrEmpty(pw1) || pw1.Length < 4)
			{
				VaultCreateError.Text = "Vault password must be at least 4 characters.";
				VaultCreateError.Visibility = Visibility.Visible;
				return;
			}
			if (pw1 != pw2)
			{
				VaultCreateError.Text = "Passwords do not match.";
				VaultCreateError.Visibility = Visibility.Visible;
				return;
			}
		}

		string vaultName = Path.GetFileName(folder.TrimEnd('\\', '/'));
		BtnVaultCreateConfirm.IsEnabled = false;
		VaultCreateError.Visibility = Visibility.Collapsed;
		VaultCreateProgressBar.Value = 0;
		VaultCreateProgressPanel.Visibility = Visibility.Visible;
		var prog = new Progress<(int cur, int tot, string file)>(p =>
		{
			VaultCreateProgressBar.Value = (p.cur * 100.0) / Math.Max(1, p.tot);
			VaultCreateProgressText.Text = $"Encrypting {p.cur}/{p.tot}: {p.file}";
		});

		try
		{
			if (_vaultRelockMode)
				await PortableVaultService.RelockVaultAsync(folder, pw1, prog);
			else
				await PortableVaultService.CreateAndLockVaultAsync(folder, pw1, prog);

			VaultCreateOverlay.Visibility = Visibility.Collapsed;
			LoadVaultsList();
			InexIslandBridge.NotifyLock(vaultName);
			Guard.ToastChip.Show("", "Vault Locked!", vaultName, System.Windows.Media.Color.FromRgb(52, 211, 153), 3000, this);
		}
		catch (Exception ex)
		{
			VaultCreateError.Text = ex.Message;
			VaultCreateError.Visibility = Visibility.Visible;
			LoadVaultsList();
		}
		finally
		{
			BtnVaultCreateConfirm.IsEnabled = true;
			VaultCreateProgressPanel.Visibility = Visibility.Collapsed;
		}
	}

	private void VaultUnlockCancel_Click(object sender, RoutedEventArgs e)
	{
		VaultUnlockOverlay.Visibility = Visibility.Collapsed;
	}

	private async void VaultUnlockPw_Click(object sender, RoutedEventArgs e)
	{
		string pw = VaultUnlockPw.Password;
		if (string.IsNullOrEmpty(pw))
		{
			VaultUnlockError.Text = "Enter vault password.";
			VaultUnlockError.Visibility = Visibility.Visible;
			return;
		}

		BtnVaultUnlockPw.IsEnabled = false;
		VaultUnlockProgressBar.Value = 0;
		VaultUnlockProgressPanel.Visibility = Visibility.Visible;
		VaultUnlockError.Visibility = Visibility.Collapsed;
		var prog = new Progress<(int cur, int tot, string file)>(p =>
		{
			VaultUnlockProgressBar.Value = (p.cur * 100.0) / Math.Max(1, p.tot);
			VaultUnlockProgressText.Text = $"Decrypting {p.cur}/{p.tot}: {p.file}";
		});

		try
		{
			await PortableVaultService.UnlockVaultWithPasswordAsync(_currentUnlockVaultPath, pw, prog);
			VaultUnlockOverlay.Visibility = Visibility.Collapsed;
			LoadVaultsList();
			InexIslandBridge.NotifyUnlock("password", Path.GetFileName(_currentUnlockVaultPath.TrimEnd('\\', '/')));
			Guard.ToastChip.Show("", "Vault Unlocked!", Path.GetFileName(_currentUnlockVaultPath.TrimEnd('\\', '/')), System.Windows.Media.Color.FromRgb(52, 211, 153), 3000, this);
			System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = _currentUnlockVaultPath, UseShellExecute = true });
		}
		catch (Exception ex)
		{
			VaultUnlockError.Text = ex.Message;
			VaultUnlockError.Visibility = Visibility.Visible;
		}
		finally
		{
			BtnVaultUnlockPw.IsEnabled = true;
			VaultUnlockProgressPanel.Visibility = Visibility.Collapsed;
		}
	}

	private void VaultAction_Click(object sender, RoutedEventArgs e)
	{
		if (sender is Button btn && btn.Tag is VaultDisplayItem item)
		{
			HandleVaultFolderSelected(item.FolderPath);
		}
	}

	private void VaultRemoveList_Click(object sender, RoutedEventArgs e)
	{
		if (sender is Button btn && btn.Tag is VaultDisplayItem item)
		{
			var settings = LockerSettings.Load();
			settings.SavedVaults.RemoveAll(v => v.VaultId == item.VaultId || v.FolderPath.Equals(item.FolderPath, StringComparison.OrdinalIgnoreCase));
			LockerSettings.Save(settings);
			LoadVaultsList();
		}
	}



}
