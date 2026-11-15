using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Sim.Core.Geo;
using Sim.Core.Map;

namespace Viewer;

/// <summary>
/// Draws the world as a flat map: longitude across, latitude down, clipped near the poles.
/// The map repeats to the left and right, so it can be scrolled around the globe without end.
/// Every cell is a polygon whose colour comes from one pixel of a small texture, so recolouring
/// the whole map only means rewriting that texture.
/// </summary>
public partial class MapView : Node2D
{
    public const float PixelsPerDegree = 10f;
    public const double MaxLatitude = 85.0;

    private const int TextureSize = 512;
    private const int CellsPerChunk = 16384;

    private readonly byte[] _pixels = new byte[TextureSize * TextureSize * 3];
    private WorldMap? _map;
    private Image? _image;
    private ImageTexture? _texture;

    // Three side-by-side copies of the map (west, centre, east) that share one mesh and one texture.
    private readonly Node2D[] _copies = new Node2D[3];
    private readonly CellOutline[] _outlines = new CellOutline[3];
    private readonly SettlementLayer[] _settlementLayers = new SettlementLayer[3];
    private readonly List<MeshInstance2D> _minorRivers = new List<MeshInstance2D>();

    private static readonly Color MajorRiverColour = Color.Color8(52, 104, 205);
    private static readonly Color MinorRiverColour = Color.Color8(88, 140, 215);

    // Minor rivers are hidden until the view is zoomed in this far, to keep the world view readable.
    private const float MinorRiverMinZoom = 1.6f;

    public static float WorldWidth => 360f * PixelsPerDegree;

    public static float HalfWidth => 180f * PixelsPerDegree;

    public static float HalfHeight => (float)MaxLatitude * PixelsPerDegree;

    /// <summary>Builds the map polygons and, if the file exists, the river lines. Call once.</summary>
    public void Build(WorldMap map, string? riverLinesPath)
    {
        _map = map;
        GeodesicGrid grid = map.Grid;
        if (grid.CellCount > TextureSize * TextureSize)
        {
            throw new InvalidOperationException("The map has more cells than the colour texture can hold.");
        }

        _image = Image.CreateFromData(TextureSize, TextureSize, false, Image.Format.Rgb8, _pixels);
        _texture = ImageTexture.CreateFromImage(_image);

        for (int k = 0; k < _copies.Length; k++)
        {
            _copies[k] = new Node2D { Position = new Vector2((k - 1) * WorldWidth, 0f) };
            AddChild(_copies[k]);
        }

        var vertices = new List<Vector2>();
        var uvs = new List<Vector2>();
        var indices = new List<int>();
        Span<Vec3> corners = stackalloc Vec3[GeodesicGrid.MaxNeighbours];
        Span<Vector2> points = stackalloc Vector2[GeodesicGrid.MaxNeighbours];
        int cellsInChunk = 0;

        for (int cell = 0; cell < grid.CellCount; cell++)
        {
            if (Math.Abs(grid.Latitude(cell)) > MaxLatitude)
            {
                continue;
            }

            int count = ProjectCell(grid, cell, corners, points);
            var uv = new Vector2(
                (cell % TextureSize + 0.5f) / TextureSize,
                (cell / TextureSize + 0.5f) / TextureSize);

            AddPolygon(vertices, uvs, indices, points, count, uv);

            cellsInChunk++;
            if (cellsInChunk >= CellsPerChunk)
            {
                FlushChunk(vertices, uvs, indices);
                cellsInChunk = 0;
            }
        }

        FlushChunk(vertices, uvs, indices);

        if (riverLinesPath != null && File.Exists(riverLinesPath))
        {
            BuildRivers(riverLinesPath);
        }

        for (int k = 0; k < _copies.Length; k++)
        {
            _settlementLayers[k] = new SettlementLayer();
            _copies[k].AddChild(_settlementLayers[k]);
            _outlines[k] = new CellOutline();
            _copies[k].AddChild(_outlines[k]);
        }
    }

    /// <summary>Replaces the settlements shown: map positions, names and populations.</summary>
    public void SetSettlements(Vector2[] positions, string[] names, float[] populations)
    {
        foreach (SettlementLayer layer in _settlementLayers)
        {
            layer?.SetSettlements(positions, names, populations);
        }
    }

    /// <summary>Replaces the trade routes: pairs of end positions, and the value each carries a month.</summary>
    public void SetRoutes(Vector2[] ends, float[] values)
    {
        foreach (SettlementLayer layer in _settlementLayers)
        {
            layer?.SetRoutes(ends, values);
        }
    }

