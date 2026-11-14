namespace Sim.Core.Geo;

/// <summary>A 3D vector in double precision. Points on the globe are unit vectors.</summary>
public readonly struct Vec3
{
    public readonly double X;
    public readonly double Y;
    public readonly double Z;

    public Vec3(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

    public Vec3 Normalized()
    {
        double l = Length;
        return new Vec3(X / l, Y / l, Z / l);
    }

    public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static Vec3 operator *(double s, Vec3 a) => new Vec3(s * a.X, s * a.Y, s * a.Z);

    public static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    public static Vec3 Cross(Vec3 a, Vec3 b) => new Vec3(
        a.Y * b.Z - a.Z * b.Y,
        a.Z * b.X - a.X * b.Z,
        a.X * b.Y - a.Y * b.X);

    /// <summary>Angle in radians between two unit vectors, accurate for small and large angles.</summary>
    public static double AngleBetween(Vec3 a, Vec3 b) => Math.Atan2(Cross(a, b).Length, Dot(a, b));

    /// <summary>Unit vector for a latitude and longitude in degrees.</summary>
    public static Vec3 FromLatLon(double latitudeDeg, double longitudeDeg)
    {
        double lat = latitudeDeg * Math.PI / 180.0;
        double lon = longitudeDeg * Math.PI / 180.0;
        double c = Math.Cos(lat);
        return new Vec3(c * Math.Cos(lon), c * Math.Sin(lon), Math.Sin(lat));
    }
}
