namespace Sim.Core.Geo;

/// <summary>
/// A grid of nearly equal cells covering a sphere, made by subdividing an icosahedron.
/// Every cell is a hexagon except 12 pentagons at the icosahedron's corners.
/// A cell is the set of points closer to its centre than to any other centre.
/// Cell ids run from 0 to CellCount - 1 and are stable for a given frequency.
/// </summary>
public sealed class GeodesicGrid
{
    /// <summary>Subdivision frequency used for Earth: 225,002 cells about 51 km across.</summary>
    public const int EarthFrequency = 150;

    /// <summary>Mean radius of Earth in kilometres.</summary>
    public const double EarthRadiusKm = 6371.0088;

    public const int MaxNeighbours = 6;

    private const int IcoCornerCount = 12;
    private const int IcoFaceCount = 20;
    private const int IcoEdgeCount = 30;

    private readonly Vec3[] _centre;
    private readonly double[] _latitude;
    private readonly double[] _longitude;
    private readonly double[] _areaKm2;
    private readonly byte[] _neighbourCount;
    private readonly int[] _neighbours;

    // Per icosahedron face: cell id at integer face coordinates (i, j), index i * (Frequency + 1) + j.
    private readonly int[][] _faceTables;
    private readonly Vec3[] _icoCorners;
    private readonly int[][] _icoFaces;
    private readonly Vec3[] _faceCentres;

    public int Frequency { get; }

    public int CellCount { get; }

    private GeodesicGrid(int frequency)
    {
        Frequency = frequency;
        CellCount = 10 * frequency * frequency + 2;
        _centre = new Vec3[CellCount];
        _latitude = new double[CellCount];
        _longitude = new double[CellCount];
        _areaKm2 = new double[CellCount];
        _neighbourCount = new byte[CellCount];
        _neighbours = new int[CellCount * MaxNeighbours];
        _faceTables = new int[IcoFaceCount][];
        _icoCorners = BuildIcoCorners();
        _icoFaces = BuildIcoFaces();
        _faceCentres = new Vec3[IcoFaceCount];
    }

    /// <summary>Builds a grid with 10 * frequency^2 + 2 cells.</summary>
    public static GeodesicGrid Create(int frequency)
    {
        if (frequency < 1 || frequency > 2000)
        {
            throw new ArgumentOutOfRangeException(nameof(frequency), "Frequency must be between 1 and 2000.");
        }

        var grid = new GeodesicGrid(frequency);
        grid.BuildCells();
        grid.BuildNeighbours();
        grid.OrderNeighbours();
        grid.ComputeAreasAndCoordinates();
        return grid;
    }

    public Vec3 Centre(int cell) => _centre[cell];

    /// <summary>Latitude of the cell centre in degrees, -90 to 90.</summary>
    public double Latitude(int cell) => _latitude[cell];

    /// <summary>Longitude of the cell centre in degrees, -180 to 180.</summary>
    public double Longitude(int cell) => _longitude[cell];

    /// <summary>True surface area of the cell on Earth, in square kilometres.</summary>
    public double AreaKm2(int cell) => _areaKm2[cell];

    /// <summary>5 for the twelve pentagons, 6 for every other cell.</summary>
    public int NeighbourCount(int cell) => _neighbourCount[cell];

    /// <summary>Neighbouring cell ids, counter-clockwise seen from outside the sphere.</summary>
    public ReadOnlySpan<int> Neighbours(int cell) =>
        new ReadOnlySpan<int>(_neighbours, cell * MaxNeighbours, _neighbourCount[cell]);

    /// <summary>Distance between two cell centres along Earth's surface, in kilometres.</summary>
    public double DistanceKm(int cellA, int cellB) =>
        EarthRadiusKm * Vec3.AngleBetween(_centre[cellA], _centre[cellB]);