    public void SetRoutesVisible(bool visible)
    {
        foreach (SettlementLayer layer in _settlementLayers)
        {
            layer?.SetRoutesVisible(visible);
        }
    }

    /// <summary>Tells the settlement layers the zoom and the visible part of the map.</summary>
    public void SetSettlementView(float zoom, Rect2 view)
    {
        for (int k = 0; k < _copies.Length; k++)
        {
            if (_settlementLayers[k] != null)
            {
                Rect2 local = view;
                local.Position -= _copies[k].Position;
                _settlementLayers[k].SetView(zoom, local);
            }
        }
    }

    /// <summary>Brings a horizontal map position back into the centre copy.</summary>
    public static float WrapX(float x)
    {
        float wrapped = (x + HalfWidth) % WorldWidth;
        if (wrapped < 0f)
        {
            wrapped += WorldWidth;
        }

        return wrapped - HalfWidth;
    }

    /// <summary>Hides the copies of the map that are outside the view, given its left and right edges.</summary>
    public void ShowCopiesInView(float left, float right)
    {
        for (int k = 0; k < _copies.Length; k++)
        {
            if (_copies[k] == null)
            {
                continue;
            }

            float centre = (k - 1) * WorldWidth;
            float margin = 2f * PixelsPerDegree;
            _copies[k].Visible = right > centre - HalfWidth - margin && left < centre + HalfWidth + margin;
        }
    }

    /// <summary>Shows or hides minor rivers to suit the zoom level.</summary>
    public void SetZoom(float zoom)
    {
        bool show = zoom >= MinorRiverMinZoom;
        foreach (MeshInstance2D rivers in _minorRivers)
        {
            rivers.Visible = show;
        }
    }

    // Reads river lines (longitude and latitude pairs) and draws them over the cells.
    private void BuildRivers(string path)
    {
        var major = new List<Vector2>();
        var minor = new List<Vector2>();
        try
        {
            using var reader = new BinaryReader(File.OpenRead(path));
            string magic = System.Text.Encoding.ASCII.GetString(reader.ReadBytes(8));
            if (magic != "SIM2RIVR")
            {
                GD.PrintErr("River file is not in the expected format.");
                return;
            }

            int lineCount = reader.ReadInt32();
            for (int line = 0; line < lineCount; line++)
            {
                byte size = reader.ReadByte();
                int pointCount = reader.ReadInt32();
                List<Vector2> target = size >= 2 ? major : minor;
                float previousLon = 0f;
                float previousLat = 0f;
                for (int p = 0; p < pointCount; p++)
                {
                    float lon = reader.ReadSingle();
                    float lat = reader.ReadSingle();
                    if (p > 0 && Math.Abs(lon - previousLon) < 180f
                        && Math.Abs(lat) <= MaxLatitude && Math.Abs(previousLat) <= MaxLatitude)
                    {
                        target.Add(MapPosition(previousLat, previousLon));
                        target.Add(MapPosition(lat, lon));
                    }

                    previousLon = lon;
                    previousLat = lat;
                }
            }
        }
        catch (Exception error) when (error is IOException or EndOfStreamException)
        {
            GD.PrintErr("Could not read river lines: " + error.Message);
            return;
        }

        AddLineMesh(major, MajorRiverColour, null);
        AddLineMesh(minor, MinorRiverColour, _minorRivers);
    }

    private void AddLineMesh(List<Vector2> points, Color colour, List<MeshInstance2D>? track)
    {
        if (points.Count == 0)
        {
            return;
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = points.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Lines, arrays);

        foreach (Node2D copy in _copies)
        {
            var instance = new MeshInstance2D { Mesh = mesh, Modulate = colour };
            copy.AddChild(instance);
            track?.Add(instance);
        }
    }

    /// <summary>Recolours every cell.</summary>
    public void SetColours(Func<int, Color> colourOfCell)
    {
        if (_map == null || _image == null || _texture == null)
        {
            return;
        }

        for (int cell = 0; cell < _map.CellCount; cell++)
        {
            Color colour = colourOfCell(cell);
            int offset = cell * 3;
            _pixels[offset] = (byte)colour.R8;
            _pixels[offset + 1] = (byte)colour.G8;
            _pixels[offset + 2] = (byte)colour.B8;
        }

        _image.SetData(TextureSize, TextureSize, false, Image.Format.Rgb8, _pixels);
        _texture.Update(_image);
    }

