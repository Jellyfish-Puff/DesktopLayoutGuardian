using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using DesktopLayoutGuardian.Models;
using Microsoft.Win32;

namespace DesktopLayoutGuardian.Services;

public sealed class DesktopLayoutPreviewService
{
    private const int PreviewWidth = 640;
    private const int PreviewHeight = 360;
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiSmallIcon = 0x000000001;
    private const uint ShgfiPidl = 0x000000008;

    public DesktopLayoutPreviewService()
    {
        var dataRootOverride = Environment.GetEnvironmentVariable("DLG_DATA_ROOT");
        var dataRoot = string.IsNullOrWhiteSpace(dataRootOverride)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DesktopLayoutGuardian")
            : Path.GetFullPath(dataRootOverride);
        PreviewDirectory = Path.Combine(dataRoot, "previews");
    }

    public string PreviewDirectory { get; }

    public Task<string> CreateAsync(
        DisplaySnapshot displaySnapshot,
        DesktopLayoutSnapshot layout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(displaySnapshot);
        ArgumentNullException.ThrowIfNull(layout);
        if (displaySnapshot.Displays.Count != 1)
        {
            throw new InvalidOperationException("只有单显示器环境可以生成布局预览。");
        }

        return Task.Run(() => Create(displaySnapshot, layout, cancellationToken), cancellationToken);
    }

    public string GetAbsolutePath(string previewImageFileName) =>
        Path.Combine(PreviewDirectory, Path.GetFileName(previewImageFileName));