    /// <summary>
    /// Writes the corner points of the cell's outline into <paramref name="destination"/>,
    /// counter-clockwise, and returns how many were written (the cell's neighbour count).
    /// Corner k lies between neighbour k and neighbour k + 1.
    /// </summary>
    public int GetCorners(int cell, Span<Vec3> destination)
    {
        int m = _neighbourCount[cell];
        if (destination.Length < m)
        {
            throw new ArgumentException("Destination is too small.", nameof(destination));
        }

        int start = cell * MaxNeighbours;
        Vec3 p = _centre[cell];
        for (int x = 0; x < m; x++)
        {
            Vec3 a = _centre[_neighbours[start + x]];
            Vec3 b = _centre[_neighbours[start + (x + 1) % m]];
            destination[x] = Circumcentre(p, a, b);
        }

        return m;
    }

    /// <summary>The cell containing the given latitude and longitude in degrees.</summary>
    public int FindCell(double latitudeDeg, double longitudeDeg) =>
        FindCell(Vec3.FromLatLon(latitudeDeg, longitudeDeg));

    /// <summary>The cell whose centre is nearest to the given direction from the sphere's centre.</summary>
    public int FindCell(Vec3 point)
    {
        Vec3 p = point.Normalized();
        int n = Frequency;

        // The icosahedron face containing a point is the one with the nearest face centre.
        int face = 0;
        double best = double.NegativeInfinity;
        for (int f = 0; f < IcoFaceCount; f++)
        {
            double d = Vec3.Dot(p, _faceCentres[f]);
            if (d > best)
            {
                best = d;
                face = f;
            }
        }

        // Rough guess of the face coordinates, then walk to the nearest centre.
        Vec3 a = _icoCorners[_icoFaces[face][0]];
        Vec3 b = _icoCorners[_icoFaces[face][1]];
        Vec3 c = _icoCorners[_icoFaces[face][2]];
        double det = Vec3.Dot(a, Vec3.Cross(b, c));
        double wa = Math.Max(0.0, Vec3.Dot(p, Vec3.Cross(b, c)) / det);
        double wb = Math.Max(0.0, Vec3.Dot(p, Vec3.Cross(c, a)) / det);
        double wc = Math.Max(0.0, Vec3.Dot(p, Vec3.Cross(a, b)) / det);
        double sum = wa + wb + wc;
        int i = 0;
        int j = 0;
        if (sum > 0.0)
        {
            i = Math.Clamp((int)Math.Round(wa / sum * n), 0, n);
            j = Math.Clamp((int)Math.Round(wb / sum * n), 0, n - i);
        }

        return FindCellFrom(_faceTables[face][i * (n + 1) + j], p);
    }

    /// <summary>
    /// The cell whose centre is nearest to <paramref name="unitPoint"/>, searching outward from
    /// <paramref name="startCell"/>. Fast when the start is close. The point must have length 1.
    /// </summary>
    public int FindCellFrom(int startCell, Vec3 unitPoint)
    {
        int cell = startCell;
        while (true)
        {
            double bestDot = Vec3.Dot(_centre[cell], unitPoint);
            int bestCell = cell;
            int start = cell * MaxNeighbours;
            int m = _neighbourCount[cell];
            for (int x = 0; x < m; x++)
            {
                int u = _neighbours[start + x];
                double d = Vec3.Dot(_centre[u], unitPoint);
                if (d > bestDot)
                {
                    bestDot = d;
                    bestCell = u;
                }
            }

            if (bestCell == cell)
            {
                return cell;
            }

            cell = bestCell;
        }
    }

    private static Vec3[] BuildIcoCorners()
    {
        // Corner 0 is the north pole, 1-5 the upper ring, 6-10 the lower ring, 11 the south pole.
        double ringLat = Math.Atan(0.5) * 180.0 / Math.PI;
        var corners = new Vec3[IcoCornerCount];
        corners[0] = new Vec3(0.0, 0.0, 1.0);
        for (int k = 0; k < 5; k++)
        {
            corners[1 + k] = Vec3.FromLatLon(ringLat, 72.0 * k);
            corners[6 + k] = Vec3.FromLatLon(-ringLat, 36.0 + 72.0 * k);
        }

        corners[11] = new Vec3(0.0, 0.0, -1.0);
        return corners;
    }

    private static int[][] BuildIcoFaces()
    {
        var faces = new int[IcoFaceCount][];
        for (int k = 0; k < 5; k++)
        {
            int k1 = (k + 1) % 5;
            faces[k] = new[] { 0, 1 + k, 1 + k1 };
            faces[5 + k] = new[] { 1 + k, 6 + k, 1 + k1 };
            faces[10 + k] = new[] { 6 + k, 1 + k1, 6 + k1 };
            faces[15 + k] = new[] { 11, 6 + k, 6 + k1 };
        }

        return faces;
    }

