using System.Runtime.InteropServices;

namespace AutoCadMcp.Com;

internal static class ComHelper
{
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

    public static object? GetProperty(object target, string name)
        => target.GetType().InvokeMember(
            name,
            System.Reflection.BindingFlags.GetProperty,
            binder: null,
            target,
            args: null,
            culture: null);

    public static void SetProperty(object target, string name, object? value)
        => target.GetType().InvokeMember(
            name,
            System.Reflection.BindingFlags.SetProperty,
            binder: null,
            target,
            args: [value],
            culture: null);

    public static object? Invoke(object target, string name, params object?[] args)
        => target.GetType().InvokeMember(
            name,
            System.Reflection.BindingFlags.InvokeMethod,
            binder: null,
            target,
            args,
            culture: null);

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
        [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
        public static extern int CLSIDFromProgID(string lpszProgID, out Guid pclsid);

        [DllImport("oleaut32.dll")]
        public static extern int GetActiveObject(ref Guid rclsid, IntPtr pvReserved,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);
    }
}
