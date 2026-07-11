using System.Diagnostics;
using System.Runtime.InteropServices;
using AutoCadMcp.Com;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AutoCadMcp.Cad;

/// <summary>
/// Late-bound COM session for AutoCAD 2008 (ProgID AutoCAD.Application.17).
/// Also supports portable installs: launch acad.exe by path, then attach via ROT.
/// </summary>
public sealed class AutoCadSession : IDisposable
{
    // AutoCAD 2008 = version 17.0
    private static readonly string[] ProgIds =
    [
        "AutoCAD.Application.17",
        "AutoCAD.Application",
    ];

    private readonly StaDispatcher _sta;
    private readonly ILogger<AutoCadSession> _logger;
    private readonly AutoCadOptions _options;
    private object? _app;
    private bool _startedByUs;
    private string? _discoveredExePath;

    public AutoCadSession(StaDispatcher sta, IOptions<AutoCadOptions> options, ILogger<AutoCadSession> logger)
    {
        _sta = sta;
        _logger = logger;
        _options = options.Value;
    }

    public Task<string> ConnectAsync(bool visible = true, string? acadExePath = null, CancellationToken cancellationToken = default)
        => _sta.InvokeAsync(() => ConnectCore(visible, acadExePath), cancellationToken);

    public Task<string> DiscoverProcessAsync(CancellationToken cancellationToken = default)
        => _sta.InvokeAsync(DiscoverProcessCore, cancellationToken);

    public Task<string> GetStatusAsync(CancellationToken cancellationToken = default)
        => _sta.InvokeAsync(GetStatusCore, cancellationToken);

    public Task<string> NewDrawingAsync(CancellationToken cancellationToken = default)
        => _sta.InvokeAsync(NewDrawingCore, cancellationToken);

    public Task<string> OpenDrawingAsync(string path, CancellationToken cancellationToken = default)
        => _sta.InvokeAsync(() => OpenDrawingCore(path), cancellationToken);

    public Task<string> SaveDrawingAsync(string? path, CancellationToken cancellationToken = default)
        => _sta.InvokeAsync(() => SaveDrawingCore(path), cancellationToken);

    public Task<string> DrawLineAsync(double x1, double y1, double x2, double y2, string? layer, CancellationToken cancellationToken = default)
        => _sta.InvokeAsync(() => DrawLineCore(x1, y1, x2, y2, layer), cancellationToken);

    public Task<string> DrawCircleAsync(double cx, double cy, double radius, string? layer, CancellationToken cancellationToken = default)
        => _sta.InvokeAsync(() => DrawCircleCore(cx, cy, radius, layer), cancellationToken);

    public Task<string> DrawArcAsync(double cx, double cy, double radius, double startAngleDeg, double endAngleDeg, string? layer, CancellationToken cancellationToken = default)
        => _sta.InvokeAsync(() => DrawArcCore(cx, cy, radius, startAngleDeg, endAngleDeg, layer), cancellationToken);

    public Task<string> DrawRectangleAsync(double x1, double y1, double x2, double y2, string? layer, CancellationToken cancellationToken = default)
        => _sta.InvokeAsync(() => DrawRectangleCore(x1, y1, x2, y2, layer), cancellationToken);

    public Task<string> DrawPolylineAsync(IReadOnlyList<(double X, double Y)> points, bool closed, string? layer, CancellationToken cancellationToken = default)
        => _sta.InvokeAsync(() => DrawPolylineCore(points, closed, layer), cancellationToken);

    public Task<string> DrawTextAsync(double x, double y, double height, string text, double rotationDeg, string? layer, CancellationToken cancellationToken = default)
        => _sta.InvokeAsync(() => DrawTextCore(x, y, height, text, rotationDeg, layer), cancellationToken);

    public Task<string> CreateLayerAsync(string name, short colorIndex, CancellationToken cancellationToken = default)
        => _sta.InvokeAsync(() => CreateLayerCore(name, colorIndex), cancellationToken);

    public Task<string> SetCurrentLayerAsync(string name, CancellationToken cancellationToken = default)
        => _sta.InvokeAsync(() => SetCurrentLayerCore(name), cancellationToken);

    public Task<string> ZoomExtentsAsync(CancellationToken cancellationToken = default)
        => _sta.InvokeAsync(ZoomExtentsCore, cancellationToken);

    public Task<string> SendCommandAsync(string command, CancellationToken cancellationToken = default)
        => _sta.InvokeAsync(() => SendCommandCore(command), cancellationToken);

    public Task<string> ListEntitiesAsync(int maxCount, CancellationToken cancellationToken = default)
        => _sta.InvokeAsync(() => ListEntitiesCore(maxCount), cancellationToken);

