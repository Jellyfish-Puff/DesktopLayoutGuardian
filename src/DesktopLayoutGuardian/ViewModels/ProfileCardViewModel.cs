using System.IO;
using System.Windows.Media.Imaging;
using DesktopLayoutGuardian.Models;

namespace DesktopLayoutGuardian.ViewModels;

public sealed class ProfileCardViewModel
{
    public ProfileCardViewModel(
        DesktopLayoutProfile profile,
        string? previewPath,
        bool isCurrent,
        bool canRestore)
    {
        Profile = profile;
        EditableName = profile.Name;
        IsCurrent = isCurrent;
        CanRestore = canRestore;
        PreviewImage = LoadPreview(previewPath);

        var display = profile.Display.Displays.FirstOrDefault();
        DisplaySummary = display is null
            ? "显示信息不可用"
            : $"{display.Width} × {display.Height}  ·  {display.ScalePercent}% 缩放  ·  {(display.IsVirtual ? "虚拟屏" : "实体屏")}";
        LayoutSummary = $"{profile.Layout.Icons.Count} 个图标  ·  更新于 {profile.UpdatedAt:yyyy-MM-dd HH:mm}";
        CurrentBadge = isCurrent ? "当前环境" : "已保存方案";
    }

    public DesktopLayoutProfile Profile { get; }

    public string EditableName { get; set; }

    public bool IsCurrent { get; }

    public bool CanRestore { get; }

    public BitmapImage? PreviewImage { get; }

    public string DisplaySummary { get; }

    public string LayoutSummary { get; }

    public string CurrentBadge { get; }

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
