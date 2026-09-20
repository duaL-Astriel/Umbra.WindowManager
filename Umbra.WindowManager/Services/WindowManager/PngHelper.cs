using System;
using System.IO;
using System.IO.Compression;

namespace Umbra.WindowManager.Services.WindowManager;

/// <summary>
/// Lightweight pure-managed PNG encoder for converting raw in-game texture pixels into
/// valid PNG bytes for display in the Umbra toolbar without native library dependencies.
/// </summary>
public static class PngHelper
{
    private static readonly uint[] CrcTable = new uint[256];

    static PngHelper()
    {
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }
            CrcTable[i] = c;
        }
    }

    private static uint CalculateCrc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        for (var i = 0; i < type.Length; i++)
            crc = CrcTable[(crc ^ type[i]) & 0xFF] ^ (crc >> 8);
        for (var i = 0; i < data.Length; i++)
            crc = CrcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    private static void WriteChunk(Stream stream, string typeStr, byte[] data)
    {
        var type = System.Text.Encoding.ASCII.GetBytes(typeStr);
        var length = (uint)data.Length;

        // 4 bytes big-endian length
        stream.WriteByte((byte)(length >> 24));
        stream.WriteByte((byte)(length >> 16));
        stream.WriteByte((byte)(length >> 8));
        stream.WriteByte((byte)length);

        // 4 bytes chunk type
        stream.Write(type, 0, 4);

        // Data
        stream.Write(data, 0, data.Length);

        // 4 bytes CRC
        var crc = CalculateCrc32(type, data);
        stream.WriteByte((byte)(crc >> 24));
        stream.WriteByte((byte)(crc >> 16));
        stream.WriteByte((byte)(crc >> 8));
        stream.WriteByte((byte)crc);
    }

    /// <summary>
    /// Encodes raw 32-bit BGRA (or RGBA) pixel data into a standard PNG byte array.
    /// </summary>
    public static byte[] EncodeBgraToPng(byte[] rawBgra, int width, int height, bool isBgra = true)
    {
        if (rawBgra == null || width <= 0 || height <= 0 || rawBgra.Length < width * height * 4)
            return [];

        using var ms = new MemoryStream();

        // 8-byte PNG signature
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], 0, 8);

        // IHDR chunk
        var ihdr = new byte[13];
        ihdr[0] = (byte)(width >> 24);
        ihdr[1] = (byte)(width >> 16);
        ihdr[2] = (byte)(width >> 8);
        ihdr[3] = (byte)width;
        ihdr[4] = (byte)(height >> 24);
        ihdr[5] = (byte)(height >> 16);
        ihdr[6] = (byte)(height >> 8);
        ihdr[7] = (byte)height;
        ihdr[8] = 8; // 8 bits per channel
        ihdr[9] = 6; // Color type 6: RGBA
        ihdr[10] = 0; // Compression (deflate)
        ihdr[11] = 0; // Filter (standard)
        ihdr[12] = 0; // Interlace (none)
        WriteChunk(ms, "IHDR", ihdr);

        // Raw scanlines with filter byte 0 (None)
        var rowSize = width * 4;
        var uncompressedData = new byte[(rowSize + 1) * height];
        var srcIdx = 0;
        var dstIdx = 0;

        for (var y = 0; y < height; y++)
        {
            uncompressedData[dstIdx++] = 0; // Filter None
            for (var x = 0; x < width; x++)
            {
                var b = rawBgra[srcIdx];
                var g = rawBgra[srcIdx + 1];
                var r = rawBgra[srcIdx + 2];
                var a = rawBgra[srcIdx + 3];
                srcIdx += 4;

                if (isBgra)
                {
                    uncompressedData[dstIdx++] = r;
                    uncompressedData[dstIdx++] = g;
                    uncompressedData[dstIdx++] = b;
                    uncompressedData[dstIdx++] = a;
                }
                else
                {
                    uncompressedData[dstIdx++] = b;
                    uncompressedData[dstIdx++] = g;
                    uncompressedData[dstIdx++] = r;
                    uncompressedData[dstIdx++] = a;
                }
            }
        }

        // Compress scanlines with ZLibStream
        using (var compressedMs = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressedMs, CompressionLevel.Fastest, leaveOpen: true))
            {
                zlib.Write(uncompressedData, 0, uncompressedData.Length);
            }
            WriteChunk(ms, "IDAT", compressedMs.ToArray());
        }

        // IEND chunk
        WriteChunk(ms, "IEND", []);

        return ms.ToArray();
    }
}
