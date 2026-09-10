using System.Buffers.Binary;
using System.IO.Compression;

namespace Vxp;

/// <summary>Minimal PNG encoder for RGBA buffers, so exports need no image library.</summary>
internal static class PngWriter
{
    public static void Write(string path, ReadOnlySpan<byte> rgba, int width, int height, int scale = 1)
    {
        if (scale < 1) scale = 1;
        var outWidth = width * scale;
        var outHeight = height * scale;

        // Each scanline is prefixed with a filter byte; filter 0 means "no filtering".
        var raw = new byte[(outWidth * 4 + 1) * outHeight];
        var p = 0;
        for (var y = 0; y < outHeight; y++)
        {
            raw[p++] = 0;
            var sourceRow = (y / scale) * width * 4;
            for (var x = 0; x < outWidth; x++)
            {
                var source = sourceRow + (x / scale) * 4;
                raw[p++] = rgba[source];
                raw[p++] = rgba[source + 1];
                raw[p++] = rgba[source + 2];
                raw[p++] = rgba[source + 3];
            }
        }

        using var compressed = new MemoryStream();
        using (var deflate = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(raw);

        using var file = File.Create(path);
        file.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, outWidth);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], outHeight);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // colour type: truecolour with alpha
        ihdr[10] = 0; // deflate
        ihdr[11] = 0; // adaptive filtering
        ihdr[12] = 0; // no interlace
        WriteChunk(file, "IHDR", ihdr);
        WriteChunk(file, "IDAT", compressed.ToArray());
        WriteChunk(file, "IEND", []);
    }

    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);

        var payload = new byte[4 + data.Length];
        for (var i = 0; i < 4; i++) payload[i] = (byte)type[i];
        data.CopyTo(payload.AsSpan(4));
        stream.Write(payload);

        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(payload));
        stream.Write(crc);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
