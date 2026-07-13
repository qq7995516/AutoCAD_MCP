using System.Diagnostics;
using System.Runtime.InteropServices;
using AutoCadMcp.Com;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AutoCadMcp.Cad;

/// <summary>
/// Late-bound COM session for AutoCAD 2008 (ProgID AutoCAD.Application.17).
/// Binds a specific process by PID via HWND/NativeOM when multiple instances run.
/// Also supports portable installs: launch acad.exe by path, then attach.
/// </summary>
public sealed class AutoCadSession : IDisposable
{
    // Prefer the registered 2008 ProgID first; generic may be missing on some installs.
    private static readonly string[] ProgIds =
    [
        "AutoCAD.Application.17", // 2008
        "AutoCAD.Application",
        "AutoCAD.Application.25", // 2025
        "AutoCAD.Application.24", // 2024
        "AutoCAD.Application.23", // 2019-2021 family varies
        "AutoCAD.Application.22",
        "AutoCAD.Application.21",
        "AutoCAD.Application.20",
        "AutoCAD.Application.19",
        "AutoCAD.Application.18", // 2010
    ];

    private readonly StaDispatcher _sta;
    private readonly ILogger<AutoCadSession> _logger;
    private readonly AutoCadOptions _options;
    private object? _app;
    private bool _startedByUs;
    private string? _discoveredExePath;
    private int? _boundPid;

    public AutoCadSession(StaDispatcher sta, IOptions<AutoCadOptions> options, ILogger<AutoCadSession> logger)
    {
        _sta = sta;
        _logger = logger;
        _options = options.Value;
    }

    public Task<string> ConnectAsync(bool visible = true, string? acadExePath = null, int? pid = null, CancellationToken cancellationToken = default)
        => _sta.InvokeAsync(() => ConnectCore(visible, acadExePath, pid), cancellationToken);

    public Task<string> DiscoverProcessAsync(CancellationToken cancellationToken = default)
        => _sta.InvokeAsync(DiscoverProcessCore, cancellationToken);

    public Task<string> GetStatusAsync(CancellationToken cancellationToken = default)
        => _sta.InvokeAsync(GetStatusCore, cancellationToken);

    public Task<string> NewDrawingAsync(CancellationToken cancellationToken = default)
        => _sta.InvokeAsync(() => GuardCom(NewDrawingCore), cancellationToken);

    public Task<string> OpenDrawingAsync(string path, CancellationToken cancellationToken = default)
        => _sta.InvokeAsync(() => GuardCom(() => OpenDrawingCore(path)), cancellationToken);

    public Task<string> SaveDrawingAsync(string? path, CancellationToken cancellationToken = default)
        => _sta.InvokeAsync(() => GuardCom(() => SaveDrawingCore(path)), cancellationToken);

    public Task<string> DrawLineAsync(double x1, double y1, double x2, double y2, string? layer, CancellationToken cancellationToken = default)
        => _sta.InvokeAsync(() => GuardCom(() => DrawLineCore(x1, y1, x2, y2, layer)), cancellationToken);

    public Task<string> DrawCircleAsync(double cx, double cy, double radius, string? layer, CancellationToken cancellationToken = default)
        => _sta.InvokeAsync(() => GuardCom(() => DrawCircleCore(cx, cy, radius, layer)), cancellationToken);

    public Task<string> DrawArcAsync(double cx, double cy, double radius, double startAngleDeg, double endAngleDeg, string? layer, CancellationToken cancellationToken = default)
        => _sta.InvokeAsync(() => GuardCom(() => DrawArcCore(cx, cy, radius, startAngleDeg, endAngleDeg, layer)), cancellationToken);

    public Task<string> DrawRectangleAsync(double x1, double y1, double x2, double y2, string? layer, CancellationToken cancellationToken = default)
        => _sta.InvokeAsync(() => GuardCom(() => DrawRectangleCore(x1, y1, x2, y2, layer)), cancellationToken);

    public Task<string> DrawPolylineAsync(IReadOnlyList<(double X, double Y)> points, bool closed, string? layer, CancellationToken cancellationToken = default)
        => _sta.InvokeAsync(() => GuardCom(() => DrawPolylineCore(points, closed, layer)), cancellationToken);

