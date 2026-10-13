using System.Buffers.Binary;
using System.Numerics;

namespace XivWayfinder.Core;

// Scalar port of meshoptimizer's MIT reference decoder by Jasper St. Pierre.
// Copyright (C) 2016-2026 Arseny Kapoulkine. Source revision:
// 4c203430ca565cb59a468a91922c76c208169536, js/meshopt_decoder_reference.js.
// See ThirdParty/meshoptimizer.LICENSE.md. Added bounded reads, size limits,
// exact stream consumption and validation of otherwise unspecified filter inputs.
internal static class GuideMeshoptDecoder
{
    public static byte[] Decode(ReadOnlySpan<byte> source, int count, int stride, string mode, string filter, bool khr)
    {
        var size = (long)count * stride;
        if (count <= 0 || stride <= 0 || size > GuideModelResources.MaxBytes || source.IsEmpty)
            throw Error("Invalid decoded size.");
        if (mode == "ATTRIBUTES")
        {
            if (stride is < 4 or > 256 || stride % 4 != 0) throw Error("Invalid attribute stride.");
            if (source[0] != 0xa0 && !(khr && source[0] == 0xa1)) throw Error("Unsupported attribute version.");
            if (filter is not ("NONE" or "OCTAHEDRAL" or "QUATERNION" or "EXPONENTIAL" or "COLOR")
                || filter == "COLOR" && !khr
                || filter is "OCTAHEDRAL" or "COLOR" && stride is not (4 or 8)
                || filter == "QUATERNION" && stride != 8) throw Error("Invalid attribute filter.");
        }
        else if (mode is "TRIANGLES" or "INDICES")
        {
            if (stride is not (2 or 4) || filter != "NONE" || mode == "TRIANGLES" && count % 3 != 0)
                throw Error("Invalid index layout.");
        }
        else throw Error("Unsupported compression mode.");
        var output = new byte[(int)size];
        if (mode == "ATTRIBUTES") { Attributes(source, output, count, stride); Filter(output, stride, filter); }
        else if (mode == "TRIANGLES") Triangles(source, output, count, stride);
        else Indices(source, output, count, stride);
        return output;
    }

