using System.Windows;
using System.Windows.Media;

namespace AppLocker.Manager;

public class DisplayItem
{
	public string Name { get; set; } = "";

	public string Path { get; set; } = "";

	public bool IsFolder { get; set; }

	public string TypeLabel
	{
		get
		{
			if (!IsFolder)
			{
				return "APP";
			}
			return "FOLDER";
		}
	}

	public SolidColorBrush BadgeBackground
	{
		get
		{
			if (!IsFolder)
			{
				return new SolidColorBrush(Color.FromArgb(40, 56, 189, 248));
			}
			return new SolidColorBrush(Color.FromArgb(40, byte.MaxValue, byte.MaxValue, byte.MaxValue));
		}
	}

	public SolidColorBrush BadgeForeground
	{
		get
		{
			if (!IsFolder)
			{
				return new SolidColorBrush(Color.FromRgb(56, 189, 248));
			}
			return new SolidColorBrush(Color.FromRgb(226, 226, 234));
		}
	}

	public ImageSource? IconSource { get; set; }

	public Visibility IconVisibility
	{
		get
		{
			if (IsFolder || IconSource == null)
			{
				return Visibility.Collapsed;
			}
			return Visibility.Visible;
		}
	}

	public Visibility FolderIconVisibility
	{
		get
		{
			if (!IsFolder && IconSource != null)
			{
				return Visibility.Collapsed;
			}
			return Visibility.Visible;
		}
	}
}