    private void BuildCells()
    {
        int n = Frequency;

        // Number the 30 icosahedron edges in order of first appearance.
        var edgeIds = new Dictionary<(int, int), int>();
        for (int f = 0; f < IcoFaceCount; f++)
        {
            int[] face = _icoFaces[f];
            for (int e = 0; e < 3; e++)
            {
                int p = face[e];
                int q = face[(e + 1) % 3];
                var key = (Math.Min(p, q), Math.Max(p, q));
                if (!edgeIds.ContainsKey(key))
                {
                    edgeIds.Add(key, edgeIds.Count);
                }
            }
        }

        if (edgeIds.Count != IcoEdgeCount)
        {
            throw new InvalidOperationException("Icosahedron edge count is wrong.");
        }

        // Ids: 12 corners, then points along edges, then points inside faces.
        double omega = Math.Acos(Vec3.Dot(_icoCorners[0], _icoCorners[1]));
        var assigned = new bool[CellCount];
        int next = IcoCornerCount + IcoEdgeCount * (n - 1);

        for (int f = 0; f < IcoFaceCount; f++)
        {
            int ca = _icoFaces[f][0];
            int cb = _icoFaces[f][1];
            int cc = _icoFaces[f][2];
            Vec3 a = _icoCorners[ca];
            Vec3 b = _icoCorners[cb];
            Vec3 c = _icoCorners[cc];
            _faceCentres[f] = (a + b + c).Normalized();

            var table = new int[(n + 1) * (n + 1)];
            Array.Fill(table, -1);

            for (int i = 0; i <= n; i++)
            {
                for (int j = 0; j <= n - i; j++)
                {
                    int k = n - i - j;
                    int id;
                    if (i == n)
                    {
                        id = ca;
                    }
                    else if (j == n)
                    {
                        id = cb;
                    }
                    else if (k == n)
                    {
                        id = cc;
                    }
                    else if (k == 0)
                    {
                        id = EdgeCellId(edgeIds, ca, cb, i, j);
                    }
                    else if (i == 0)
                    {
                        id = EdgeCellId(edgeIds, cb, cc, j, k);
                    }
                    else if (j == 0)
                    {
                        id = EdgeCellId(edgeIds, cc, ca, k, i);
                    }
                    else
                    {
                        id = next;
                        next++;
                    }

                    table[i * (n + 1) + j] = id;

                    if (!assigned[id])
                    {
                        // Sine weights space points evenly along edges and keep cell sizes close to equal.
                        double wi = Math.Sin(i * omega / n);
                        double wj = Math.Sin(j * omega / n);
                        double wk = Math.Sin(k * omega / n);
                        _centre[id] = (wi * a + wj * b + wk * c).Normalized();
                        assigned[id] = true;
                    }
                }
            }

            _faceTables[f] = table;
        }

        if (next != CellCount)
        {
            throw new InvalidOperationException("Cell count is wrong.");
        }
    }

    // Id of the point on the edge between corners p and q that carries weightP steps toward p
    // and weightQ steps toward q. Points are numbered from the lower-numbered corner.
    private int EdgeCellId(Dictionary<(int, int), int> edgeIds, int p, int q, int weightP, int weightQ)
    {
        int edge;
        int t;
        if (p < q)
        {
            edge = edgeIds[(p, q)];
            t = weightQ;
        }
        else
        {
            edge = edgeIds[(q, p)];
            t = weightP;
        }

        return IcoCornerCount + edge * (Frequency - 1) + (t - 1);
    }

