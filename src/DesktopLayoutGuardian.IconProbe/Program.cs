using System.Text.Json;
using DesktopLayoutGuardian.Services;

namespace DesktopLayoutGuardian.IconProbe;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var snapshot = new DesktopIconLayoutService().Capture();
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            if (args.Contains("--profile-roundtrip", StringComparer.OrdinalIgnoreCase))
            {
                var display = new DisplayConfigurationService().Capture("布局存储测试");
                var store = new DesktopLayoutProfileStore();
                var saved = store.SaveAsync(display, snapshot).GetAwaiter().GetResult();
                var updated = store.SaveAsync(display, snapshot).GetAwaiter().GetResult();
                var match = store.FindMatchAsync(display).GetAwaiter().GetResult();
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    SavedProfile = saved.Id,
                    UpdatedProfile = updated.Id,
                    MatchFound = match is not null,
                    match?.IsExactMatch,
                    IconCount = match?.Profile.Layout.Icons.Count,
                    store.ProfileDirectory,
                    store.HistoryDirectory
                }, new JsonSerializerOptions { WriteIndented = true }));
                return match is not null && match.Profile.Layout.Icons.Count == snapshot.Icons.Count ? 0 : 2;
            }

            Console.WriteLine(JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }
}
