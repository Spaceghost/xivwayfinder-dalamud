using System.Buffers.Binary;

namespace XivWayfinder.Core;

/// <summary>Reads image dimensions before any image decoder or GPU allocation sees user model data.</summary>
internal static class GuideImageBounds
{
    public static (int Width, int Height) Read(string mime, ReadOnlySpan<byte> data)
    {
        if (mime == "image/png")
        {
            ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
            if (data.Length < 33 || !data[..8].SequenceEqual(signature) || BinaryPrimitives.ReadUInt32BigEndian(data[8..]) != 13 || !data.Slice(12, 4).SequenceEqual("IHDR"u8))
                throw new InvalidDataException("Invalid embedded PNG header.");
            var width = BinaryPrimitives.ReadUInt32BigEndian(data[16..]); var height = BinaryPrimitives.ReadUInt32BigEndian(data[20..]);
            if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue) throw new InvalidDataException("Invalid embedded PNG dimensions.");
            return ((int)width, (int)height);
        }
        if (mime != "image/jpeg" || data.Length < 4 || data[0] != 0xff || data[1] != 0xd8)
            throw new InvalidDataException("Invalid embedded JPEG header.");
        var offset = 2;
        while (offset < data.Length)
        {
            if (data[offset++] != 0xff) throw new InvalidDataException("Invalid embedded JPEG marker.");
            while (offset < data.Length && data[offset] == 0xff) offset++;
            if (offset >= data.Length) break;
            var marker = data[offset++];
            if (marker is 0xd9 or 0xda) break;
            if (marker is 0x01 or (>= 0xd0 and <= 0xd8)) continue;
            if (offset + 2 > data.Length) break;
            var length = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            if (length < 2 || length > data.Length - offset) break;
            if (marker is 0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc5 or 0xc6 or 0xc7 or 0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf)
            {
                if (length < 8) break;
                var height = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 3)..]); var width = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 5)..]);
                if (width == 0 || height == 0) break;
                return (width, height);
            }
            offset += length;
        }
        throw new InvalidDataException("The embedded JPEG has no valid frame dimensions.");
    }
}