    private string Create(
        DisplaySnapshot displaySnapshot,
        DesktopLayoutSnapshot layout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(PreviewDirectory);

        var fileName = $"{displaySnapshot.ConfigurationKey}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.jpg";
        var targetPath = GetAbsolutePath(fileName);
        var temporaryPath = targetPath + $".{Guid.NewGuid():N}.tmp";

        try
        {
            using var bitmap = new Bitmap(PreviewWidth, PreviewHeight, PixelFormat.Format24bppRgb);
            using var graphics = Graphics.FromImage(bitmap);
            ConfigureGraphics(graphics);
            DrawBackground(graphics);
            DrawIcons(graphics, displaySnapshot.Displays[0], layout, cancellationToken);
            DrawPreviewShade(graphics);
            SaveJpeg(bitmap, temporaryPath);
            File.Move(temporaryPath, targetPath, overwrite: false);
            return fileName;
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                // A stale preview temp file is ignored and can be cleaned on a later run.
            }
        }
    }

    private static void ConfigureGraphics(Graphics graphics)
    {
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.HighQuality;
    }

    private static void DrawBackground(Graphics graphics)
    {
        using var fallback = new LinearGradientBrush(
            new Rectangle(0, 0, PreviewWidth, PreviewHeight),
            Color.FromArgb(24, 44, 73),
            Color.FromArgb(39, 91, 137),
            20f);
        graphics.FillRectangle(fallback, 0, 0, PreviewWidth, PreviewHeight);

        var wallpaperPath = FindWallpaperPath();
        if (wallpaperPath is null)
        {
            return;
        }

        try
        {
            using var stream = new FileStream(wallpaperPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var wallpaper = Image.FromStream(stream, useEmbeddedColorManagement: true, validateImageData: false);
            var scale = Math.Max(
                PreviewWidth / (double)wallpaper.Width,
                PreviewHeight / (double)wallpaper.Height);
            var targetWidth = wallpaper.Width * scale;
            var targetHeight = wallpaper.Height * scale;
            var targetRectangle = new RectangleF(
                (float)((PreviewWidth - targetWidth) / 2),
                (float)((PreviewHeight - targetHeight) / 2),
                (float)targetWidth,
                (float)targetHeight);
            graphics.DrawImage(wallpaper, targetRectangle);
        }
        catch (ArgumentException)
        {
            // Unsupported wallpaper formats use the neutral fallback background.
        }
        catch (IOException)
        {
            // A wallpaper being replaced by Windows should not block saving a layout.
        }
        catch (UnauthorizedAccessException)
        {
            // The preview remains useful with its fallback background.
        }
    }

    private static string? FindWallpaperPath()
    {
        using var desktopKey = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", writable: false);
        var configuredPath = desktopKey?.GetValue("WallPaper") as string;
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return configuredPath;
        }

        var transcodedWallpaper = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft",
            "Windows",
            "Themes",
            "TranscodedWallpaper");
        return File.Exists(transcodedWallpaper) ? transcodedWallpaper : null;
    }

    private static void DrawIcons(
        Graphics graphics,
        DisplayMonitorInfo display,
        DesktopLayoutSnapshot layout,
        CancellationToken cancellationToken)
    {
        var scaleX = PreviewWidth / (double)Math.Max(display.Width, 1);
        var scaleY = PreviewHeight / (double)Math.Max(display.Height, 1);
        const int iconSize = 18;

        foreach (var desktopIcon in layout.Icons)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var x = Math.Clamp((int)Math.Round(desktopIcon.X * scaleX), 0, PreviewWidth - iconSize);
            var y = Math.Clamp((int)Math.Round(desktopIcon.Y * scaleY), 0, PreviewHeight - iconSize);
            var target = new Rectangle(x, y, iconSize, iconSize);

            using var icon = TryLoadShellIcon(desktopIcon.Identity);
            if (icon is not null)
            {
                graphics.DrawIcon(icon, target);
                continue;
            }

            using var tileBrush = new SolidBrush(Color.FromArgb(225, 238, 246, 255));
            graphics.FillRoundedRectangle(tileBrush, target, 4);
            using var dotBrush = new SolidBrush(Color.FromArgb(200, 79, 124, 255));
            graphics.FillEllipse(dotBrush, x + 5, y + 5, 8, 8);
        }
    }

    private static Icon? TryLoadShellIcon(string parsingName)
    {
        IntPtr pidl = IntPtr.Zero;
        IntPtr iconHandle = IntPtr.Zero;
        try
        {
            var result = NativeMethods.SHParseDisplayName(parsingName, IntPtr.Zero, out pidl, 0, out _);
            if (result < 0 || pidl == IntPtr.Zero)
            {
                return null;
            }

            var fileInfo = new ShellFileInfo();
            var infoResult = NativeMethods.SHGetFileInfo(
                pidl,
                0,
                ref fileInfo,
                (uint)Marshal.SizeOf<ShellFileInfo>(),
                ShgfiPidl | ShgfiIcon | ShgfiSmallIcon);
            iconHandle = fileInfo.IconHandle;
            if (infoResult == IntPtr.Zero || iconHandle == IntPtr.Zero)
            {
                return null;
            }

            using var borrowedIcon = Icon.FromHandle(iconHandle);
            return (Icon)borrowedIcon.Clone();
        }
        catch (ArgumentException)
        {
            return null;
        }
        finally
        {
            if (iconHandle != IntPtr.Zero)
            {
                NativeMethods.DestroyIcon(iconHandle);
            }

            if (pidl != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(pidl);
            }
        }
    }

    private static void DrawPreviewShade(Graphics graphics)
    {
        using var shade = new LinearGradientBrush(
            new Rectangle(0, PreviewHeight - 70, PreviewWidth, 70),
            Color.FromArgb(0, 0, 0, 0),
            Color.FromArgb(70, 0, 0, 0),
            LinearGradientMode.Vertical);
        graphics.FillRectangle(shade, 0, PreviewHeight - 70, PreviewWidth, 70);
    }

    private static void SaveJpeg(Bitmap bitmap, string targetPath)
    {
        var encoder = ImageCodecInfo.GetImageEncoders()
            .First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 82L);
        bitmap.Save(targetPath, encoder, parameters);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr IconHandle;
        public int IconIndex;
        public uint Attributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }

    private static class NativeMethods
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        internal static extern int SHParseDisplayName(
            string name,
            IntPtr bindingContext,
            out IntPtr itemIdList,
            uint attributes,
            out uint attributesOut);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr SHGetFileInfo(
            IntPtr itemIdList,
            uint fileAttributes,
            ref ShellFileInfo fileInfo,
            uint fileInfoSize,
            uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyIcon(IntPtr iconHandle);
    }
}

internal static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics graphics, Brush brush, Rectangle rectangle, int radius)
    {
        using var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }
}