    public Task<string> EraseByHandleAsync(string handle, CancellationToken cancellationToken = default)
        => _sta.InvokeAsync(() => EraseByHandleCore(handle), cancellationToken);

    private string ConnectCore(bool visible, string? acadExePath)
    {
        if (_app is not null && IsAlive(_app))
        {
            ComHelper.SetProperty(_app, "Visible", visible);
            return FormatAppInfo("Already connected");
        }

        // 0) Always detect running CAD process first (green/official).
        var running = ListRunningAcadProcesses();
        CacheExeFromProcesses(running);
        if (!string.IsNullOrWhiteSpace(acadExePath) && File.Exists(acadExePath.Trim().Trim('"')))
        {
            _discoveredExePath = Path.GetFullPath(acadExePath.Trim().Trim('"'));
        }

        object? app = null;
        string? usedProgId = null;
        var connectMode = "unknown";
        var processSummary = running.Count == 0
            ? "No running acad process."
            : string.Join("; ", running.Select(p =>
                $"PID={p.Pid} Exe={p.ExePath ?? "(denied)"} Dir={p.Directory ?? "?"}"));

        _logger.LogInformation("CAD process check: {Summary}", processSummary);

        if (running.Count > 0)
        {
            // 1) CAD is already running → only attach, do not start another instance.
            (app, usedProgId) = TryGetActiveApp();
            if (app is null)
            {
                // Process up but COM not ready yet (cold UI) — brief wait.
                (app, usedProgId) = WaitForActiveApp(Math.Min(15, _options.LaunchTimeoutSeconds));
            }

            if (app is not null)
            {
                connectMode = "attach-running-process";
                _startedByUs = false;
                _logger.LogInformation("Attached to running AutoCAD process via {ProgId}", usedProgId);
            }
            else
            {
                throw new InvalidOperationException(
                    "Detected running AutoCAD process but COM attach failed. " +
                    $"Processes: {processSummary}. " +
                    "The build may lack Automation/COM, or you need to wait until CAD fully finishes starting, then retry cad_connect.");
            }
        }
        else
        {
            // 2) No process → try ProgID (official install).
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

            // 3) Still nothing → launch acad.exe from cached/env/arg path, then attach.
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
                (app, usedProgId) = WaitForActiveApp(_options.LaunchTimeoutSeconds);
                if (app is not null)
                {
                    connectMode = "exe-launch-attach";
                    CacheExeFromProcesses(ListRunningAcadProcesses());
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Started '{exePath}' but COM never appeared. This green build likely has no Automation support.");
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
        var exeInfo = _discoveredExePath is not null ? $", Exe={_discoveredExePath}" : "";
        return $"ProcessCheck: {processSummary}. " + FormatAppInfo($"Connected ({connectMode}) via {usedProgId}") + exeInfo;
    }

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
            lines.Add(
                $"PID={info.Pid}, Exe={info.ExePath ?? "(access denied)"}, Dir={info.Directory ?? "?"}, Title={info.WindowTitle}");
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
                $"PID={p.Pid} Exe={p.ExePath ?? "(denied)"}"));

        if (_app is null || !IsAlive(_app))
        {
            return $"{processLine}. COM: not connected. Call cad_connect after CAD is running.";
        }

        var exe = _discoveredExePath ?? TryGetExePathFromRunningProcesses();
        var baseInfo = FormatAppInfo("OK");
        return exe is null
            ? $"{processLine}. {baseInfo}"
            : $"{processLine}. {baseInfo}, Exe={exe}, Dir={Path.GetDirectoryName(exe)}";
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
        if (_app is null || !IsAlive(_app))
        {
            ConnectCore(visible: true, acadExePath: null);
        }
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
        EnsureApp();
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

        return $"{prefix}. App={name}, Version={version}, Doc={docName}, Path={fullName}, StartedByUs={_startedByUs}";
    }

    private static bool IsAlive(object app)
    {
        try
        {
            _ = ComHelper.GetProperty(app, "Name");
            return true;
        }
        catch (COMException)
        {
            return false;
        }
        catch (InvalidComObjectException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _sta.InvokeAsync(() =>
        {
            if (_app is not null)
            {
                // Do not Quit AutoCAD if we attached to an existing instance.
                if (_startedByUs)
                {
                    try
                    {
                        // Leave CAD running for the user; only release our RCW.
                    }
                    catch
                    {
                        // ignore
                    }
                }

                ComHelper.Release(_app);
                _app = null;
            }
        }).GetAwaiter().GetResult();
    }
}
