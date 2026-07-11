# AutoCAD 2008 MCP Server (C# + COM)

本地 MCP 服务端：通过 COM 控制 **AutoCAD 2008**，让 Cursor Agent 画图。

## 架构

```
Cursor Agent  --stdio MCP-->  AutoCadMcp.exe (x86)  --COM-->  AutoCAD 2008
```

- 传输：stdio（JSON-RPC）
- COM：晚绑定（无需 Interop DLL）
- ProgID：`AutoCAD.Application.17`（2008）/ `AutoCAD.Application`
- 所有 COM 调用在专用 **STA** 线程执行

## 环境要求

1. AutoCAD 2008（官方安装 **或** 绿色版，见下）
2. [.NET 10 SDK](https://dotnet.microsoft.com/download)（可跑 `net10.0-windows`）
3. 本进程必须是 **x86**（已在 csproj 中固定）

### 绿色版兼容说明

COM 控制依赖 AutoCAD 启动后把自身挂到系统 ROT。很多绿色版**没注册 ProgID**，但仍可能在进程运行后被附着。

连接顺序：

1. **先检测**是否有运行中的 `acad` 进程（并缓存 exe 目录）  
2. 有进程 → **只附着 COM**，不再启动第二个实例  
3. 无进程 → ProgID 创建，或用已缓存/`AUTOCAD_EXE` 路径启动后再附着  

推荐：先开 CAD → `cad_discover_process` 或 `cad_status` → `cad_connect` → 再画图。

## 构建

```powershell
cd F:\CSharp_Demo\AutoCAD_MCP
dotnet build src\AutoCadMcp\AutoCadMcp.csproj -c Release
```

发布（可选）：

```powershell
dotnet publish src\AutoCadMcp\AutoCadMcp.csproj -c Release -r win-x86 --self-contained false
```

## 在 Cursor 中配置

项目已提供 `.cursor/mcp.json`。也可在用户级 MCP 配置中加入：

```json
{
  "mcpServers": {
    "autocad-2008": {
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "F:/CSharp_Demo/AutoCAD_MCP/src/AutoCadMcp/AutoCadMcp.csproj",
        "-c",
        "Release",
        "--no-build"
      ]
    }
  }
}
```

更稳妥的方式是先 `dotnet publish`，再把 `command` 指到发布出的 `AutoCadMcp.exe`。

配置后重启 MCP / Cursor，在 Agent 对话里应能看到 `cad_*` 工具。

## Agent 典型用法

1. `cad_connect` — 连接或启动 AutoCAD  
2. `cad_create_layer` / `cad_set_layer` — 图层  
3. `cad_draw_line` / `cad_draw_circle` / `cad_draw_rectangle` / `cad_draw_polyline` / `cad_draw_text` / `cad_draw_arc`  
4. `cad_zoom_extents`  
5. `cad_save_drawing`

示例提示词：

> 连接 AutoCAD，在图层 WALL（颜色 1）上画一个 100×80 的矩形，左下角在 (0,0)，并写文字 “ROOM”，然后 ZoomExtents 并保存到 D:\temp\demo.dwg

## 工具一览

| 工具 | 作用 |
|------|------|
| `cad_connect` | 连接/启动 CAD |
| `cad_status` | 状态与版本 |
| `cad_new_drawing` / `cad_open_drawing` / `cad_save_drawing` | 文档 |
| `cad_draw_line` / `cad_draw_circle` / `cad_draw_arc` | 基本图元 |
| `cad_draw_rectangle` / `cad_draw_polyline` | 多段线 |
| `cad_draw_text` | 单行文字 |
| `cad_create_layer` / `cad_set_layer` | 图层 |
| `cad_zoom_extents` | 缩放范围 |
| `cad_list_entities` / `cad_erase` | 查询/删除 |
| `cad_send_command` | 原始命令（高级） |

## 注意

- 首次冷启动 AutoCAD 可能较慢，`cad_connect` 后稍等再画。
- 不要对已打开的用户图纸随意 `Quit`；本服务释放 COM 时**不会**强制退出你已打开的 CAD。
- `cad_send_command` 可能打断交互式命令；优先用专用绘图工具。
