using System;
using System.Collections.Generic;
using Godot;
using Sim.Core.Geo;
using Sim.Core.Map;

namespace Viewer;

/// <summary>
/// Draws the world as a flat map: longitude across, latitude down, clipped near the poles.
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
    private CellOutline? _outline;

    public static float HalfWidth => 180f * PixelsPerDegree;

    public static float HalfHeight => (float)MaxLatitude * PixelsPerDegree;

    /// <summary>Builds the map polygons. Call once.</summary>
    public void Build(WorldMap map)
    {
        _map = map;
        GeodesicGrid grid = map.Grid;
        if (grid.CellCount > TextureSize * TextureSize)
        {
            throw new InvalidOperationException("The map has more cells than the colour texture can hold.");
        }

        _image = Image.CreateFromData(TextureSize, TextureSize, false, Image.Format.Rgb8, _pixels);
        _texture = ImageTexture.CreateFromImage(_image);

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
            float minX = float.MaxValue;
            float maxX = float.MinValue;
            for (int k = 0; k < count; k++)
            {
                minX = Math.Min(minX, points[k].X);
                maxX = Math.Max(maxX, points[k].X);
            }

            var uv = new Vector2(
                (cell % TextureSize + 0.5f) / TextureSize,
                (cell / TextureSize + 0.5f) / TextureSize);

            AddPolygon(vertices, uvs, indices, points, count, uv, 0f);

            // Cells on the left or right edge are drawn again on the other side so the edges are filled.
            if (minX < -HalfWidth)
            {
                AddPolygon(vertices, uvs, indices, points, count, uv, 360f * PixelsPerDegree);
            }
            else if (maxX > HalfWidth)
            {
                AddPolygon(vertices, uvs, indices, points, count, uv, -360f * PixelsPerDegree);
            }

            cellsInChunk++;
            if (cellsInChunk >= CellsPerChunk)
            {
                FlushChunk(vertices, uvs, indices);
                cellsInChunk = 0;
            }
        }

        FlushChunk(vertices, uvs, indices);

        _outline = new CellOutline();
        AddChild(_outline);
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

        double longitude = mapPosition.X / PixelsPerDegree;
        double latitude = -mapPosition.Y / PixelsPerDegree;
        if (Math.Abs(latitude) > MaxLatitude || Math.Abs(longitude) > 180.0)
        {
            return -1;
        }

        return _map.Grid.FindCell(latitude, longitude);
    }

    /// <summary>Where a latitude and longitude sit on the map.</summary>
    public static Vector2 MapPosition(double latitude, double longitude) =>
        new Vector2((float)longitude * PixelsPerDegree, (float)-latitude * PixelsPerDegree);

    /// <summary>Outlines one cell, or none when given -1.</summary>
    public void SetSelectedCell(int cell)
    {
        if (_map == null || _outline == null)
        {
            return;
        }

        if (cell < 0 || Math.Abs(_map.Grid.Latitude(cell)) > MaxLatitude)
        {
            _outline.SetPoints(Array.Empty<Vector2>());
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
        _outline.SetPoints(closed);
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
        Vector2 uv,
        float shiftX)
    {
        int first = vertices.Count;
        for (int k = 0; k < count; k++)
        {
            vertices.Add(new Vector2(points[k].X + shiftX, points[k].Y));
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

        var instance = new MeshInstance2D
        {
            Mesh = mesh,
            Texture = _texture,
            TextureFilter = TextureFilterEnum.Nearest,
        };
        AddChild(instance);

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