    private void BuildNeighbours()
    {
        int n = Frequency;
        Array.Fill(_neighbours, -1);
        for (int f = 0; f < IcoFaceCount; f++)
        {
            int[] table = _faceTables[f];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n - i; j++)
                {
                    int a = table[i * (n + 1) + j];
                    int b = table[(i + 1) * (n + 1) + j];
                    int c = table[i * (n + 1) + j + 1];
                    Link(a, b);
                    Link(a, c);
                    Link(b, c);
                    if (i + j < n - 1)
                    {
                        int d = table[(i + 1) * (n + 1) + j + 1];
                        Link(b, d);
                        Link(c, d);
                    }
                }
            }
        }
    }

    private void Link(int a, int b)
    {
        AddNeighbour(a, b);
        AddNeighbour(b, a);
    }

    private void AddNeighbour(int cell, int other)
    {
        int start = cell * MaxNeighbours;
        int count = _neighbourCount[cell];
        for (int x = 0; x < count; x++)
        {
            if (_neighbours[start + x] == other)
            {
                return;
            }
        }

        if (count == MaxNeighbours)
        {
            throw new InvalidOperationException("A cell has more than six neighbours.");
        }

        _neighbours[start + count] = other;
        _neighbourCount[cell] = (byte)(count + 1);
    }

    // Sorts each cell's neighbours counter-clockwise, starting from the lowest id.
    private void OrderNeighbours()
    {
        Span<int> ids = stackalloc int[MaxNeighbours];
        Span<double> angles = stackalloc double[MaxNeighbours];

        for (int cell = 0; cell < CellCount; cell++)
        {
            int start = cell * MaxNeighbours;
            int m = _neighbourCount[cell];
            Vec3 p = _centre[cell];

            int first = _neighbours[start];
            for (int x = 1; x < m; x++)
            {
                first = Math.Min(first, _neighbours[start + x]);
            }

            Vec3 q0 = _centre[first];
            Vec3 e1 = (q0 - Vec3.Dot(q0, p) * p).Normalized();
            Vec3 e2 = Vec3.Cross(p, e1);

            for (int x = 0; x < m; x++)
            {
                int u = _neighbours[start + x];
                double angle = 0.0;
                if (u != first)
                {
                    Vec3 q = _centre[u];
                    angle = Math.Atan2(Vec3.Dot(q, e2), Vec3.Dot(q, e1));
                    if (angle < 0.0)
                    {
                        angle += 2.0 * Math.PI;
                    }
                }

                // Insertion sort by angle.
                int pos = x;
                while (pos > 0 && angles[pos - 1] > angle)
                {
                    angles[pos] = angles[pos - 1];
                    ids[pos] = ids[pos - 1];
                    pos--;
                }

                angles[pos] = angle;
                ids[pos] = u;
            }

            for (int x = 0; x < m; x++)
            {
                _neighbours[start + x] = ids[x];
            }
        }
    }

    private void ComputeAreasAndCoordinates()
    {
        Span<Vec3> corners = stackalloc Vec3[MaxNeighbours];
        double r2 = EarthRadiusKm * EarthRadiusKm;

        for (int cell = 0; cell < CellCount; cell++)
        {
            Vec3 p = _centre[cell];
            _latitude[cell] = Math.Asin(Math.Clamp(p.Z, -1.0, 1.0)) * 180.0 / Math.PI;
            _longitude[cell] = Math.Atan2(p.Y, p.X) * 180.0 / Math.PI;

            int m = GetCorners(cell, corners);
            double steradians = 0.0;
            for (int x = 0; x < m; x++)
            {
                steradians += TriangleSolidAngle(p, corners[x], corners[(x + 1) % m]);
            }

            _areaKm2[cell] = steradians * r2;
        }
    }

    // Point on the sphere equally distant from three points, on their side of the sphere.
    private static Vec3 Circumcentre(Vec3 a, Vec3 b, Vec3 c)
    {
        Vec3 normal = Vec3.Cross(b - a, c - a).Normalized();
        if (Vec3.Dot(normal, a + b + c) < 0.0)
        {
            normal = -1.0 * normal;
        }

        return normal;
    }

    // Area of a triangle on the unit sphere.
    private static double TriangleSolidAngle(Vec3 a, Vec3 b, Vec3 c)
    {
        double numerator = Math.Abs(Vec3.Dot(a, Vec3.Cross(b, c)));
        double denominator = 1.0 + Vec3.Dot(a, b) + Vec3.Dot(b, c) + Vec3.Dot(c, a);
        return 2.0 * Math.Atan2(numerator, denominator);
    }
}
