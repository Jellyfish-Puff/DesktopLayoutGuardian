using System.Runtime.InteropServices;
using DesktopLayoutGuardian.Models;

namespace DesktopLayoutGuardian.Services;

public sealed class DesktopIconLayoutService
{
    private const int CsidlDesktop = 0;
    private const int SwcDesktop = 8;
    private const int SwfoNeedDispatch = 1;
    private const uint SvgioAllView = 0x00000002;
    private const uint SvsiPositionItem = 0x00000080;
    private const uint SigdnNormalDisplay = 0x00000000;
    private const uint SigdnDesktopAbsoluteParsing = 0x80028000;

    private static readonly Guid ClsidShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid SidTopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid IidShellBrowser = new("000214E2-0000-0000-C000-000000000046");
    private static readonly Guid IidFolderView = new("CDE725B0-CCC9-4519-917E-325D72FAB4CE");
    private static readonly Guid IidShellFolder = new("000214E6-0000-0000-C000-000000000046");
    private static readonly Guid IidShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    public DesktopLayoutSnapshot Capture()
    {
        EnsureStaThread();
        using var context = OpenDesktopView();

        ThrowIfFailed(context.View.ItemCount(SvgioAllView, out var count), "无法读取桌面图标数量");
        var icons = new List<DesktopIconPosition>(count);

        for (var index = 0; index < count; index++)
        {
            IntPtr pidl = IntPtr.Zero;
            try
            {
                ThrowIfFailed(context.View.Item(index, out pidl), $"无法读取第 {index + 1} 个桌面图标");
                ThrowIfFailed(context.View.GetItemPosition(pidl, out var point), $"无法读取第 {index + 1} 个桌面图标的位置");
                var names = GetNames(context.Folder, pidl, index);
                icons.Add(new DesktopIconPosition
                {
                    Identity = names.Identity,
                    DisplayName = names.DisplayName,
                    X = point.X,
                    Y = point.Y
                });
            }
            finally
            {
                if (pidl != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(pidl);
                }
            }
        }

        var spacing = new NativePoint();
        var spacingResult = context.View.GetSpacing(ref spacing);
        if (spacingResult < 0)
        {
            spacing = default;
        }

        return new DesktopLayoutSnapshot
        {
            CapturedAt = DateTimeOffset.Now,
            AutoArrangeEnabled = context.View.GetAutoArrange() == 0,
            HorizontalSpacing = spacing.X,
            VerticalSpacing = spacing.Y,
            Icons = icons
                .OrderBy(icon => icon.X)
                .ThenBy(icon => icon.Y)
                .ThenBy(icon => icon.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray()
        };
    }

    public DesktopLayoutRestoreResult Restore(DesktopLayoutSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        EnsureStaThread();
        using var context = OpenDesktopView();

        if (context.View.GetAutoArrange() == 0)
        {
            throw new InvalidOperationException("桌面已启用“自动排列图标”，请先关闭该选项再恢复布局。");
        }

        ThrowIfFailed(context.View.ItemCount(SvgioAllView, out var count), "无法读取当前桌面图标数量");
        var currentItems = new Dictionary<string, DesktopPidl>(StringComparer.OrdinalIgnoreCase);

        try
        {
            for (var index = 0; index < count; index++)
            {
                ThrowIfFailed(context.View.Item(index, out var pidl), $"无法读取第 {index + 1} 个当前桌面图标");
                try
                {
                    var names = GetNames(context.Folder, pidl, index);
                    if (!currentItems.ContainsKey(names.Identity))
                    {
                        currentItems[names.Identity] = new DesktopPidl(pidl, names.DisplayName);
                        pidl = IntPtr.Zero;
                    }
                }
                finally
                {
                    if (pidl != IntPtr.Zero)
                    {
                        Marshal.FreeCoTaskMem(pidl);
                    }
                }
            }

            var pidls = new List<IntPtr>(snapshot.Icons.Count);
            var positions = new List<NativePoint>(snapshot.Icons.Count);
            var missing = 0;

            foreach (var savedIcon in snapshot.Icons)
            {
                if (!currentItems.TryGetValue(savedIcon.Identity, out var current))
                {
                    missing++;
                    continue;
                }

                pidls.Add(current.Pidl);
                positions.Add(new NativePoint(savedIcon.X, savedIcon.Y));
            }

            if (pidls.Count > 0)
            {
                ThrowIfFailed(
                    context.View.SelectAndPositionItems(
                        checked((uint)pidls.Count),
                        pidls.ToArray(),
                        positions.ToArray(),
                        SvsiPositionItem),
                    "Windows 未能恢复桌面图标位置");
            }

            var savedIdentities = new HashSet<string>(snapshot.Icons.Select(icon => icon.Identity), StringComparer.OrdinalIgnoreCase);
            return new DesktopLayoutRestoreResult
            {
                RestoredCount = pidls.Count,
                MissingCount = missing,
                NewIconCount = currentItems.Keys.Count(identity => !savedIdentities.Contains(identity))
            };
        }
        finally
        {
            foreach (var item in currentItems.Values)
            {
                Marshal.FreeCoTaskMem(item.Pidl);
            }
        }
    }

    private static DesktopViewContext OpenDesktopView()
    {
        object? shellWindows = null;
        object? desktopDispatch = null;
        object? browserObject = null;
        object? viewObject = null;
        object? folderObject = null;
        IntPtr browserPointer = IntPtr.Zero;
        IntPtr shellViewPointer = IntPtr.Zero;
        IntPtr folderViewPointer = IntPtr.Zero;

        try
        {
            var shellWindowsType = Type.GetTypeFromCLSID(ClsidShellWindows, throwOnError: true)
                ?? throw new InvalidOperationException("Windows ShellWindows 组件不可用。");
            shellWindows = Activator.CreateInstance(shellWindowsType)
                ?? throw new InvalidOperationException("无法创建 Windows ShellWindows 组件。");

            dynamic automation = shellWindows;
            object location = CsidlDesktop;
            object root = Type.Missing;
            int desktopWindow;
            desktopDispatch = automation.FindWindowSW(
                ref location,
                ref root,
                SwcDesktop,
                out desktopWindow,
                SwfoNeedDispatch);

            if (desktopDispatch is not IServiceProviderNative serviceProvider)
            {
                throw new InvalidOperationException("无法访问 Windows 桌面服务提供程序。");
            }

            var serviceId = SidTopLevelBrowser;
            var browserId = IidShellBrowser;
            ThrowIfFailed(
                serviceProvider.QueryService(ref serviceId, ref browserId, out browserPointer),
                "无法取得 Windows 桌面浏览器");

            browserObject = Marshal.GetObjectForIUnknown(browserPointer);
            var browser = (IShellBrowserNative)browserObject;
            ThrowIfFailed(browser.QueryActiveShellView(out shellViewPointer), "无法取得活动桌面视图");

            var folderViewId = IidFolderView;
            ThrowIfFailed(Marshal.QueryInterface(shellViewPointer, in folderViewId, out folderViewPointer), "活动桌面不支持 IFolderView");
            viewObject = Marshal.GetObjectForIUnknown(folderViewPointer);
            var view = (IFolderViewNative)viewObject;

            var shellFolderId = IidShellFolder;
            ThrowIfFailed(view.GetFolder(ref shellFolderId, out folderObject), "无法取得桌面 Shell 文件夹");

            var context = new DesktopViewContext(
                view,
                folderObject,
                shellWindows,
                desktopDispatch,
                browserObject,
                viewObject);

            shellWindows = null;
            desktopDispatch = null;
            browserObject = null;
            viewObject = null;
            folderObject = null;
            return context;
        }
        catch
        {
            ReleaseComObject(folderObject);
            ReleaseComObject(viewObject);
            ReleaseComObject(browserObject);
            ReleaseComObject(desktopDispatch);
            ReleaseComObject(shellWindows);
            throw;
        }
        finally
        {
            if (folderViewPointer != IntPtr.Zero)
            {
                Marshal.Release(folderViewPointer);
            }

            if (shellViewPointer != IntPtr.Zero)
            {
                Marshal.Release(shellViewPointer);
            }

            if (browserPointer != IntPtr.Zero)
            {
                Marshal.Release(browserPointer);
            }
        }
    }

    private static (string Identity, string DisplayName) GetNames(object shellFolder, IntPtr pidl, int index)
    {
        IShellItemNative? shellItem = null;
        try
        {
            var shellItemId = IidShellItem;
            ThrowIfFailed(
                NativeMethods.SHCreateItemWithParent(IntPtr.Zero, shellFolder, pidl, ref shellItemId, out shellItem),
                $"无法解析第 {index + 1} 个桌面图标的身份");

            var displayName = GetShellItemName(shellItem, SigdnNormalDisplay);
            var identity = GetShellItemName(shellItem, SigdnDesktopAbsoluteParsing);
            if (string.IsNullOrWhiteSpace(identity))
            {
                identity = $"display-name:{displayName}";
            }

            return (identity, string.IsNullOrWhiteSpace(displayName) ? identity : displayName);
        }
        finally
        {
            ReleaseComObject(shellItem);
        }
    }

    private static string GetShellItemName(IShellItemNative shellItem, uint kind)
    {
        IntPtr valuePointer = IntPtr.Zero;
        try
        {
            var result = shellItem.GetDisplayName(kind, out valuePointer);
            return result >= 0 && valuePointer != IntPtr.Zero
                ? Marshal.PtrToStringUni(valuePointer) ?? string.Empty
                : string.Empty;
        }
        finally
        {
            if (valuePointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(valuePointer);
            }
        }
    }

    private static void EnsureStaThread()
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            throw new InvalidOperationException("桌面图标操作必须在 Windows STA 线程上执行。");
        }
    }

    private static void ThrowIfFailed(int result, string message)
    {
        if (result < 0)
        {
            throw new COMException(message, result);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    private readonly record struct DesktopPidl(IntPtr Pidl, string DisplayName);

    private sealed class DesktopViewContext : IDisposable
    {
        private readonly object _viewObject;
        private readonly object _browserObject;
        private readonly object _desktopDispatch;
        private readonly object _shellWindows;
        private bool _disposed;

        public DesktopViewContext(
            IFolderViewNative view,
            object folder,
            object shellWindows,
            object desktopDispatch,
            object browserObject,
            object viewObject)
        {
            View = view;
            Folder = folder;
            _shellWindows = shellWindows;
            _desktopDispatch = desktopDispatch;
            _browserObject = browserObject;
            _viewObject = viewObject;
        }

        public IFolderViewNative View { get; }

        public object Folder { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ReleaseComObject(Folder);
            ReleaseComObject(_viewObject);
            ReleaseComObject(_browserObject);
            ReleaseComObject(_desktopDispatch);
            ReleaseComObject(_shellWindows);
        }
    }

    private static class NativeMethods
    {
        [DllImport("shell32.dll")]
        internal static extern int SHCreateItemWithParent(
            IntPtr parentPidl,
            [MarshalAs(UnmanagedType.Interface)] object parentFolder,
            IntPtr childPidl,
            ref Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemNative shellItem);
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePoint
{
    public NativePoint(int x, int y)
    {
        X = x;
        Y = y;
    }

    public int X;
    public int Y;
}

[ComImport]
[Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IServiceProviderNative
{
    [PreserveSig]
    int QueryService(ref Guid serviceId, ref Guid interfaceId, out IntPtr service);
}

[ComImport]
[Guid("000214E2-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellBrowserNative
{
    [PreserveSig] int GetWindow(out IntPtr window);
    [PreserveSig] int ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool enterMode);
    [PreserveSig] int InsertMenusSB(IntPtr sharedMenu, IntPtr menuWidths);
    [PreserveSig] int SetMenuSB(IntPtr sharedMenu, IntPtr reservedMenu, IntPtr activeWindow);
    [PreserveSig] int RemoveMenusSB(IntPtr sharedMenu);
    [PreserveSig] int SetStatusTextSB([MarshalAs(UnmanagedType.LPWStr)] string statusText);
    [PreserveSig] int EnableModelessSB([MarshalAs(UnmanagedType.Bool)] bool enable);
    [PreserveSig] int TranslateAcceleratorSB(IntPtr message, ushort commandId);
    [PreserveSig] int BrowseObject(IntPtr pidl, uint flags);
    [PreserveSig] int GetViewStateStream(uint mode, out IntPtr stream);
    [PreserveSig] int GetControlWindow(uint controlId, out IntPtr window);
    [PreserveSig] int SendControlMsg(uint controlId, uint message, IntPtr wParam, IntPtr lParam, out IntPtr result);
    [PreserveSig] int QueryActiveShellView(out IntPtr shellView);
    [PreserveSig] int OnViewWindowActive(IntPtr shellView);
    [PreserveSig] int SetToolbarItems(IntPtr buttons, uint buttonCount, uint flags);
}

[ComImport]
[Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFolderViewNative
{
    [PreserveSig] int GetCurrentViewMode(out uint viewMode);
    [PreserveSig] int SetCurrentViewMode(uint viewMode);
    [PreserveSig] int GetFolder(ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out object folder);
    [PreserveSig] int Item(int itemIndex, out IntPtr childPidl);
    [PreserveSig] int ItemCount(uint flags, out int itemCount);
    [PreserveSig] int Items(uint flags, ref Guid interfaceId, out IntPtr items);
    [PreserveSig] int GetSelectionMarkedItem(out int itemIndex);
    [PreserveSig] int GetFocusedItem(out int itemIndex);
    [PreserveSig] int GetItemPosition(IntPtr childPidl, out NativePoint point);
    [PreserveSig] int GetSpacing(ref NativePoint point);
    [PreserveSig] int GetDefaultSpacing(ref NativePoint point);
    [PreserveSig] int GetAutoArrange();
    [PreserveSig] int SelectItem(int itemIndex, uint flags);

    [PreserveSig]
    int SelectAndPositionItems(
        uint itemCount,
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[] childPidls,
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] NativePoint[] positions,
        uint flags);
}

[ComImport]
[Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItemNative
{
    [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid handlerId, ref Guid interfaceId, out IntPtr result);
    [PreserveSig] int GetParent([MarshalAs(UnmanagedType.Interface)] out IShellItemNative parent);
    [PreserveSig] int GetDisplayName(uint nameKind, out IntPtr name);
    [PreserveSig] int GetAttributes(uint mask, out uint attributes);
    [PreserveSig] int Compare([MarshalAs(UnmanagedType.Interface)] IShellItemNative other, uint hint, out int order);
}