    private static void Attributes(ReadOnlySpan<byte> source, Span<byte> output, int count, int stride)
    {
        var version = source[0] & 15;
        var tailSize = stride + (version == 1 ? stride / 4 : 0);
        var padding = Math.Max(tailSize, version == 0 ? 32 : 24);
        if (source.Length < 1 + padding) throw Error("Truncated attribute tail.");
        var input = new Cursor(source.Slice(1, source.Length - 1 - padding));
        Span<byte> previous = stackalloc byte[256];
        source.Slice(source.Length - tailSize, stride).CopyTo(previous);
        var channels = version == 1 ? source[^ (stride / 4)..] : default;
        foreach (var channel in channels)
            if ((channel & 15) > 2 || (channel & 15) != 2 && channel > 15) throw Error("Invalid attribute channel.");
        var blockMaximum = Math.Min((8192 / stride) & ~15, 256);
        ReadOnlySpan<byte> headerModes = [0, 2, 4, 8, 0, 1, 2, 4, 1, 2, 4, 8];
        // Each byte channel has its own padded block, including ignored elements in
        // the final group. Scratch storage is constant, independent of decoded size.
        Span<byte> deltas = stackalloc byte[8192];
        for (var first = 0; first < count; first += blockMaximum)
        {
            var blockCount = Math.Min(count - first, blockMaximum);
            var groups = (blockCount + 15) / 16;
            var control = version == 1 ? input.Take(stride / 4) : default;
            deltas.Clear();
            for (var channel = 0; channel < stride; channel++)
            {
                var lane = deltas.Slice(channel * blockMaximum, blockMaximum);
                var controlMode = version == 1 ? control[channel / 4] >> ((channel & 3) * 2) & 3 : 0;
                if (controlMode == 2) continue;
                if (controlMode == 3) { input.Take(blockCount).CopyTo(lane); continue; }
                var headers = input.Take((groups + 3) / 4);
                for (var group = 0; group < groups; group++)
                {
                    var code = headers[group / 4] >> ((group & 3) * 2) & 3;
                    var bits = headerModes[(version == 0 ? 0 : controlMode + 1) * 4 + code];
                    var destination = lane.Slice(group * 16, 16);
                    if (bits == 0) continue;
                    if (bits == 8) { input.Take(16).CopyTo(destination); continue; }
                    var packed = input.Take(bits * 2); var mask = (1 << bits) - 1;
                    for (var j = 0; j < 16; j++)
                    {
                        var shift = bits == 1 ? j & 7 : 8 - bits - (j * bits & 7);
                        var delta = packed[j * bits / 8] >> shift & mask;
                        destination[j] = delta == mask ? input.Byte() : (byte)delta;
                    }
                }
            }
            for (var element = 0; element < blockCount; element++)
                for (var channel = 0; channel < stride; channel += 4)
                {
                    var encoding = version == 1 ? channels[channel / 4] : 0;
                    var mode = encoding & 15;
                    if (mode == 0)
                    {
                        for (var b = channel; b < channel + 4; b++)
                            previous[b] = unchecked((byte)(previous[b] + Unzig(deltas[b * blockMaximum + element])));
                    }
                    else if (mode == 1)
                    {
                        for (var b = channel; b < channel + 4; b += 2)
                        {
                            var delta = (uint)(deltas[b * blockMaximum + element] | deltas[(b + 1) * blockMaximum + element] << 8);
                            var value = unchecked((ushort)(BinaryPrimitives.ReadUInt16LittleEndian(previous[b..]) + Unzig(delta)));
                            BinaryPrimitives.WriteUInt16LittleEndian(previous[b..], value);
                        }
                    }
                    else
                    {
                        uint delta = 0;
                        for (var b = 0; b < 4; b++) delta |= (uint)deltas[(channel + b) * blockMaximum + element] << (b * 8);
                        var value = BinaryPrimitives.ReadUInt32LittleEndian(previous[channel..]) ^ BitOperations.RotateRight(delta, encoding >> 4);
                        BinaryPrimitives.WriteUInt32LittleEndian(previous[channel..], value);
                    }
                    previous.Slice(channel, 4).CopyTo(output.Slice((first + element) * stride + channel, 4));
                }
        }
        input.Complete();
    }

    private static void Triangles(ReadOnlySpan<byte> source, Span<byte> output, int count, int stride)
    {
        var triangleCount = count / 3;
        if (source[0] != 0xe1 || source.Length < 1L + triangleCount + 16) throw Error("Invalid triangle stream.");
        var codes = source.Slice(1, triangleCount); var table = source[^16..];
        if (table[14] != 0 || table[15] != 0) throw Error("Invalid triangle lookup table.");
        foreach (var code in table) if ((code & 15) == 15 || (code >> 4) == 15) throw Error("Invalid triangle lookup table.");
        var input = new Cursor(source.Slice(1 + triangleCount, source.Length - triangleCount - 17));
        Span<uint> edges = stackalloc uint[32]; Span<uint> vertices = stackalloc uint[16];
        var edgeOffset = 0; var edgeCount = 0; var vertexOffset = 0; var vertexCount = 0;
        uint next = 0, last = 0;
        for (var i = 0; i < triangleCount; i++)
        {
            var hi = codes[i] >> 4; var lo = codes[i] & 15;
            uint a, b, c;
            if (hi < 15)
            {
                a = FifoRead(edges, edgeOffset, edgeCount, hi * 2);
                b = FifoRead(edges, edgeOffset, edgeCount, hi * 2 + 1);
                c = lo == 0 ? next++ : lo < 13 ? FifoRead(vertices, vertexOffset, vertexCount, lo)
                    : lo == 13 ? unchecked(--last) : lo == 14 ? unchecked(++last) : ReadIndex(ref input, ref last);
                if (lo == 0 || lo >= 13) FifoPush(vertices, ref vertexOffset, ref vertexCount, c);
                FifoPush(edges, ref edgeOffset, ref edgeCount, b); FifoPush(edges, ref edgeOffset, ref edgeCount, c);
                FifoPush(edges, ref edgeOffset, ref edgeCount, c); FifoPush(edges, ref edgeOffset, ref edgeCount, a);
            }
            else
            {
                var auxiliary = lo < 14 ? table[lo] : input.Byte();
                var z = auxiliary >> 4; var w = auxiliary & 15;
                if (lo >= 14 && auxiliary == 0) next = 0;
                a = lo == 15 ? ReadIndex(ref input, ref last) : next++;
                b = z == 0 ? next++ : z == 15 ? ReadIndex(ref input, ref last) : FifoRead(vertices, vertexOffset, vertexCount, z - 1);
                c = w == 0 ? next++ : w == 15 ? ReadIndex(ref input, ref last) : FifoRead(vertices, vertexOffset, vertexCount, w - 1);
                FifoPush(vertices, ref vertexOffset, ref vertexCount, a);
                if (z is 0 or 15) FifoPush(vertices, ref vertexOffset, ref vertexCount, b);
                if (w is 0 or 15) FifoPush(vertices, ref vertexOffset, ref vertexCount, c);
                FifoPush(edges, ref edgeOffset, ref edgeCount, a); FifoPush(edges, ref edgeOffset, ref edgeCount, b);
                FifoPush(edges, ref edgeOffset, ref edgeCount, b); FifoPush(edges, ref edgeOffset, ref edgeCount, c);
                FifoPush(edges, ref edgeOffset, ref edgeCount, c); FifoPush(edges, ref edgeOffset, ref edgeCount, a);
            }
            WriteIndex(output[(i * 3 * stride)..], stride, a);
            WriteIndex(output[((i * 3 + 1) * stride)..], stride, b);
            WriteIndex(output[((i * 3 + 2) * stride)..], stride, c);
        }
        input.Complete();
    }

