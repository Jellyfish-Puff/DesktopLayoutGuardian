using DesktopLayoutGuardian.Models;
using DesktopLayoutGuardian.Services;

var testRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".testrun", "v03-core"));
Environment.SetEnvironmentVariable("DLG_DATA_ROOT", testRoot);
Environment.SetEnvironmentVariable("DLG_DIAGNOSTIC_ROOT", Path.Combine(testRoot, "diagnostics"));

var display = new DisplaySnapshot
{
    CapturedAt = DateTimeOffset.Now,
    Trigger = "核心测试",
    ConfigurationKey = "TEST-CONFIG-2560X1440-150",
    Displays =
    [
        new DisplayMonitorInfo
        {
            FriendlyName = "TEST-DISPLAY",
            Width = 2560,
            Height = 1440,
            ScalePercent = 150,
            Rotation = "横向 (0°)",
            OutputTechnology = "内部显示器",
            IsPrimary = true,
            ExactIdentityKey = "TEST-EXACT",
            CompatibilityIdentityKey = "TEST-COMPATIBLE"
        }
    ]
};

var layout = new DesktopLayoutSnapshot
{
    CapturedAt = DateTimeOffset.Now,
    AutoArrangeEnabled = false,
    HorizontalSpacing = 75,
    VerticalSpacing = 98,
    Icons =
    [
        new DesktopIconPosition { Identity = "desktop:test-one", DisplayName = "测试一", X = 100, Y = 100 },
        new DesktopIconPosition { Identity = "desktop:test-two", DisplayName = "测试二", X = 200, Y = 100 }
    ]
};

var settingsStore = new ApplicationSettingsStore();
var expectedSettings = new ApplicationSettings
{
    StartWithWindows = false,
    ShowRestoreNotifications = true,
    DisplayChangeDelayMilliseconds = 3200,
    StabilityProbeDelayMilliseconds = 900
};
await settingsStore.SaveAsync(expectedSettings);
var loadedSettings = await settingsStore.LoadAsync();
Assert(loadedSettings.ShowRestoreNotifications, "通知设置未能持久化");
Assert(loadedSettings.DisplayChangeDelayMilliseconds == 3200, "防抖设置未能持久化");

var profileStore = new DesktopLayoutProfileStore();
var firstProfile = await profileStore.SaveAsync(display, layout);
var secondProfile = await profileStore.SaveAsync(display, layout);
var match = await profileStore.FindMatchAsync(display);
Assert(firstProfile.CreatedAt == secondProfile.CreatedAt, "再次保存不应改变方案创建时间");
Assert(match?.IsExactMatch == true, "保存后的方案无法精确匹配");
Assert(match!.Profile.Layout.Icons.Count == 2, "保存后的图标数量不正确");
Assert(Directory.EnumerateFiles(profileStore.HistoryDirectory, "*.json", SearchOption.AllDirectories).Any(), "再次保存没有生成历史备份");

var recoveryStore = new DesktopLayoutRecoveryStore();
await recoveryStore.SaveAsync(display, layout, "测试方案");
var recovery = await recoveryStore.LoadAsync();
Assert(recovery?.ConfigurationKey == display.ConfigurationKey, "撤销快照的显示环境不正确");
Assert(recovery!.Layout.Icons.Count == 2, "撤销快照的图标数量不正确");
await recoveryStore.ClearAsync();
Assert(await recoveryStore.LoadAsync() is null, "撤销快照清除失败");

var logService = new DiagnosticLogService();
await logService.AppendAsync(display);
Assert(File.Exists(logService.LogFilePath), "诊断日志没有写入");

var staleTemporaryFiles = Directory.EnumerateFiles(testRoot, "*.tmp", SearchOption.AllDirectories).ToArray();
Assert(staleTemporaryFiles.Length == 0, "原子写入遗留了临时文件");

Console.WriteLine("PASS: settings atomic write");
Console.WriteLine("PASS: profile compatibility and history backup");
Console.WriteLine("PASS: recovery snapshot save/load/clear");
Console.WriteLine("PASS: diagnostic log append");

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
