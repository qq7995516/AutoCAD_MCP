using System.ComponentModel;
using System.Text.Json;
using AutoCadMcp.Cad;
using ModelContextProtocol.Server;

namespace AutoCadMcp.Tools;

[McpServerToolType]
public sealed class AutoCadTools(AutoCadSession session)
{
    [McpServerTool(Name = "cad_connect"), Description("Connect to AutoCAD 2008. ALWAYS detects running acad process first: if running → attach COM only (no second instance); if not running → ProgID create or launch cached acad.exe then attach.")]
    public Task<string> Connect(
        [Description("Whether AutoCAD window should be visible")] bool visible = true,
        [Description("Optional full path to acad.exe; only used when no CAD process is running")] string? acadExePath = null)
        => session.ConnectAsync(visible, acadExePath);

    [McpServerTool(Name = "cad_discover_process"), Description("Detect whether AutoCAD is running. Returns PID, exe path, and folder; caches path for later launch. Prefer calling this (or cad_status) before drawing.")]
    public Task<string> DiscoverProcess()
        => session.DiscoverProcessAsync();

    [McpServerTool(Name = "cad_status"), Description("Check running acad process first, then COM connection status. Does not start CAD.")]
    public Task<string> Status()
        => session.GetStatusAsync();

    [McpServerTool(Name = "cad_new_drawing"), Description("Create a new empty drawing in AutoCAD.")]
    public Task<string> NewDrawing()
        => session.NewDrawingAsync();

    [McpServerTool(Name = "cad_open_drawing"), Description("Open an existing DWG/DXF file.")]
    public Task<string> OpenDrawing(
        [Description("Absolute path to the drawing file")] string path)
        => session.OpenDrawingAsync(path);

    [McpServerTool(Name = "cad_save_drawing"), Description("Save the active drawing. If path is omitted, saves to the current file.")]
    public Task<string> SaveDrawing(
        [Description("Optional absolute path for SaveAs")] string? path = null)
        => session.SaveDrawingAsync(path);

    [McpServerTool(Name = "cad_draw_line"), Description("Draw a line in model space from (x1,y1) to (x2,y2).")]
    public Task<string> DrawLine(
        [Description("Start X")] double x1,
        [Description("Start Y")] double y1,
        [Description("End X")] double x2,
        [Description("End Y")] double y2,
        [Description("Optional layer name")] string? layer = null)
        => session.DrawLineAsync(x1, y1, x2, y2, layer);

    [McpServerTool(Name = "cad_draw_circle"), Description("Draw a circle in model space.")]
    public Task<string> DrawCircle(
        [Description("Center X")] double cx,
        [Description("Center Y")] double cy,
        [Description("Radius (>0)")] double radius,
        [Description("Optional layer name")] string? layer = null)
        => session.DrawCircleAsync(cx, cy, radius, layer);

    [McpServerTool(Name = "cad_draw_arc"), Description("Draw an arc. Angles are in degrees, measured counter-clockwise from +X.")]
    public Task<string> DrawArc(
        [Description("Center X")] double cx,
        [Description("Center Y")] double cy,
        [Description("Radius (>0)")] double radius,
        [Description("Start angle in degrees")] double startAngleDeg,
        [Description("End angle in degrees")] double endAngleDeg,
        [Description("Optional layer name")] string? layer = null)
        => session.DrawArcAsync(cx, cy, radius, startAngleDeg, endAngleDeg, layer);

    [McpServerTool(Name = "cad_draw_rectangle"), Description("Draw a rectangle as a closed lightweight polyline using opposite corners.")]
    public Task<string> DrawRectangle(
        [Description("Corner1 X")] double x1,
        [Description("Corner1 Y")] double y1,
        [Description("Corner2 X")] double x2,
        [Description("Corner2 Y")] double y2,
        [Description("Optional layer name")] string? layer = null)
        => session.DrawRectangleAsync(x1, y1, x2, y2, layer);

    [McpServerTool(Name = "cad_draw_polyline"), Description("Draw a lightweight polyline. pointsJson is a JSON array of {x,y} objects, e.g. [{\"x\":0,\"y\":0},{\"x\":10,\"y\":0}].")]
    public Task<string> DrawPolyline(
        [Description("JSON array of points: [{\"x\":0,\"y\":0},...]")] string pointsJson,
        [Description("Whether to close the polyline")] bool closed = false,
        [Description("Optional layer name")] string? layer = null)
    {
        var points = ParsePoints(pointsJson);
        return session.DrawPolylineAsync(points, closed, layer);
    }

    [McpServerTool(Name = "cad_draw_text"), Description("Draw single-line text in model space.")]
    public Task<string> DrawText(
        [Description("Insertion X")] double x,
        [Description("Insertion Y")] double y,
        [Description("Text height (>0)")] double height,
        [Description("Text content")] string text,
        [Description("Rotation in degrees")] double rotationDeg = 0,
        [Description("Optional layer name")] string? layer = null)
        => session.DrawTextAsync(x, y, height, text, rotationDeg, layer);

    [McpServerTool(Name = "cad_create_layer"), Description("Create a layer (or update color if it already exists). colorIndex is AutoCAD Color Index 1-255 (7=white/black).")]
    public Task<string> CreateLayer(
        [Description("Layer name")] string name,
        [Description("ACI color index 1-255")] short colorIndex = 7)
        => session.CreateLayerAsync(name, colorIndex);

    [McpServerTool(Name = "cad_set_layer"), Description("Set the active/current layer.")]
    public Task<string> SetLayer(
        [Description("Layer name")] string name)
        => session.SetCurrentLayerAsync(name);

    [McpServerTool(Name = "cad_zoom_extents"), Description("Zoom the active viewport to drawing extents.")]
    public Task<string> ZoomExtents()
        => session.ZoomExtentsAsync();

    [McpServerTool(Name = "cad_send_command"), Description("Send a raw AutoCAD command string to the active document (advanced). Prefer dedicated draw tools when possible.")]
    public Task<string> SendCommand(
        [Description("Command text, e.g. LINE or a LISP expression")] string command)
        => session.SendCommandAsync(command);

    [McpServerTool(Name = "cad_list_entities"), Description("List entities in model space (handle, type, layer).")]
    public Task<string> ListEntities(
        [Description("Max entities to list (1-500)")] int maxCount = 50)
        => session.ListEntitiesAsync(maxCount);

    [McpServerTool(Name = "cad_erase"), Description("Erase an entity by its AutoCAD handle (from cad_list_entities / draw results).")]
    public Task<string> Erase(
        [Description("Entity handle string")] string handle)
        => session.EraseByHandleAsync(handle);

    private static List<(double X, double Y)> ParsePoints(string pointsJson)
    {
        using var doc = JsonDocument.Parse(pointsJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("pointsJson must be a JSON array of {x,y} objects.");
        }

        var list = new List<(double X, double Y)>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            if (el.ValueKind == JsonValueKind.Array && el.GetArrayLength() >= 2)
            {
                list.Add((el[0].GetDouble(), el[1].GetDouble()));
                continue;
            }

            var x = el.GetProperty("x").GetDouble();
            var y = el.GetProperty("y").GetDouble();
            list.Add((x, y));
        }

        if (list.Count < 2)
        {
            throw new ArgumentException("Need at least 2 points.");
        }

        return list;
    }
}