    private static void Indices(ReadOnlySpan<byte> source, Span<byte> output, int count, int stride)
    {
        if (source[0] != 0xd1 || source.Length < 5) throw Error("Invalid index sequence.");
        var input = new Cursor(source[1..^4]); Span<uint> last = stackalloc uint[2]; last.Clear();
        for (var i = 0; i < count; i++)
        {
            var value = input.Varint(); var baseline = (int)(value & 1);
            last[baseline] = unchecked(last[baseline] + Unzig(value >> 1));
            WriteIndex(output[(i * stride)..], stride, last[baseline]);
        }
        input.Complete();
    }

    private static void Filter(Span<byte> data, int stride, string filter)
    {
        if (filter == "NONE") return;
        if (filter == "EXPONENTIAL")
        {
            for (var i = 0; i < data.Length; i += 4)
            {
                var encoded = BinaryPrimitives.ReadInt32LittleEndian(data[i..]); var exponent = encoded >> 24;
                if (exponent is < -100 or > 100) throw Error("Invalid exponential filter value.");
                var value = BitConverter.Int32BitsToSingle((exponent + 127) << 23) * ((encoded << 8) >> 8);
                BinaryPrimitives.WriteSingleLittleEndian(data[i..], value);
            }
            return;
        }
        var width = stride / 4;
        for (var i = 0; i < data.Length; i += stride)
        {
            var element = data.Slice(i, stride);
            var x = Signed(element, width); var y = Signed(element[width..], width);
            var z = Signed(element[(width * 2)..], width); var w = Signed(element[(width * 3)..], width);
            if (filter == "OCTAHEDRAL")
            {
                if (!AllOnes(z) || Math.Abs(x) > z || Math.Abs(y) > z) throw Error("Invalid octahedral filter value.");
                float fx = x, fy = y, fz = z - Math.Abs(x) - Math.Abs(y);
                var t = Math.Min(fz, 0); fx += fx >= 0 ? t : -t; fy += fy >= 0 ? t : -t;
                var scale = (width == 1 ? 127 : 32767) / MathF.Sqrt(fx * fx + fy * fy + fz * fz);
                WriteComponent(element, width, Round(fx * scale));
                WriteComponent(element[width..], width, Round(fy * scale));
                WriteComponent(element[(width * 2)..], width, Round(fz * scale));
            }
            else if (filter == "QUATERNION")
            {
                var one = w | 3;
                if (one < 7 || !AllOnes(one) || Math.Abs(x) > one || Math.Abs(y) > one || Math.Abs(z) > one)
                    throw Error("Invalid quaternion filter value.");
                var scale = 1f / MathF.Sqrt(2) / one;
                var fx = x * scale; var fy = y * scale; var fz = z * scale;
                var fw = MathF.Sqrt(Math.Max(0, 1 - fx * fx - fy * fy - fz * fz));
                var component = w & 3;
                WriteComponent(element[(((component + 1) & 3) * 2)..], 2, Round(fx * 32767));
                WriteComponent(element[(((component + 2) & 3) * 2)..], 2, Round(fy * 32767));
                WriteComponent(element[(((component + 3) & 3) * 2)..], 2, Round(fz * 32767));
                WriteComponent(element[(component * 2)..], 2, Round(fw * 32767));
            }
            else
            {
                var luma = Unsigned(element, width); var alpha = Unsigned(element[(width * 3)..], width);
                if (alpha < 2) throw Error("Invalid color filter scale.");
                var range = (1 << (BitOperations.Log2((uint)alpha) + 1)) - 1;
                var red = luma + y - z; var green = luma + z; var blue = luma - y - z;
                if (luma > range || Math.Abs(y) > range / 2 || Math.Abs(z) > range / 2
                    || red < 0 || green < 0 || blue < 0 || red > range || green > range || blue > range)
                    throw Error("Invalid color filter value.");
                var expandedAlpha = ((alpha << 1) & range) | (alpha & 1);
                var scale = (width == 1 ? 255f : 65535f) / range;
                WriteComponent(element, width, Round(red * scale)); WriteComponent(element[width..], width, Round(green * scale));
                WriteComponent(element[(width * 2)..], width, Round(blue * scale));
                WriteComponent(element[(width * 3)..], width, Round(expandedAlpha * scale));
            }
        }
    }