    public Task<string> DrawTextAsync(double x, double y, double height, string text, double rotationDeg, string? layer, CancellationToken cancellationToken = default)
        => _sta.InvokeAsync(() => GuardCom(() => DrawTextCore(x, y, height, text, rotationDeg, layer)), cancellationToken);

    public Task<string> CreateLayerAsync(string name, short colorIndex, CancellationToken cancellationToken = default)
        => _sta.InvokeAsync(() => GuardCom(() => CreateLayerCore(name, colorIndex)), cancellationToken);

    public Task<string> SetCurrentLayerAsync(string name, CancellationToken cancellationToken = default)
        => _sta.InvokeAsync(() => GuardCom(() => SetCurrentLayerCore(name)), cancellationToken);

    public Task<string> ZoomExtentsAsync(CancellationToken cancellationToken = default)
        => _sta.InvokeAsync(() => GuardCom(ZoomExtentsCore), cancellationToken);

    public Task<string> SendCommandAsync(string command, CancellationToken cancellationToken = default)
        => _sta.InvokeAsync(() => GuardCom(() => SendCommandCore(command)), cancellationToken);

    public Task<string> ListEntitiesAsync(int maxCount, CancellationToken cancellationToken = default)
        => _sta.InvokeAsync(() => GuardCom(() => ListEntitiesCore(maxCount)), cancellationToken);

    public Task<string> EraseByHandleAsync(string handle, CancellationToken cancellationToken = default)
        => _sta.InvokeAsync(() => GuardCom(() => EraseByHandleCore(handle)), cancellationToken);

    private string GuardCom(Func<string> action)
    {
        try
        {
            return action();
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex) when (ComHelper.IsBusyComException(ex) || ComHelper.UnwrapComException(ex) is not null)
        {
            throw new InvalidOperationException($"{DiagnoseBoundPid()} Detail: {ex.Message}", ex);
        }
    }

