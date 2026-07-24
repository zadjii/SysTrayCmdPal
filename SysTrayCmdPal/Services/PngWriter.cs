// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace SysTrayCmdPal.Services;

/// <summary>
/// Minimal, dependency-free PNG encoder for 32-bit RGBA pixel buffers.
/// Uses <see cref="ZLibStream"/> for the IDAT payload so no imaging library is required.
/// </summary>
internal static class PngWriter
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    /// <summary>Encode top-down, non-premultiplied RGBA (<paramref name="rgba"/>) to PNG bytes.</summary>
    public static byte[] Encode(byte[] rgba, int width, int height)
    {
        using var ms = new MemoryStream();
        ms.Write(Signature, 0, Signature.Length);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr[..4], width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.Slice(4, 4), height);
        ihdr[8] = 8;   // bit depth
        ihdr[9] = 6;   // color type: truecolor + alpha
        ihdr[10] = 0;  // compression
        ihdr[11] = 0;  // filter
        ihdr[12] = 0;  // interlace
        WriteChunk(ms, "IHDR", ihdr);

        byte[] raw = AddFilterBytes(rgba, width, height);
        byte[] compressed = Deflate(raw);
        WriteChunk(ms, "IDAT", compressed);

        WriteChunk(ms, "IEND", ReadOnlySpan<byte>.Empty);
        return ms.ToArray();
    }

    private static byte[] AddFilterBytes(byte[] rgba, int width, int height)
    {
        int stride = width * 4;
        byte[] raw = new byte[(stride + 1) * height];
        for (int y = 0; y < height; y++)
        {
            int src = y * stride;
            int dst = y * (stride + 1);
            raw[dst] = 0; // filter type: none
            Buffer.BlockCopy(rgba, src, raw, dst + 1, stride);
        }

        return raw;
    }

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data, 0, data.Length);
        }

        return output.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        stream.Write(len);

        Span<byte> typeBytes = stackalloc byte[4];
        for (int i = 0; i < 4; i++)
        {
            typeBytes[i] = (byte)type[i];
        }

        stream.Write(typeBytes);
        if (!data.IsEmpty)
        {
            stream.Write(data);
        }

        uint crc = Crc32(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    private static uint Crc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        crc = UpdateCrc(crc, type);
        crc = UpdateCrc(crc, data);
        return crc ^ 0xFFFFFFFF;
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
            {
                uint mask = (uint)(-(int)(crc & 1));
                crc = (crc >> 1) ^ (0xEDB88320 & mask);
            }
        }

        return crc;
    }
}
