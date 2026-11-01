using System.Windows.Media;

namespace AppLocker.Manager;

public class AppItem
{
	public string Name { get; set; } = "";

	public string Path { get; set; } = "";

	public ImageSource? IconSource { get; set; }
}
