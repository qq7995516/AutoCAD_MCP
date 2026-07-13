using System.Reflection;
using System.Runtime.InteropServices;

namespace AutoCadMcp.Com;

internal static class ComHelper
{
    private const int RpcECallRejected = unchecked((int)0x80010001);
    private const int RpcEServerCallRetryLater = unchecked((int)0x8001010A);
    private const int MaxBusyRetries = 8;
    private const int BusyRetryDelayMs = 250;

    // OBJID_NATIVEOM — returns the application's native object model (IDispatch).
    private const uint ObjIdNativeOm = 0xFFFFFFF0;
    private static readonly Guid IidIDispatch = new("00020400-0000-0000-C000-000000000046");

    public static object CreateInstance(string progId)
    {
        var type = Type.GetTypeFromProgID(progId, throwOnError: false)
            ?? throw new InvalidOperationException($"ProgID '{progId}' not found. Is AutoCAD 2008 installed?");
        return Activator.CreateInstance(type)
            ?? throw new InvalidOperationException($"Failed to create COM instance for '{progId}'.");
    }

    public static object? GetActiveObject(string progId)
    {
        try
        {
            var hr = NativeMethods.CLSIDFromProgID(progId, out var clsid);
            if (hr < 0)
            {
                return null;
            }

            hr = NativeMethods.GetActiveObject(ref clsid, IntPtr.Zero, out var obj);
            return hr >= 0 ? obj : null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    /// <summary>
    /// Attach to a specific AutoCAD process via HWND + AccessibleObjectFromWindow(OBJID_NATIVEOM).
    /// </summary>
    public static object? GetApplicationFromProcess(int pid)
    {
        var hwnd = FindTopLevelWindow(pid);
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        var iid = IidIDispatch;
        var hr = NativeMethods.AccessibleObjectFromWindow(hwnd, ObjIdNativeOm, ref iid, out var obj);
        if (hr < 0 || obj is null)
        {
            return null;
        }

        // NativeOM may return Application directly, or a Document — normalize to Application.
        try
        {
            var app = GetProperty(obj, "Application");
            if (app is not null)
            {
                if (!ReferenceEquals(app, obj))
                {
                    Release(obj);
                }

                return app;
            }
        }
        catch
        {
            // Already the Application, or property not available.
        }

        return obj;
    }

    public static IntPtr FindTopLevelWindow(int pid)
    {
        IntPtr found = IntPtr.Zero;
        NativeMethods.EnumWindows((hwnd, lParam) =>
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out var windowPid);
            if (windowPid != (uint)pid)
            {
                return true;
            }

            if (!NativeMethods.IsWindowVisible(hwnd))
            {
                return true;
            }

            // Prefer the owner-less top-level frame (AutoCAD main window).
            if (NativeMethods.GetWindow(hwnd, NativeMethods.GwOwner) != IntPtr.Zero)
            {
                return true;
            }

            found = hwnd;
            return false; // stop
        }, IntPtr.Zero);

        return found;
    }

    public static object? GetProperty(object target, string name)
        => InvokeMemberWithRetry(target, name, BindingFlags.GetProperty, args: null);

    public static void SetProperty(object target, string name, object? value)
        => InvokeMemberWithRetry(target, name, BindingFlags.SetProperty, args: [value]);

    public static object? Invoke(object target, string name, params object?[] args)
        => InvokeMemberWithRetry(target, name, BindingFlags.InvokeMethod, args);

    private static object? InvokeMemberWithRetry(
        object target,
        string name,
        BindingFlags flags,
        object?[]? args)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < MaxBusyRetries; attempt++)
        {
            try
            {
                return target.GetType().InvokeMember(
                    name,
                    flags,
                    binder: null,
                    target,
                    args,
                    culture: null);
            }
            catch (Exception ex) when (IsBusyComException(ex))
            {
                last = ex;
                Thread.Sleep(BusyRetryDelayMs);
            }
        }

        throw last ?? new InvalidOperationException($"COM call '{name}' failed after retries.");
    }

    public static bool IsBusyComException(Exception ex)
    {
        var com = UnwrapComException(ex);
        if (com is null)
        {
            return false;
        }

        return com.ErrorCode is RpcECallRejected or RpcEServerCallRetryLater;
    }

    public static COMException? UnwrapComException(Exception ex)
    {
        for (Exception? cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (cur is COMException com)
            {
                return com;
            }
        }

        return null;
    }

    public static double[] Point(double x, double y, double z = 0)
        => [x, y, z];

    public static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            try
            {
                Marshal.ReleaseComObject(comObject);
            }
            catch (ArgumentException)
            {
                // Already released / not a valid RCW.
            }
        }
    }

    private static class NativeMethods
    {
        public const uint GwOwner = 4;

        [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
        public static extern int CLSIDFromProgID(string lpszProgID, out Guid pclsid);

        [DllImport("oleaut32.dll")]
        public static extern int GetActiveObject(ref Guid rclsid, IntPtr pvReserved,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);

        [DllImport("oleacc.dll")]
        public static extern int AccessibleObjectFromWindow(
            IntPtr hwnd,
            uint dwObjectID,
            ref Guid riid,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppvObject);

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
    }
}