    private static bool AllOnes(int value) => value > 0 && (value & (value + 1)) == 0;
    private static int Round(float value) => (int)MathF.Round(value, MidpointRounding.AwayFromZero);
    private static int Signed(ReadOnlySpan<byte> value, int width) => width == 1 ? unchecked((sbyte)value[0]) : BinaryPrimitives.ReadInt16LittleEndian(value);
    private static int Unsigned(ReadOnlySpan<byte> value, int width) => width == 1 ? value[0] : BinaryPrimitives.ReadUInt16LittleEndian(value);
    private static void WriteComponent(Span<byte> value, int width, int component)
    { if (width == 1) value[0] = unchecked((byte)component); else BinaryPrimitives.WriteUInt16LittleEndian(value, unchecked((ushort)component)); }
    private static uint Unzig(uint value) => (value >> 1) ^ unchecked(0u - (value & 1));
    private static uint ReadIndex(ref Cursor input, ref uint last) => last = unchecked(last + Unzig(input.Varint()));
    private static void WriteIndex(Span<byte> output, int stride, uint value)
    { if (stride == 2) BinaryPrimitives.WriteUInt16LittleEndian(output, unchecked((ushort)value)); else BinaryPrimitives.WriteUInt32LittleEndian(output, value); }
    private static uint FifoRead(ReadOnlySpan<uint> fifo, int offset, int count, int index)
        => index < count ? fifo[(offset - 1 - index) & (fifo.Length - 1)] : throw Error("Uninitialized triangle reference.");
    private static void FifoPush(Span<uint> fifo, ref int offset, ref int count, uint value)
    { fifo[offset] = value; offset = (offset + 1) & (fifo.Length - 1); count = Math.Min(count + 1, fifo.Length); }

    private ref struct Cursor(ReadOnlySpan<byte> source)
    {
        private readonly ReadOnlySpan<byte> data = source;
        private int at;
        public byte Byte() => Take(1)[0];
        public ReadOnlySpan<byte> Take(int length)
        {
            if (length < 0 || length > data.Length - at) throw Error("Truncated compressed stream.");
            var value = data.Slice(at, length); at += length; return value;
        }
        public uint Varint()
        {
            uint value = 0;
            for (var i = 0; i < 5; i++)
            {
                var next = Byte(); value |= (uint)(next & 127) << (7 * i);
                if (next < 128) return value;
            }
            throw Error("Invalid variable-length index.");
        }
        public readonly void Complete() { if (at != data.Length) throw Error("Unused compressed stream data."); }
    }
    private static InvalidDataException Error(string reason) => new($"Invalid meshopt data: {reason}");
}