    /// <summary>The cell under a point of the map, or -1 if the point is off the map.</summary>
    public int CellAt(Vector2 mapPosition)
    {
        if (_map == null)
        {
            return -1;
        }

        double longitude = WrapX(mapPosition.X) / PixelsPerDegree;
        double latitude = -mapPosition.Y / PixelsPerDegree;
        if (Math.Abs(latitude) > MaxLatitude)
        {
            return -1;
        }

        int cell = _map.Grid.FindCell(latitude, longitude);
        return Math.Abs(_map.Grid.Latitude(cell)) > MaxLatitude ? -1 : cell;
    }

    /// <summary>Where a latitude and longitude sit on the map.</summary>
    public static Vector2 MapPosition(double latitude, double longitude) =>
        new Vector2((float)longitude * PixelsPerDegree, (float)-latitude * PixelsPerDegree);

    /// <summary>Outlines one cell, or none when given -1.</summary>
    public void SetSelectedCell(int cell)
    {
        if (_map == null || _outlines[0] == null)
        {
            return;
        }

        if (cell < 0 || Math.Abs(_map.Grid.Latitude(cell)) > MaxLatitude)
        {
            foreach (CellOutline outline in _outlines)
            {
                outline.SetPoints(Array.Empty<Vector2>());
            }

            return;
        }

        Span<Vec3> corners = stackalloc Vec3[GeodesicGrid.MaxNeighbours];
        Span<Vector2> points = stackalloc Vector2[GeodesicGrid.MaxNeighbours];
        int count = ProjectCell(_map.Grid, cell, corners, points);
        var closed = new Vector2[count + 1];
        for (int k = 0; k < count; k++)
        {
            closed[k] = points[k];
        }

        closed[count] = points[0];
        foreach (CellOutline outline in _outlines)
        {
            outline.SetPoints(closed);
        }
    }

    // Projects a cell's corners to map positions, keeping them on the same side as the cell centre.
    private static int ProjectCell(GeodesicGrid grid, int cell, Span<Vec3> corners, Span<Vector2> points)
    {
        int count = grid.GetCorners(cell, corners);
        double centreLongitude = grid.Longitude(cell);
        for (int k = 0; k < count; k++)
        {
            Vec3 c = corners[k];
            double latitude = Math.Asin(Math.Clamp(c.Z, -1.0, 1.0)) * 180.0 / Math.PI;
            double longitude = Math.Atan2(c.Y, c.X) * 180.0 / Math.PI;
            if (longitude - centreLongitude > 180.0)
            {
                longitude -= 360.0;
            }
            else if (longitude - centreLongitude < -180.0)
            {
                longitude += 360.0;
            }

            points[k] = MapPosition(latitude, longitude);
        }

        return count;
    }

    private static void AddPolygon(
        List<Vector2> vertices,
        List<Vector2> uvs,
        List<int> indices,
        Span<Vector2> points,
        int count,
        Vector2 uv)
    {
        int first = vertices.Count;
        for (int k = 0; k < count; k++)
        {
            vertices.Add(points[k]);
            uvs.Add(uv);
        }

        for (int k = 1; k < count - 1; k++)
        {
            indices.Add(first);
            indices.Add(first + k);
            indices.Add(first + k + 1);
        }
    }

    private void FlushChunk(List<Vector2> vertices, List<Vector2> uvs, List<int> indices)
    {
        if (vertices.Count == 0)
        {
            return;
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices.ToArray();
        arrays[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

        foreach (Node2D copy in _copies)
        {
            copy.AddChild(new MeshInstance2D
            {
                Mesh = mesh,
                Texture = _texture,
                TextureFilter = TextureFilterEnum.Nearest,
            });
        }

        vertices.Clear();
        uvs.Clear();
        indices.Clear();
    }
}

/// <summary>Draws the outline of the selected cell on top of the map.</summary>
public partial class CellOutline : Node2D
{
    private Vector2[] _points = Array.Empty<Vector2>();

    public void SetPoints(Vector2[] points)
    {
        _points = points;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_points.Length >= 2)
        {
            DrawPolyline(_points, Colors.White, 2f);
            DrawPolyline(_points, Colors.Black, -1f);
        }
    }
}

/// <summary>
/// Draws settlements as dots, and their names once the view is zoomed in far enough. One layer
/// sits in each copy of the map; dots and text keep the same size on screen at every zoom.
/// </summary>
public partial class SettlementLayer : Node2D
{
    private const float NameMinZoom = 7f;
    private const int MaxNames = 60;
    private const int FontSize = 12;

