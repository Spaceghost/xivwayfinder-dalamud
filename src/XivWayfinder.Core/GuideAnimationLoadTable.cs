using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace XivWayfinder.Core;

/// <summary>The game's authored sources for the resident body animation packs used by guides.</summary>
public sealed class GuideAnimationLoadTable
{
    public const string ResourcePath = "chara/xls/animation/papLoadTable.plt";
    private const int HeaderSize = 16, PackSize = 12, ModelSize = 168, MaskWords = 20;
    private readonly Dictionary<string, string> paths;

    private GuideAnimationLoadTable(Dictionary<string, string> paths) => this.paths = paths;

    public string ResolvePath(string path) => paths.GetValueOrDefault(path, path);

    /// <summary>An unavailable optional table must not prevent reading a model's direct PAPs.</summary>
    public static GuideAnimationLoadTable? ReadOptional(Func<byte[]?> read)
    {
        byte[]? bytes;
        try { bytes = read(); }
        catch (Exception ex) when (ex is not OutOfMemoryException and not AccessViolationException)
        {
            // Only the optional resource read is contained here. Resident PAP
            // failures still reach the caller's normal diagnostics. A Lazy can
            // cache this null fallback instead of permanently caching a fault.
            return null;
        }
        return bytes is null ? null : Read(bytes);
    }

    /// <summary>
    /// Reads PLT's pack descriptors, exact model/variant records, and sparse four-pack source overrides.
    /// Layout and bit ranking follow PapLoadTableResourceHandle in the installed 2026.09.15 client;
    /// LoadAnimation uses this table before loading resident PAPs. Clip availability still requires PAP proof.
    /// </summary>
    public static GuideAnimationLoadTable? Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize) return null;
        var packs = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        var models = BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]);
        var directoriesAt = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        var namesAt = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        var modelsAt = HeaderSize + packs * PackSize;
        var overridesAt = modelsAt + models * ModelSize;
        if (packs is 0 or > MaskWords * 64 * 4 || models == 0
            || overridesAt > directoriesAt || directoriesAt >= namesAt || namesAt >= bytes.Length)
            return null;

        var wanted = new List<(int Index, ushort DirectoryOffset, string Name)>();
        for (var i = 0; i < packs; i++)
        {
            var pack = bytes.Slice(HeaderSize + i * PackSize, PackSize);
            var directoryOffset = BinaryPrimitives.ReadUInt16LittleEndian(pack[4..]);
            var nameOffset = BinaryPrimitives.ReadUInt32LittleEndian(pack[8..]);
            var directory = ReadPath(bytes, directoriesAt, namesAt, directoryOffset);
            var name = ReadPath(bytes, namesAt, (uint)bytes.Length, nameOffset);
            if (directory is null || name is null) return null;
            if (directory == "bt_common" && name is "resident/idle" or "resident/move_a"
                or "resident/move_b" or "resident/demihuman" or "resident/monster" or "resident/mount")
                wanted.Add((i, directoryOffset, name));
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var identities = new HashSet<(uint, ushort)>();
        for (var i = 0; i < models; i++)
        {
            var model = bytes.Slice(modelsAt + i * ModelSize, ModelSize);
            var key = BinaryPrimitives.ReadUInt32LittleEndian(model);
            var variant = BinaryPrimitives.ReadUInt16LittleEndian(model[4..]);
            if (!identities.Add((key, variant))) return null;
            var root = Root(key);
            if (root is null) continue;
            foreach (var pack in wanted)
            {
                var sourceKey = key;
                var sourceVariant = variant;
                var sourceDirectory = pack.DirectoryOffset;
                var group = pack.Index / 4;
                var word = group / 64;
                var bit = group % 64;
                var mask = BinaryPrimitives.ReadUInt64LittleEndian(model[(8 + word * 8)..]);
                if ((mask & (1UL << bit)) != 0)
                {
                    var rank = BitOperations.PopCount(mask & ((1UL << bit) - 1));
                    for (var w = 0; w < word; w++)
                        rank += BitOperations.PopCount(BinaryPrimitives.ReadUInt64LittleEndian(model[(8 + w * 8)..]));
                    var firstGroup = BinaryPrimitives.ReadUInt16LittleEndian(model[6..]);
                    var sourceAt = (long)overridesAt + ((firstGroup + rank) * 4L + pack.Index % 4) * 8;
                    if (sourceAt < overridesAt || sourceAt + 8 > directoriesAt) return null;
                    var source = bytes.Slice((int)sourceAt, 8);
                    sourceKey = BinaryPrimitives.ReadUInt32LittleEndian(source);
                    sourceVariant = BinaryPrimitives.ReadUInt16LittleEndian(source[4..]);
                    sourceDirectory = BinaryPrimitives.ReadUInt16LittleEndian(source[6..]);
                }
                var sourceRoot = Root(sourceKey);
                var directory = ReadPath(bytes, directoriesAt, namesAt, sourceDirectory);
                if (sourceRoot is null || directory is null) return null;
                var direct = $"{root}/animation/a{variant:D4}/bt_common/{pack.Name}.pap";
                var inherited = $"{sourceRoot}/animation/a{sourceVariant:D4}/{directory}/{pack.Name}.pap";
                if (!result.TryAdd(direct, inherited)) return null;
            }
        }
        return new(result);
    }

    private static string? Root(uint key) => (key >> 16) switch
    {
        0 => $"chara/human/c{key & 0xffff:D4}",
        1 => $"chara/monster/m{key & 0xffff:D4}",
        2 => $"chara/demihuman/d{key & 0xffff:D4}",
        _ => null,
    };

    private static string? ReadPath(ReadOnlySpan<byte> bytes, uint start, uint end, uint offset)
    {
        if ((ulong)start + offset >= end) return null;
        var text = bytes.Slice((int)(start + offset), (int)(end - start - offset));
        var length = text.IndexOf((byte)0);
        if (length is <= 0 or > 256) return null;
        text = text[..length];
        // PLT contains game resource paths, never host file paths or relative traversal.
        foreach (var ch in text)
            if (!(ch is >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9'
                or (byte)'_' or (byte)'/' or (byte)'*')) return null;
        if (text[0] == '/' || text[^1] == '/') return null;
        return Encoding.ASCII.GetString(text);
    }
}
