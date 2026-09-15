using System.IO;
using System.Text.Json;

namespace DesktopLayoutGuardian.Services;

internal static class AtomicJsonFile
{
    public static async Task WriteAsync<T>(
        string targetPath,
        T value,
        JsonSerializerOptions options,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException("目标文件没有有效的父目录。");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, value, options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                // A completed move removes the temporary file. A failed cleanup is harmless.
            }
            catch (UnauthorizedAccessException)
            {
                // Keep the original error if one occurred; a stale temp file is ignored on load.
            }
        }
    }
}