    // A cell is about 0.46 degrees across, so this many screen pixels per unit of zoom.
    private const float CellPixelsPerZoom = 0.46f * MapView.PixelsPerDegree;

    private Vector2[] _positions = Array.Empty<Vector2>();
    private Vector2[] _routeEnds = Array.Empty<Vector2>();
    private float[] _routeValues = Array.Empty<float>();
    private bool _routesVisible;
    private static readonly Color RouteColour = new Color(0.95f, 0.55f, 0.15f);
    private string[] _names = Array.Empty<string>();
    private float[] _populations = Array.Empty<float>();
    private float _zoom = 1f;
    private Rect2 _view;

    public void SetSettlements(Vector2[] positions, string[] names, float[] populations)
    {
        _positions = positions;
        _names = names;
        _populations = populations;
        QueueRedraw();
    }

    public void SetRoutes(Vector2[] ends, float[] values)
    {
        _routeEnds = ends;
        _routeValues = values;
        if (_routesVisible)
        {
            QueueRedraw();
        }
    }

    public void SetRoutesVisible(bool visible)
    {
        if (visible != _routesVisible)
        {
            _routesVisible = visible;
            QueueRedraw();
        }
    }

    /// <summary>Gives the zoom and the visible part of the map, in this copy's own coordinates.</summary>
    public void SetView(float zoom, Rect2 view)
    {
        bool namesShown = zoom >= NameMinZoom;
        if (zoom != _zoom || (namesShown && view != _view))
        {
            _zoom = zoom;
            _view = view;
            QueueRedraw();
        }
    }

    public override void _Draw()
    {
        if (_positions.Length == 0 || !IsVisibleInTree())
        {
            return;
        }

        // Dots grow with the cells on screen, so at the world view they stay small marks that do not hide the map.
        float unit = 1f / _zoom;
        if (_routesVisible)
        {
            // Thicker lines for routes that carry more; skip any that would cross the map's edge.
            for (int i = 0; i < _routeValues.Length; i++)
            {
                Vector2 a = _routeEnds[2 * i];
                Vector2 b = _routeEnds[2 * i + 1];
                if (Math.Abs(a.X - b.X) > MapView.WorldWidth / 2f)
                {
                    continue;
                }

                float width = (_routeValues[i] >= 100f ? 4f : _routeValues[i] >= 10f ? 2.5f : 1.2f) * unit;
                DrawLine(a, b, RouteColour, width);
            }
        }

        float cellPixels = CellPixelsPerZoom * _zoom;
        float baseHalf = Math.Clamp(0.12f * cellPixels, 0.5f, 2.5f);
        bool outline = baseHalf >= 1.2f;
        for (int i = 0; i < _positions.Length; i++)
        {
            float half = (_populations[i] >= 300f ? baseHalf : 0.7f * baseHalf) * unit;
            Vector2 p = _positions[i];
            if (outline)
            {
                DrawRect(new Rect2(p.X - half - unit, p.Y - half - unit, 2f * (half + unit), 2f * (half + unit)), Colors.Black);
            }

            DrawRect(new Rect2(p.X - half, p.Y - half, 2f * half, 2f * half), Colors.White);
        }

        if (_zoom < NameMinZoom)
        {
            return;
        }

        // Largest first, skipping any name that would overlap one already drawn.
        Font font = ThemeDB.FallbackFont;
        var order = Enumerable.Range(0, _positions.Length)
            .Where(i => _view.HasPoint(_positions[i]))
            .OrderByDescending(i => _populations[i])
            .ToList();
        var taken = new List<Rect2>();
        var scale = new Vector2(unit, unit);
        foreach (int i in order)
        {
            if (taken.Count >= MaxNames)
            {
                break;
            }

            Vector2 size = font.GetStringSize(_names[i], HorizontalAlignment.Left, -1, FontSize) * unit;
            var box = new Rect2(_positions[i] + new Vector2(5f * unit, -size.Y * 0.5f), size);
            if (taken.Any(t => t.Intersects(box)))
            {
                continue;
            }

            taken.Add(box);
            DrawSetTransform(_positions[i] + new Vector2(5f * unit, 4f * unit), 0f, scale);
            DrawStringOutline(font, Vector2.Zero, _names[i], HorizontalAlignment.Left, -1, FontSize, 3, Colors.Black);
            DrawString(font, Vector2.Zero, _names[i], HorizontalAlignment.Left, -1, FontSize, Colors.White);
        }

        DrawSetTransform(Vector2.Zero, 0f, Vector2.One);
    }
}
