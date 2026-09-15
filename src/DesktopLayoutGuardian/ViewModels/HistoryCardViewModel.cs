using System.IO;
using System.Windows.Media.Imaging;
using DesktopLayoutGuardian.Models;

namespace DesktopLayoutGuardian.ViewModels;

public sealed class HistoryCardViewModel
{
    public HistoryCardViewModel(DesktopLayoutProfile profile, string? previewPath, bool canRestore)
    {
        Profile = profile;
        CanRestore = canRestore;
        PreviewImage = LoadPreview(previewPath);

        var display = profile.Display.Displays.FirstOrDefault();
        ProfileName = profile.Name;
        SavedAt = $"保存于 {profile.UpdatedAt:yyyy-MM-dd HH:mm:ss}";
        DisplaySummary = display is null
            ? "显示信息不可用"
            : $"{display.FriendlyName}  ·  {display.Width} × {display.Height}  ·  {display.ScalePercent}%";
        LayoutSummary = $"{profile.Layout.Icons.Count} 个图标";
    }

    public DesktopLayoutProfile Profile { get; }

    public bool CanRestore { get; }

    public BitmapImage? PreviewImage { get; }

    public string ProfileName { get; }

    public string SavedAt { get; }

    public string DisplaySummary { get; }

    public string LayoutSummary { get; }

    private static BitmapImage? LoadPreview(string? previewPath)
    {
        if (string.IsNullOrWhiteSpace(previewPath) || !File.Exists(previewPath))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(previewPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (IOException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}
