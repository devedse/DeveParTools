using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;

namespace DeveParTools.App.Services;

/// <summary>
/// Receives Explorer's "Create recovery set" for any number of selected files and folders in one go, so a
/// multi-selection starts the app once.
/// </summary>
/// <remarks>
/// <para><b>Why.</b> A context menu entry with a command line (<c>deveparui.exe --create "%1"</c>) is run by Explorer once
/// per selected item, one after the other, and Explorer does not respond until it has started them all: 23 selected
/// episodes are 23 program starts in a row (<see cref="CreateRequestCollector"/> then gathers them into one window).
/// Microsoft's way for a verb that takes many items is a drop target: the verb's <c>DropTarget</c> key names a COM class,
/// and Explorer creates that object once and drops all selected items on it, as if they were dragged onto the
/// program.</para>
/// <para><b>How.</b> The class is registered for the current user with this program as its server
/// (<c>HKCU\Software\Classes\CLSID\{…}\LocalServer32</c>, see <see cref="ExplorerContextMenu"/>):</para>
/// <code>
/// right-click → Create recovery set
///   Explorer: CoCreateInstance(Clsid) → COM starts "deveparui.exe -Embedding"
///   deveparui: CoRegisterClassObject(Clsid, factory)           (ReceiveDrop)
///   Explorer:  factory.CreateInstance(IDropTarget) → DragEnter, Drop(data object with every selected item)
///   deveparui: reads the paths from the data object (CF_HDROP), revokes the class, opens the window with them
/// </code>
/// <para>The class is registered single-use from a multithreaded apartment: Explorer's calls arrive on COM's own
/// threads while the main thread waits, and the next right-click starts a new process (a new window), as before. The
/// COM interfaces are source-generated (<see cref="GeneratedComInterfaceAttribute"/>), which works in the trimmed
/// build.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static partial class ExplorerDropTarget
{
    /// <summary>The COM class Explorer creates for "Create recovery set".</summary>
    public static readonly Guid Clsid = new(ExplorerContextMenu.DropTargetClsid);

    /// <summary>The argument COM adds when it starts the program as a server.</summary>
    public static bool IsServerStart(IReadOnlyList<string> args)
        => args.Any(a => a.Equals("-Embedding", StringComparison.OrdinalIgnoreCase) || a.Equals("/Embedding", StringComparison.OrdinalIgnoreCase));

    internal static readonly StrategyBasedComWrappers Wrappers = new();

    private const int ClassNotAggregatable = unchecked((int)0x80040110);
    private const int DropEffectCopy = 1;
    private const ushort ClipboardHDrop = 15;

    /// <summary>
    /// Serves <see cref="Clsid"/> (or <paramref name="clsid"/>) until Explorer has dropped its items on it, at most
    /// <paramref name="wait"/>.
    /// </summary>
    /// <returns>The dropped paths, or null when nothing arrived in time.</returns>
    public static IReadOnlyList<string>? ReceiveDrop(TimeSpan wait, Guid? clsid = null)
    {
        IReadOnlyList<string>? received = null;
        using var dropped = new ManualResetEventSlim();
        var server = new Thread(() =>
        {
            try
            {
                CoInitializeEx(0, 0 /* COINIT_MULTITHREADED */); // S_FALSE when the thread already is in the MTA
                nint factory = Wrappers.GetOrCreateComInterfaceForObject(new ClassFactory(paths =>
                {
                    received = paths;
                    dropped.Set();
                }), CreateComInterfaceFlags.None);
                try
                {
                    Marshal.ThrowExceptionForHR(CoRegisterClassObject(clsid ?? Clsid, factory, 4 /* CLSCTX_LOCAL_SERVER */, 0 /* REGCLS_SINGLEUSE */, out uint cookie));
                    try
                    {
                        dropped.Wait(wait);
                    }
                    finally
                    {
                        CoRevokeClassObject(cookie);
                    }
                }
                finally
                {
                    Marshal.Release(factory);
                }
            }
            catch (Exception e) when (e is COMException or InvalidOperationException)
            {
                // Could not serve the class (e.g. started with -Embedding by hand): nothing was dropped.
            }
        })
        {
            IsBackground = true,
            Name = "Explorer drop target",
        };
        server.SetApartmentState(ApartmentState.MTA);
        server.Start();
        server.Join();
        return received;
    }

    /// <summary>The file and folder paths in a data object (its CF_HDROP: the format Explorer and drag and drop use).</summary>
    internal static IReadOnlyList<string> ReadPaths(nint dataObject)
    {
        var data = (IDataObject)Wrappers.GetOrCreateObjectForComInstance(dataObject, CreateObjectFlags.None);
        var format = new FormatEtc { Format = ClipboardHDrop, Aspect = 1 /* DVASPECT_CONTENT */, Index = -1, Tymed = 1 /* TYMED_HGLOBAL */ };
        Marshal.ThrowExceptionForHR(data.GetData(in format, out var medium));
        try
        {
            return DropFiles(medium.Handle);
        }
        finally
        {
            ReleaseStgMedium(ref medium);
        }
    }

    /// <summary>The paths in an HDROP (a DROPFILES block), read with DragQueryFile.</summary>
    internal static unsafe List<string> DropFiles(nint drop)
    {
        uint count = DragQueryFile(drop, uint.MaxValue, null, 0);
        var paths = new List<string>((int)count);
        for (uint i = 0; i < count; i++)
        {
            uint length = DragQueryFile(drop, i, null, 0);
            var buffer = new char[length + 1];
            fixed (char* p = buffer)
            {
                DragQueryFile(drop, i, p, length + 1);
            }

            paths.Add(new string(buffer, 0, (int)length));
        }

        return paths;
    }

    /// <summary>Makes the drop target for Explorer.</summary>
    [GeneratedComClass]
    internal sealed partial class ClassFactory(Action<IReadOnlyList<string>> received) : IClassFactory
    {
        public int CreateInstance(nint outer, in Guid iid, out nint instance)
        {
            instance = 0;
            if (outer != 0)
            {
                return ClassNotAggregatable;
            }

            nint unknown = Wrappers.GetOrCreateComInterfaceForObject(new DropTarget(received), CreateComInterfaceFlags.None);
            try
            {
                return Marshal.QueryInterface(unknown, in iid, out instance);
            }
            finally
            {
                Marshal.Release(unknown);
            }
        }

        public int LockServer(int doLock) => 0;
    }

    /// <summary>Accepts what Explorer drops (as a copy) and hands the paths over.</summary>
    [GeneratedComClass]
    internal sealed partial class DropTarget(Action<IReadOnlyList<string>> received) : IDropTarget
    {
        public int DragEnter(nint dataObject, int keyState, PointL point, ref int effect)
        {
            effect = DropEffectCopy;
            return 0;
        }

        public int DragOver(int keyState, PointL point, ref int effect)
        {
            effect = DropEffectCopy;
            return 0;
        }

        public int DragLeave() => 0;

        public int Drop(nint dataObject, int keyState, PointL point, ref int effect)
        {
            try
            {
                received(ReadPaths(dataObject));
                effect = DropEffectCopy;
                return 0;
            }
            catch (Exception e)
            {
                return e.HResult;
            }
        }
    }

    [GeneratedComInterface]
    [Guid("00000001-0000-0000-C000-000000000046")]
    internal partial interface IClassFactory
    {
        [PreserveSig]
        int CreateInstance(nint outer, in Guid iid, out nint instance);

        [PreserveSig]
        int LockServer(int doLock);
    }

    [GeneratedComInterface]
    [Guid("00000122-0000-0000-C000-000000000046")]
    internal partial interface IDropTarget
    {
        [PreserveSig]
        int DragEnter(nint dataObject, int keyState, PointL point, ref int effect);

        [PreserveSig]
        int DragOver(int keyState, PointL point, ref int effect);

        [PreserveSig]
        int DragLeave();

        [PreserveSig]
        int Drop(nint dataObject, int keyState, PointL point, ref int effect);
    }

    /// <summary>The first method of IDataObject (the only one used; the vtable order is what matters).</summary>
    [GeneratedComInterface]
    [Guid("0000010E-0000-0000-C000-000000000046")]
    internal partial interface IDataObject
    {
        [PreserveSig]
        int GetData(in FormatEtc format, out StgMedium medium);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PointL
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FormatEtc
    {
        public ushort Format;
        public nint TargetDevice;
        public int Aspect;
        public int Index;
        public int Tymed;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct StgMedium
    {
        public int Tymed;
        public nint Handle;
        public nint ReleaseUnknown;
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, int flags);

    [LibraryImport("ole32.dll")]
    private static partial int CoRegisterClassObject(in Guid clsid, nint unknown, int context, int flags, out uint cookie);

    [LibraryImport("ole32.dll")]
    private static partial int CoRevokeClassObject(uint cookie);

    [LibraryImport("ole32.dll")]
    private static partial void ReleaseStgMedium(ref StgMedium medium);

    [LibraryImport("shell32.dll", EntryPoint = "DragQueryFileW")]
    private static unsafe partial uint DragQueryFile(nint drop, uint index, char* file, uint length);
}
