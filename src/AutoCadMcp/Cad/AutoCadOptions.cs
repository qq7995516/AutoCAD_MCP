namespace AutoCadMcp.Cad;

/// <summary>
/// Options for connecting to AutoCAD, including portable/"green" installs.
/// Set env AUTOCAD_EXE to the full path of acad.exe when ProgID is not registered.
/// </summary>
public sealed class AutoCadOptions
{
    /// <summary>Full path to acad.exe (e.g. D:\CAD2008\acad.exe).</summary>
    public string? AcadExePath { get; set; }

    /// <summary>Seconds to wait for COM ROT after launching acad.exe.</summary>
    public int LaunchTimeoutSeconds { get; set; } = 60;

    public static AutoCadOptions FromEnvironment()
    {
        var exe = Environment.GetEnvironmentVariable("AUTOCAD_EXE");
        var timeoutRaw = Environment.GetEnvironmentVariable("AUTOCAD_LAUNCH_TIMEOUT_SECONDS");
        var timeout = 60;
        if (int.TryParse(timeoutRaw, out var parsed) && parsed > 0)
        {
            timeout = parsed;
        }

        return new AutoCadOptions
        {
            AcadExePath = string.IsNullOrWhiteSpace(exe) ? null : exe.Trim().Trim('"'),
            LaunchTimeoutSeconds = timeout,
        };
    }
}