    private string ConnectCore(bool visible, string? acadExePath, int? requestedPid)
    {
        if (_app is not null && IsAlive(_app) && MatchesBoundPid(requestedPid))
        {
            ComHelper.SetProperty(_app, "Visible", visible);
            return FormatAppInfo("Already connected");
        }

        // Drop stale RCW before rebinding.
        if (_app is not null)
        {
            ComHelper.Release(_app);
            _app = null;
        }

        var running = ListRunningAcadProcesses();
        CacheExeFromProcesses(running);
        if (!string.IsNullOrWhiteSpace(acadExePath) && File.Exists(acadExePath.Trim().Trim('"')))
        {
            _discoveredExePath = Path.GetFullPath(acadExePath.Trim().Trim('"'));
        }

        object? app = null;
        string? usedProgId = null;
        var connectMode = "unknown";
        var processSummary = FormatProcessSummary(running);

        _logger.LogInformation("CAD process check: {Summary}", processSummary);

        var targetPid = ResolveTargetPid(running, requestedPid);
        if (targetPid is not null)
        {
            app = WaitForAppFromPid(targetPid.Value, Math.Min(15, _options.LaunchTimeoutSeconds));
            if (app is not null)
            {
                connectMode = "attach-by-pid";
                _startedByUs = false;
                _boundPid = targetPid;
                usedProgId = "(HWND/NativeOM)";
                _logger.LogInformation("Attached to AutoCAD PID={Pid} via HWND/NativeOM", targetPid);
            }
            else
            {
                // Single-instance fallback: ROT GetActiveObject (never CreateInstance while process exists).
                if (running.Count == 1)
                {
                    (app, usedProgId) = TryGetActiveApp();
                    if (app is null)
                    {
                        (app, usedProgId) = WaitForActiveApp(Math.Min(15, _options.LaunchTimeoutSeconds));
                    }

                    if (app is not null)
                    {
                        connectMode = "attach-running-rot";
                        _startedByUs = false;
                        _boundPid = running[0].Pid;
                        _logger.LogInformation(
                            "HWND attach failed; attached via ROT {ProgId} to PID={Pid}",
                            usedProgId,
                            _boundPid);
                    }
                }

                if (app is null)
                {
                    throw new InvalidOperationException(
                        $"Detected AutoCAD PID={targetPid} but COM attach failed. " +
                        $"Processes: {processSummary}. " +
                        "Wait until CAD finishes starting, then retry cad_connect. " +
                        "Do not use CreateInstance while a process is already running.");
                }
            }
        }
        else
        {
            // No process → try ProgID (official install), then launch exe.
            foreach (var progId in ProgIds)
            {
                try
                {
                    app = ComHelper.CreateInstance(progId);
                    usedProgId = progId;
                    connectMode = "progid-create";
                    _startedByUs = true;
                    _logger.LogInformation("No process found; started AutoCAD via {ProgId}", progId);
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to create {ProgId}", progId);
                }
            }

            if (app is null)
            {
                var exePath = ResolveAcadExePath(acadExePath);
                if (exePath is null)
                {
                    throw new InvalidOperationException(
                        "No running AutoCAD process, ProgID create failed, and acad.exe path is unknown. " +
                        "Start CAD manually once, call cad_discover_process (or cad_connect after CAD is open) to cache the path, " +
                        "or set AUTOCAD_EXE.");
                }

                _logger.LogInformation("No process found; launching acad.exe: {Path}", exePath);
                LaunchAcadExe(exePath);
                _startedByUs = true;
                running = ListRunningAcadProcesses();
                CacheExeFromProcesses(running);
                if (running.Count == 0)
                {
                    throw new InvalidOperationException($"Started '{exePath}' but no acad process appeared.");
                }

                var launchedPid = running.OrderByDescending(p => p.Pid).First().Pid;
                app = WaitForAppFromPid(launchedPid, _options.LaunchTimeoutSeconds);
                if (app is null)
                {
                    (app, usedProgId) = WaitForActiveApp(_options.LaunchTimeoutSeconds);
                }

                if (app is not null)
                {
                    connectMode = "exe-launch-attach";
                    _boundPid = launchedPid;
                    usedProgId ??= "(HWND/NativeOM)";
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Started '{exePath}' but COM never appeared. This green build likely has no Automation support.");
                }
            }
            else
            {
                // ProgID create started a process — bind its PID once visible.
                Thread.Sleep(1500);
                running = ListRunningAcadProcesses();
                CacheExeFromProcesses(running);
                if (running.Count > 0)
                {
                    _boundPid = running.OrderByDescending(p => p.Pid).First().Pid;
                }
            }
        }

        _app = app;
        _discoveredExePath ??= TryGetExePathFromRunningProcesses();
        try
        {
            ComHelper.SetProperty(_app, "Visible", visible);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set Visible={Visible}", visible);
        }

        if (_startedByUs)
        {
            Thread.Sleep(1500);
        }

        EnsureDocument();

        if (_boundPid is null or 0)
        {
            _boundPid = TryReadPidFromApp(_app);
            if (_boundPid is null or 0)
            {
                var still = ListRunningAcadProcesses();
                _boundPid = still.Count >= 1 ? still[0].Pid : null;
            }
        }

        var exeInfo = _discoveredExePath is not null ? $", Exe={_discoveredExePath}" : "";
        return $"ProcessCheck: {processSummary}. " +
               FormatAppInfo($"Connected ({connectMode}) via {usedProgId}") +
               exeInfo;
    }

    private int? ResolveTargetPid(IReadOnlyList<AcadProcessInfo> running, int? requestedPid)
    {
        if (running.Count == 0)
        {
            return null;
        }

        var preferred = requestedPid ?? _boundPid;
        if (preferred is int pid)
        {
            if (running.Any(p => p.Pid == pid))
            {
                return pid;
            }

            throw new InvalidOperationException(
                $"Requested PID={pid} is not a running acad process. " +
                $"Running: {FormatProcessSummary(running)}. Call cad_discover_process and pass a valid pid.");
        }

        if (running.Count == 1)
        {
            return running[0].Pid;
        }

        throw new InvalidOperationException(
            $"Multiple acad processes running ({running.Count}). Pass pid=... to cad_connect. " +
            $"Discovered: {FormatProcessSummary(running)}");
    }

    private bool MatchesBoundPid(int? requestedPid)
    {
        if (requestedPid is int req)
        {
            return _boundPid == req;
        }

        return true;
    }

    private static string FormatProcessSummary(IReadOnlyList<AcadProcessInfo> running)
        => running.Count == 0
            ? "No running acad process."
            : string.Join("; ", running.Select(p =>
                $"PID={p.Pid} Exe={p.ExePath ?? "(denied)"} Dir={p.Directory ?? "?"} Title={p.WindowTitle}"));

