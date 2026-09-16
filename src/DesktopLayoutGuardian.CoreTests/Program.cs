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
    AutoRestoreEnabled = false,
    DetectionStrategy = "Stable",
    HistoryRetentionPerProfile = 20
};
await settingsStore.SaveAsync(expectedSettings);
var loadedSettings = await settingsStore.LoadAsync();
Assert(loadedSettings.ShowRestoreNotifications, "通知设置未能持久化");
Assert(!loadedSettings.AutoRestoreEnabled, "自动恢复开关未能持久化");
Assert(loadedSettings.DetectionStrategy == "Stable", "检测策略未能持久化");
Assert(loadedSettings.DisplayChangeDelayMilliseconds == 5000, "稳健检测策略没有应用正确的防抖时间");
Assert(loadedSettings.StabilityProbeDelayMilliseconds == 1400, "稳健检测策略没有应用正确的确认时间");
Assert(loadedSettings.HistoryRetentionPerProfile == 20, "历史保留数量未能持久化");

var profileStore = new DesktopLayoutProfileStore();
var previewService = new DesktopLayoutPreviewService();
var previewFileName = await previewService.CreateAsync(display, layout);
Assert(File.Exists(previewService.GetAbsolutePath(previewFileName)), "布局预览没有生成");
var firstProfile = await profileStore.SaveAsync(display, layout, previewFileName);
var secondProfile = await profileStore.SaveAsync(display, layout, previewFileName);
var match = await profileStore.FindMatchAsync(display);
Assert(firstProfile.CreatedAt == secondProfile.CreatedAt, "再次保存不应改变方案创建时间");
Assert(match?.IsExactMatch == true, "保存后的方案无法精确匹配");
Assert(match!.Profile.Layout.Icons.Count == 2, "保存后的图标数量不正确");
Assert(match.Profile.PreviewImageFileName == previewFileName, "方案没有保存布局预览引用");
Assert(Directory.EnumerateFiles(profileStore.HistoryDirectory, "*.json", SearchOption.AllDirectories).Any(), "再次保存没有生成历史备份");
var history = await profileStore.GetHistoryAsync();
Assert(history.Count > 0, "历史记录无法读取");
Assert(history[0].Layout.Icons.Count == 2, "历史记录中的布局不完整");
var renamedProfile = await profileStore.RenameAsync(display.ConfigurationKey, "核心测试方案");
Assert(renamedProfile.Name == "核心测试方案", "方案重命名失败");
Assert((await profileStore.FindMatchAsync(display))?.Profile.Name == "核心测试方案", "重命名没有写入当前方案");
var savedAfterRename = await profileStore.SaveAsync(display, layout, previewFileName);
Assert(savedAfterRename.Name == "核心测试方案", "再次保存布局时不应覆盖自定义名称");
var orphanPreviewPath = Path.Combine(profileStore.PreviewDirectory, "orphan-preview.jpg");
await File.WriteAllTextAsync(orphanPreviewPath, "orphan");
var cleanupResult = await profileStore.CleanupAsync(2);
Assert((await profileStore.GetHistoryAsync()).Count(profile => profile.Id == display.ConfigurationKey) <= 2, "历史保留数量没有生效");
Assert(cleanupResult.DeletedPreviewCount >= 1 && !File.Exists(orphanPreviewPath), "无引用预览没有被清理");

var deletableDisplay = new DisplaySnapshot
{
    CapturedAt = DateTimeOffset.Now,
    Trigger = "删除测试",
    ConfigurationKey = "TEST-DELETE-CONFIG",
    Displays =
    [
        new DisplayMonitorInfo
        {
            FriendlyName = "TEST-DELETE-DISPLAY",
            Width = 1920,
            Height = 1080,
            ScalePercent = 100,
            Rotation = "横向 (0°)",
            OutputTechnology = "DisplayPort",
            IsPrimary = true,
            ExactIdentityKey = "TEST-DELETE-EXACT",
            CompatibilityIdentityKey = "TEST-DELETE-COMPATIBLE"
        }
    ]
};
await profileStore.SaveAsync(deletableDisplay, layout, previewFileName);
await profileStore.DeleteAsync(deletableDisplay.ConfigurationKey);
Assert(await profileStore.FindMatchAsync(deletableDisplay) is null, "删除后的方案仍然可以匹配");
Assert(Directory.EnumerateFiles(profileStore.DeletedDirectory, "*.json").Any(), "删除的方案没有移入备份目录");

var deleteAllRoot = Path.Combine(testRoot, $"delete-all-case-{Guid.NewGuid():N}");
Environment.SetEnvironmentVariable("DLG_DATA_ROOT", deleteAllRoot);
var deleteAllStore = new DesktopLayoutProfileStore();
await deleteAllStore.SaveAsync(display, layout);
await deleteAllStore.SaveAsync(deletableDisplay, layout);
Assert(deleteAllStore.DeleteAll() == 2, "安全清空没有移动全部当前方案");
Assert((await deleteAllStore.GetAllAsync()).Count == 0, "安全清空后仍存在当前方案");
Assert(Directory.EnumerateFiles(deleteAllStore.DeletedDirectory, "*.json").Count() == 2, "安全清空的方案没有进入 deleted 目录");
Environment.SetEnvironmentVariable("DLG_DATA_ROOT", testRoot);

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
Console.WriteLine("PASS: auto-restore, detection strategy, and history settings");
Console.WriteLine("PASS: profile compatibility and history backup");
Console.WriteLine("PASS: privacy-safe desktop layout preview");
Console.WriteLine("PASS: profile history, rename, and recoverable delete");
Console.WriteLine("PASS: history cleanup and recoverable reset");
Console.WriteLine("PASS: recovery snapshot save/load/clear");
Console.WriteLine("PASS: diagnostic log append");

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
