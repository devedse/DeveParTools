using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using DeveParTools.App.Services;

/// <summary>
/// Windows-only helpers for the Explorer checks: dropping paths on the drop target through COM (as Explorer does), and
/// invoking the real "Create recovery set" verb through the Windows shell.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class ExplorerShellTests
{
    /// <summary>Where the program started by COM (or by the command line fallback) writes what it received.</summary>
    public static string DropLog => Path.Combine(AppContext.BaseDirectory, "explorer-drop.txt");

    public static string CommandLog => Path.Combine(AppContext.BaseDirectory, "explorer-command.txt");

    private static readonly Guid IDropTargetId = new("00000122-0000-0000-C000-000000000046");
    private static readonly Guid IContextMenuId = new("000214E4-0000-0000-C000-000000000046");

    /// <summary>
    /// The class factory and drop target through their COM interfaces, as Explorer uses them: CreateInstance(IDropTarget),
    /// DragEnter, Drop with a data object holding <paramref name="paths"/> (CF_HDROP).
    /// </summary>
    public static IReadOnlyList<string>? DropThroughCom(IReadOnlyList<string> paths)
    {
        IReadOnlyList<string>? received = null;
        var wrappers = ExplorerDropTarget.Wrappers;
        nint factoryUnknown = wrappers.GetOrCreateComInterfaceForObject(new ExplorerDropTarget.ClassFactory(p => received = p), CreateComInterfaceFlags.None);
        var factory = (ExplorerDropTarget.IClassFactory)wrappers.GetOrCreateObjectForComInstance(factoryUnknown, CreateObjectFlags.None);
        Marshal.ThrowExceptionForHR(factory.CreateInstance(0, IDropTargetId, out nint targetPointer));
        var target = (ExplorerDropTarget.IDropTarget)wrappers.GetOrCreateObjectForComInstance(targetPointer, CreateObjectFlags.None);
        nint data = wrappers.GetOrCreateComInterfaceForObject(new FakeDataObject(paths), CreateComInterfaceFlags.None);
        int effect = 7;
        Marshal.ThrowExceptionForHR(target.DragEnter(data, 0, default, ref effect));
        Marshal.ThrowExceptionForHR(target.Drop(data, 0, default, ref effect));
        if (effect != 1)
        {
            throw new InvalidOperationException("The drop target should accept a copy, effect " + effect);
        }

        Marshal.Release(data);
        Marshal.Release(targetPointer);
        Marshal.Release(factoryUnknown);
        return received;
    }

    /// <summary>
    /// Selects <paramref name="paths"/> and invokes the "DeveParTools.Create" verb through the shell (the shell's own
    /// context menu for the items, as Explorer builds it), on an STA thread like Explorer's.
    /// </summary>
    public static void InvokeCreateVerb(IReadOnlyList<string> paths)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                InvokeOnThisThread(paths);
            }
            catch (Exception e)
            {
                failure = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null)
        {
            throw new InvalidOperationException("Invoking the verb through the shell failed: " + failure.Message, failure);
        }
    }

    private static unsafe void InvokeOnThisThread(IReadOnlyList<string> paths)
    {
        var wrappers = ExplorerDropTarget.Wrappers;
        var pidls = new nint[paths.Count];
        for (int i = 0; i < paths.Count; i++)
        {
            Marshal.ThrowExceptionForHR(SHParseDisplayName(paths[i], 0, out pidls[i], 0, out _));
        }

        Marshal.ThrowExceptionForHR(SHCreateShellItemArrayFromIDLists((uint)pidls.Length, pidls, out nint arrayPointer));
        var items = (IShellItemArray)wrappers.GetOrCreateObjectForComInstance(arrayPointer, CreateObjectFlags.None);
        var bhidSfUiObject = new Guid("3981E225-F559-11D3-8E3A-00C04F6837D5");
        Marshal.ThrowExceptionForHR(items.BindToHandler(0, in bhidSfUiObject, IContextMenuId, out nint menuPointer));
        var menu = (IContextMenu)wrappers.GetOrCreateObjectForComInstance(menuPointer, CreateObjectFlags.None);
        nint popup = CreatePopupMenu();
        nint verb = Marshal.StringToHGlobalAnsi(ExplorerContextMenu.CreateVerb);
        try
        {
            int hr = menu.QueryContextMenu(popup, 0, 1, 0x7FFF, 0 /* CMF_NORMAL */);
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            var info = new CommandInfo
            {
                Size = sizeof(CommandInfo),
                Mask = 0x400 | 0x100, // CMIC_MASK_FLAG_NO_UI | CMIC_MASK_NOASYNC: no message boxes, return when done
                Verb = verb,
                Show = 1,
            };
            Marshal.ThrowExceptionForHR(menu.InvokeCommand((nint)(&info)));
        }
        finally
        {
            Marshal.FreeHGlobal(verb);
            DestroyMenu(popup);
            Marshal.Release(menuPointer);
            Marshal.Release(arrayPointer);
            foreach (var pidl in pidls)
            {
                Marshal.FreeCoTaskMem(pidl);
            }
        }
    }

    /// <summary>A data object with one format: CF_HDROP with the given paths, like Explorer's for a selection of files.</summary>
    [GeneratedComClass]
    internal sealed partial class FakeDataObject(IReadOnlyList<string> paths) : ExplorerDropTarget.IDataObject
    {
        public int GetData(in ExplorerDropTarget.FormatEtc format, out ExplorerDropTarget.StgMedium medium)
        {
            medium = default;
            if (format.Format != 15 || (format.Tymed & 1) == 0)
            {
                return unchecked((int)0x80040064); // DV_E_FORMATETC
            }

            medium = new ExplorerDropTarget.StgMedium { Tymed = 1, Handle = HDrop(paths) };
            return 0;
        }

        /// <summary>A DROPFILES block in global memory: the 20-byte header (offset of the list, wide characters), then the
        /// paths, each ending in a 0, and one more 0.</summary>
        private static unsafe nint HDrop(IReadOnlyList<string> paths)
        {
            string list = string.Concat(paths.Select(p => p + "\0")) + "\0";
            int size = 20 + list.Length * 2;
            nint handle = GlobalAlloc(0x0042 /* GMEM_MOVEABLE | GMEM_ZEROINIT */, (nuint)size);
            byte* block = (byte*)GlobalLock(handle);
            *(int*)block = 20;
            *(int*)(block + 16) = 1;
            fixed (char* text = list)
            {
                Buffer.MemoryCopy(text, block + 20, size - 20, list.Length * 2);
            }

            GlobalUnlock(handle);
            return handle;
        }
    }

    [GeneratedComInterface]
    [Guid("B63EA76D-1F85-456F-A19C-48159EFA858B")]
    internal partial interface IShellItemArray
    {
        [PreserveSig]
        int BindToHandler(nint bindContext, in Guid handler, in Guid iid, out nint result);
    }

    [GeneratedComInterface]
    [Guid("000214E4-0000-0000-C000-000000000046")]
    internal partial interface IContextMenu
    {
        [PreserveSig]
        int QueryContextMenu(nint menu, uint indexMenu, uint firstCommand, uint lastCommand, uint flags);

        [PreserveSig]
        int InvokeCommand(nint info);
    }

    /// <summary>CMINVOKECOMMANDINFO.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CommandInfo
    {
        public int Size;
        public int Mask;
        public nint Window;
        public nint Verb;
        public nint Parameters;
        public nint Directory;
        public int Show;
        public int HotKey;
        public nint Icon;
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHParseDisplayName(string name, nint bindContext, out nint pidl, uint attributesIn, out uint attributesOut);

    [LibraryImport("shell32.dll")]
    private static partial int SHCreateShellItemArrayFromIDLists(uint count, nint[] pidls, out nint array);

    [LibraryImport("user32.dll")]
    private static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(nint menu);

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalLock(nint memory);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(nint memory);
}