    private object? WaitForAppFromPid(int pid, int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(timeoutSeconds, 5, 300));
        while (DateTime.UtcNow < deadline)
        {
            if (!IsProcessAlive(pid))
            {
                return null;
            }

            var app = ComHelper.GetApplicationFromProcess(pid);
            if (app is not null)
            {
                return app;
            }

            Thread.Sleep(500);
        }

        return null;
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static int? TryReadPidFromApp(object app)
    {
        try
        {
            var hwndObj = ComHelper.GetProperty(app, "HWND");
            if (hwndObj is null)
            {
                return null;
            }

            var hwnd = new IntPtr(Convert.ToInt64(hwndObj));
            if (hwnd == IntPtr.Zero)
            {
                return null;
            }

            _ = GetWindowThreadProcessId(hwnd, out var pid);
            return pid == 0 ? null : (int)pid;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private void CacheExeFromProcesses(IReadOnlyList<AcadProcessInfo> running)
    {
        var exe = running.Select(p => p.ExePath).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p!));
        if (exe is null)
        {
            return;
        }

        _discoveredExePath = exe;
        _options.AcadExePath ??= exe;
    }

    private string DiscoverProcessCore()
    {
        var infos = ListRunningAcadProcesses();
        if (infos.Count == 0)
        {
            return "No running acad process found. Start AutoCAD 2008 first, then call again.";
        }

        CacheExeFromProcesses(infos);

        var lines = new List<string> { $"Found {infos.Count} acad process(es):" };
        foreach (var info in infos)
        {
            var bound = _boundPid == info.Pid ? " [BOUND]" : "";
            lines.Add(
                $"PID={info.Pid}{bound}, Exe={info.ExePath ?? "(access denied)"}, Dir={info.Directory ?? "?"}, Title={info.WindowTitle}");
        }

        if (_boundPid is int boundPid)
        {
            lines.Add($"Session bound PID: {boundPid}" + (IsProcessAlive(boundPid) ? " (alive)" : " (exited)"));
        }
        else
        {
            lines.Add("Session bound PID: (none). If multiple processes, call cad_connect with pid=...");
        }

        if (_discoveredExePath is not null)
        {
            lines.Add($"Cached path: {_discoveredExePath}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private (object? App, string? ProgId) TryGetActiveApp()
    {
        foreach (var progId in ProgIds)
        {
            var app = ComHelper.GetActiveObject(progId);
            if (app is not null)
            {
                return (app, progId);
            }
        }

        return (null, null);
    }

    private (object? App, string? ProgId) WaitForActiveApp(int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(timeoutSeconds, 5, 300));
        while (DateTime.UtcNow < deadline)
        {
            var found = TryGetActiveApp();
            if (found.App is not null)
            {
                return found;
            }

            Thread.Sleep(500);
        }

        return (null, null);
    }

    private string? ResolveAcadExePath(string? overridePath)
    {
        var candidates = new[]
        {
            overridePath,
            _discoveredExePath,
            _options.AcadExePath,
            Environment.GetEnvironmentVariable("AUTOCAD_EXE"),
            TryGetExePathFromRunningProcesses(),
        };

        foreach (var raw in candidates)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var path = raw.Trim().Trim('"');
            if (File.Exists(path))
            {
                var full = Path.GetFullPath(path);
                _discoveredExePath = full;
                return full;
            }

            _logger.LogWarning("Configured acad.exe not found: {Path}", path);
        }

        return null;
    }

    private static string? TryGetExePathFromRunningProcesses()
        => ListRunningAcadProcesses()
            .Select(p => p.ExePath)
            .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p!));

    private static List<AcadProcessInfo> ListRunningAcadProcesses()
    {
        var list = new List<AcadProcessInfo>();
        foreach (var name in new[] { "acad", "acadbin", "AutoCAD" })
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch
            {
                continue;
            }

            foreach (var proc in processes)
            {
                using (proc)
                {
                    string? exe = null;
                    string? dir = null;
                    var title = "(no title)";
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(proc.MainWindowTitle))
                        {
                            title = proc.MainWindowTitle;
                        }
                    }
                    catch
                    {
                        // ignore
                    }

                    try
                    {
                        exe = proc.MainModule?.FileName;
                        if (!string.IsNullOrWhiteSpace(exe))
                        {
                            dir = Path.GetDirectoryName(exe);
                        }
                    }
                    catch (Exception)
                    {
                        // Bitnes/access: MainModule may throw.
                        exe = null;
                    }

                    list.Add(new AcadProcessInfo(proc.Id, exe, dir, title));
                }
            }
        }

        return list
            .GroupBy(p => p.Pid)
            .Select(g => g.First())
            .OrderBy(p => p.Pid)
            .ToList();
    }

    private void LaunchAcadExe(string exePath)
    {
        if (ListRunningAcadProcesses().Count > 0)
        {
            _logger.LogInformation("Found running acad process(es); waiting for COM instead of starting another.");
            return;
        }

        var start = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory,
            UseShellExecute = true,
        };
        Process.Start(start);
    }

    private readonly record struct AcadProcessInfo(int Pid, string? ExePath, string? Directory, string WindowTitle);

    private string GetStatusCore()
    {
        var running = ListRunningAcadProcesses();
        CacheExeFromProcesses(running);
        var processLine = running.Count == 0
            ? "Process: none"
            : "Process: " + string.Join("; ", running.Select(p =>
                $"PID={p.Pid}{( _boundPid == p.Pid ? "*" : "")} Exe={p.ExePath ?? "(denied)"}"));

        var boundLine = _boundPid is int bp
            ? $"BoundPid={bp} ({(IsProcessAlive(bp) ? "alive" : "exited")})"
            : "BoundPid=(none)";

        if (_app is null || !IsAlive(_app))
        {
            return $"{processLine}. {boundLine}. COM: not connected. Call cad_connect after CAD is running" +
                   (running.Count > 1 ? " (pass pid=... when multiple)." : ".");
        }

        var exe = _discoveredExePath ?? TryGetExePathFromRunningProcesses();
        var baseInfo = FormatAppInfo("OK");
        return exe is null
            ? $"{processLine}. {boundLine}. {baseInfo}"
            : $"{processLine}. {boundLine}. {baseInfo}, Exe={exe}, Dir={Path.GetDirectoryName(exe)}";
    }

    private string NewDrawingCore()
    {
        EnsureApp();
        var docs = ComHelper.GetProperty(_app!, "Documents")
            ?? throw new InvalidOperationException("Documents is null.");
        try
        {
            ComHelper.Invoke(docs, "Add");
            return "Created new drawing.";
        }
        finally
        {
            ComHelper.Release(docs);
        }
    }

    private string OpenDrawingCore(string path)
    {
        EnsureApp();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new FileNotFoundException("Drawing file not found.", path);
        }

        var docs = ComHelper.GetProperty(_app!, "Documents")
            ?? throw new InvalidOperationException("Documents is null.");
        try
        {
            ComHelper.Invoke(docs, "Open", path);
            return $"Opened: {path}";
        }
        finally
        {
            ComHelper.Release(docs);
        }
    }

    private string SaveDrawingCore(string? path)
    {
        var doc = GetActiveDocument();
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                ComHelper.Invoke(doc, "Save");
                var name = ComHelper.GetProperty(doc, "FullName")?.ToString() ?? "(unnamed)";
                return $"Saved: {name}";
            }

            ComHelper.Invoke(doc, "SaveAs", path);
            return $"Saved as: {path}";
        }
        finally
        {
            ComHelper.Release(doc);
        }
    }

    private string DrawLineCore(double x1, double y1, double x2, double y2, string? layer)
    {
        var ms = GetModelSpace();
        try
        {
            var entity = ComHelper.Invoke(ms, "AddLine", ComHelper.Point(x1, y1), ComHelper.Point(x2, y2))
                ?? throw new InvalidOperationException("AddLine returned null.");
            try
            {
                ApplyLayer(entity, layer);
                return DescribeEntity("LINE", entity);
            }
            finally
            {
                ComHelper.Release(entity);
            }
        }
        finally
        {
            ComHelper.Release(ms);
        }
    }

    private string DrawCircleCore(double cx, double cy, double radius, string? layer)
    {
        if (radius <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radius), "Radius must be > 0.");
        }

        var ms = GetModelSpace();
        try
        {
            var entity = ComHelper.Invoke(ms, "AddCircle", ComHelper.Point(cx, cy), radius)
                ?? throw new InvalidOperationException("AddCircle returned null.");
            try
            {
                ApplyLayer(entity, layer);
                return DescribeEntity("CIRCLE", entity);
            }
            finally
            {
                ComHelper.Release(entity);
            }
        }
        finally
        {
            ComHelper.Release(ms);
        }
    }

    private string DrawArcCore(double cx, double cy, double radius, double startAngleDeg, double endAngleDeg, string? layer)
    {
        if (radius <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radius), "Radius must be > 0.");
        }

        var startRad = startAngleDeg * Math.PI / 180.0;
        var endRad = endAngleDeg * Math.PI / 180.0;
        var ms = GetModelSpace();
        try
        {
            var entity = ComHelper.Invoke(ms, "AddArc", ComHelper.Point(cx, cy), radius, startRad, endRad)
                ?? throw new InvalidOperationException("AddArc returned null.");
            try
            {
                ApplyLayer(entity, layer);
                return DescribeEntity("ARC", entity);
            }
            finally
            {
                ComHelper.Release(entity);
            }
        }
        finally
        {
            ComHelper.Release(ms);
        }
    }

    private string DrawRectangleCore(double x1, double y1, double x2, double y2, string? layer)
    {
        var points = new (double X, double Y)[]
        {
            (x1, y1),
            (x2, y1),
            (x2, y2),
            (x1, y2),
        };
        return DrawPolylineCore(points, closed: true, layer);
    }

    private string DrawPolylineCore(IReadOnlyList<(double X, double Y)> points, bool closed, string? layer)
    {
        if (points.Count < 2)
        {
            throw new ArgumentException("Polyline needs at least 2 points.", nameof(points));
        }

        // LightweightPolyline expects a flat double[] of X,Y pairs (2D).
        var coords = new double[points.Count * 2];
        for (var i = 0; i < points.Count; i++)
        {
            coords[i * 2] = points[i].X;
            coords[i * 2 + 1] = points[i].Y;
        }

        var ms = GetModelSpace();
        try
        {
            var entity = ComHelper.Invoke(ms, "AddLightWeightPolyline", coords)
                ?? throw new InvalidOperationException("AddLightWeightPolyline returned null.");
            try
            {
                if (closed)
                {
                    ComHelper.SetProperty(entity, "Closed", true);
                }

                ApplyLayer(entity, layer);
                return DescribeEntity(closed ? "LWPOLYLINE(closed)" : "LWPOLYLINE", entity);
            }
            finally
            {
                ComHelper.Release(entity);
            }
        }
        finally
        {
            ComHelper.Release(ms);
        }
    }

    private string DrawTextCore(double x, double y, double height, string text, double rotationDeg, string? layer)
    {
        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "Text height must be > 0.");
        }

        if (string.IsNullOrEmpty(text))
        {
            throw new ArgumentException("Text cannot be empty.", nameof(text));
        }

        var ms = GetModelSpace();
        try
        {
            var entity = ComHelper.Invoke(ms, "AddText", text, ComHelper.Point(x, y), height)
                ?? throw new InvalidOperationException("AddText returned null.");
            try
            {
                if (Math.Abs(rotationDeg) > 1e-9)
                {
                    ComHelper.SetProperty(entity, "Rotation", rotationDeg * Math.PI / 180.0);
                }

                ApplyLayer(entity, layer);
                return DescribeEntity("TEXT", entity);
            }
            finally
            {
                ComHelper.Release(entity);
            }
        }
        finally
        {
            ComHelper.Release(ms);
        }
    }

    private string CreateLayerCore(string name, short colorIndex)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Layer name is required.", nameof(name));
        }

        var doc = GetActiveDocument();
        object? layers = null;
        object? layer = null;
        try
        {
            layers = ComHelper.GetProperty(doc, "Layers")
                ?? throw new InvalidOperationException("Layers is null.");

            try
            {
                layer = ComHelper.Invoke(layers, "Item", name);
            }
            catch
            {
                layer = ComHelper.Invoke(layers, "Add", name)
                    ?? throw new InvalidOperationException("Failed to add layer.");
            }

            // AutoCAD ACI color 1-255
            var color = Math.Clamp(colorIndex, (short)1, (short)255);
            ComHelper.SetProperty(layer!, "Color", color);
            return $"Layer '{name}' ready (color {color}).";
        }
        finally
        {
            ComHelper.Release(layer);
            ComHelper.Release(layers);
            ComHelper.Release(doc);
        }
    }

    private string SetCurrentLayerCore(string name)
    {
        var doc = GetActiveDocument();
        object? layer = null;
        try
        {
            layer = GetLayer(doc, name);
            ComHelper.SetProperty(doc, "ActiveLayer", layer);
            return $"Current layer: {name}";
        }
        finally
        {
            ComHelper.Release(layer);
            ComHelper.Release(doc);
        }
    }

    private string ZoomExtentsCore()
    {
        EnsureApp();
        // AcadApplication.ZoomExtents()
        ComHelper.Invoke(_app!, "ZoomExtents");
        return "ZoomExtents done.";
    }

    private string SendCommandCore(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException("Command is required.", nameof(command));
        }

        var doc = GetActiveDocument();
        try
        {
            // AutoCAD SendCommand expects a trailing newline for execution.
            var cmd = command.EndsWith('\n') ? command : command + "\n";
            ComHelper.Invoke(doc, "SendCommand", cmd);
            return $"Sent command: {command.Trim()}";
        }
        finally
        {
            ComHelper.Release(doc);
        }
    }

    private string ListEntitiesCore(int maxCount)
    {
        maxCount = Math.Clamp(maxCount, 1, 500);
        var ms = GetModelSpace();
        try
        {
            var count = Convert.ToInt32(ComHelper.GetProperty(ms, "Count") ?? 0);
            var lines = new List<string> { $"ModelSpace entities: {count} (showing up to {maxCount})" };
            var take = Math.Min(count, maxCount);
            for (var i = 0; i < take; i++)
            {
                object? ent = null;
                try
                {
                    ent = ComHelper.Invoke(ms, "Item", i);
                    if (ent is null)
                    {
                        continue;
                    }

                    var typeName = ComHelper.GetProperty(ent, "ObjectName")?.ToString() ?? "?";
                    var handle = ComHelper.GetProperty(ent, "Handle")?.ToString() ?? "?";
                    var layer = ComHelper.GetProperty(ent, "Layer")?.ToString() ?? "?";
                    lines.Add($"[{i}] {typeName} handle={handle} layer={layer}");
                }
                finally
                {
                    ComHelper.Release(ent);
                }
            }

            return string.Join(Environment.NewLine, lines);
        }
        finally
        {
            ComHelper.Release(ms);
        }
    }

    private string EraseByHandleCore(string handle)
    {
        if (string.IsNullOrWhiteSpace(handle))
        {
            throw new ArgumentException("Handle is required.", nameof(handle));
        }

        var doc = GetActiveDocument();
        object? ent = null;
        try
        {
            ent = ComHelper.Invoke(doc, "HandleToObject", handle)
                ?? throw new InvalidOperationException($"No entity with handle '{handle}'.");
            ComHelper.Invoke(ent, "Delete");
            return $"Deleted entity handle={handle}";
        }
        finally
        {
            ComHelper.Release(ent);
            ComHelper.Release(doc);
        }
    }

    private void EnsureApp()
    {
        if (_app is not null && IsAlive(_app))
        {
            if (_boundPid is int pid && !IsProcessAlive(pid))
            {
                ComHelper.Release(_app);
                _app = null;
                var dead = _boundPid;
                _boundPid = null;
                throw new InvalidOperationException(
                    $"COM failed; bound PID={dead} not found → AutoCAD exited. Call cad_connect again.");
            }

            return;
        }

        if (_app is not null)
        {
            ComHelper.Release(_app);
            _app = null;
        }

        if (_boundPid is int bound && !IsProcessAlive(bound))
        {
            var dead = _boundPid;
            _boundPid = null;
            throw new InvalidOperationException(
                $"COM failed; bound PID={dead} not found → AutoCAD exited. Call cad_connect again.");
        }

        try
        {
            ConnectCore(visible: true, acadExePath: null, requestedPid: null);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{DiagnoseBoundPid()} Reconnect failed: {ex.Message}", ex);
        }
    }

    private string DiagnoseBoundPid()
    {
        if (_boundPid is not int pid)
        {
            return "COM failed; no bound PID.";
        }

        if (!IsProcessAlive(pid))
        {
            return $"COM failed; bound PID={pid} not found → AutoCAD exited.";
        }

        return $"COM failed; bound PID={pid} still running → likely busy/dialog/command or RCW invalid.";
    }

    private void EnsureDocument()
    {
        EnsureApp();
        var docs = ComHelper.GetProperty(_app!, "Documents");
        try
        {
            var count = Convert.ToInt32(ComHelper.GetProperty(docs!, "Count") ?? 0);
            if (count == 0)
            {
                ComHelper.Invoke(docs!, "Add");
            }
        }
        finally
        {
            ComHelper.Release(docs);
        }
    }

    private object GetActiveDocument()
    {
        EnsureDocument();
        return ComHelper.GetProperty(_app!, "ActiveDocument")
            ?? throw new InvalidOperationException("No active document.");
    }

    private object GetModelSpace()
    {
        // Keep ActiveDocument RCW alive via GetProperty only; do not Release the document
        // while ModelSpace is still in use (same COM identity graph).
        var doc = GetActiveDocument();
        return ComHelper.GetProperty(doc, "ModelSpace")
            ?? throw new InvalidOperationException("ModelSpace is null.");
    }

    private object GetLayer(object doc, string name)
    {
        var layers = ComHelper.GetProperty(doc, "Layers")
            ?? throw new InvalidOperationException("Layers is null.");
        try
        {
            return ComHelper.Invoke(layers, "Item", name)
                ?? throw new InvalidOperationException($"Layer '{name}' not found.");
        }
        finally
        {
            ComHelper.Release(layers);
        }
    }

    private void ApplyLayer(object entity, string? layer)
    {
        if (string.IsNullOrWhiteSpace(layer))
        {
            return;
        }

        var doc = GetActiveDocument();
        try
        {
            // Ensure layer exists (create with default color if missing).
            CreateLayerCore(layer, colorIndex: 7);
            ComHelper.SetProperty(entity, "Layer", layer);
        }
        finally
        {
            ComHelper.Release(doc);
        }
    }

    private string DescribeEntity(string kind, object entity)
    {
        var handle = ComHelper.GetProperty(entity, "Handle")?.ToString() ?? "?";
        var layer = ComHelper.GetProperty(entity, "Layer")?.ToString() ?? "?";
        return $"Created {kind}: handle={handle}, layer={layer}";
    }

    private string FormatAppInfo(string prefix)
    {
        // Do not call EnsureApp here — callers already connected; avoids recursion on reconnect.
        var name = ComHelper.GetProperty(_app!, "Name")?.ToString() ?? "AutoCAD";
        var version = ComHelper.GetProperty(_app!, "Version")?.ToString() ?? "?";
        var fullName = ComHelper.GetProperty(_app!, "FullName")?.ToString() ?? "?";
        string docName = "(none)";
        try
        {
            var doc = ComHelper.GetProperty(_app!, "ActiveDocument");
            if (doc is not null)
            {
                docName = ComHelper.GetProperty(doc, "Name")?.ToString() ?? "(unnamed)";
                ComHelper.Release(doc);
            }
        }
        catch
        {
            // No document yet.
        }

        var pidPart = _boundPid is int bp ? $", BoundPid={bp}" : ", BoundPid=(none)";
        return $"{prefix}. App={name}, Version={version}, Doc={docName}, Path={fullName}, StartedByUs={_startedByUs}{pidPart}";
    }

    private static bool IsAlive(object app)
    {
        try
        {
            _ = ComHelper.GetProperty(app, "Name");
            return true;
        }
        catch (Exception ex) when (ComHelper.IsBusyComException(ex))
        {
            // Rejected/busy means the server is still there.
            return true;
        }
        catch (Exception ex) when (
            ex is InvalidComObjectException
            || ComHelper.UnwrapComException(ex) is not null)
        {
            // Late-bound COM failures are often wrapped in TargetInvocationException.
            return false;
        }
    }

    public void Dispose()
    {
        _sta.InvokeAsync(() =>
        {
            if (_app is not null)
            {
                ComHelper.Release(_app);
                _app = null;
            }

            _boundPid = null;
        }).GetAwaiter().GetResult();
    }
}
