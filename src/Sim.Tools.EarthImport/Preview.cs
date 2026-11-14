using System.Buffers.Binary;
using System.IO.Compression;
using Sim.Core.Geo;
using Sim.Core.Map;

namespace Sim.Tools.EarthImport;

/// <summary>Writes a flat picture of a world's terrain classes, for checking the import by eye.</summary>
public static class Preview
{
    private static readonly byte[][] Colours =
    {
        new byte[] { 24, 52, 110 },   // Ocean
        new byte[] { 70, 120, 190 },  // Coast
        new byte[] { 96, 150, 80 },   // Lowland
        new byte[] { 176, 160, 100 }, // Hills
        new byte[] { 120, 96, 84 },   // Mountains
    };

    public static void Write(WorldMap world, string path, int width, int height)
    {
        GeodesicGrid grid = world.Grid;

        // PNG rows: one filter byte, then RGB for each pixel.
        var raw = new byte[height * (1 + width * 3)];
        int offset = 0;
        int cell = 0;
        for (int y = 0; y < height; y++)
        {
            raw[offset++] = 0;
            double lat = 90.0 - (y + 0.5) * 180.0 / height;
            for (int x = 0; x < width; x++)
            {
                double lon = -180.0 + (x + 0.5) * 360.0 / width;
                cell = grid.FindCellFrom(cell, Vec3.FromLatLon(lat, lon));
                byte[] colour = Colours[(int)world.Terrain(cell)];
                raw[offset++] = colour[0];
                raw[offset++] = colour[1];
                raw[offset++] = colour[2];
            }
        }

        byte[] compressed;
        using (var buffer = new MemoryStream())
        {
            using (var zlib = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(raw, 0, raw.Length);
            }

            compressed = buffer.ToArray();
        }

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), height);
        header[8] = 8;  // bits per channel
        header[9] = 2;  // RGB
        header[10] = 0;
        header[11] = 0;
        header[12] = 0;

        using var file = File.Create(path);
        file.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        WriteChunk(file, "IHDR", header);
        WriteChunk(file, "IDAT", compressed);
        WriteChunk(file, "IEND", Array.Empty<byte>());
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var typeBytes = new byte[] { (byte)type[0], (byte)type[1], (byte)type[2], (byte)type[3] };
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        stream.Write(typeBytes);
        stream.Write(data);

        uint crc = 0xFFFFFFFFu;
        crc = UpdateCrc(crc, typeBytes);
        crc = UpdateCrc(crc, data);
        var crcBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc ^ 0xFFFFFFFFu);
        stream.Write(crcBytes);
    }

    private static uint UpdateCrc(uint crc, byte[] data)
    {
        foreach (byte b in data)
        {
            crc ^= b;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1u) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return crc;
    }
}
