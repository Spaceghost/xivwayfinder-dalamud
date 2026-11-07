using System.Buffers.Binary;
using System.Text;

namespace XivWayfinder.Core;

/// <summary>Locomotion clips actually present in a model's resident animation pack.</summary>
public readonly record struct GuideAnimationSet(ushort Idle, ushort Walk, ushort Run)
{
    public static GuideAnimationSet FromNames(IEnumerable<string> names)
    {
        var available = names.ToHashSet(StringComparer.Ordinal);
        ushort Pick(string first, ushort firstId, string second, ushort secondId) =>
            available.Contains(first) ? firstId : available.Contains(second) ? secondId : (ushort)0;
        return new(Pick("cbnm_id0", 3, "cbbm_id0", 34),
            Pick("cbnm_01f_lp0", 13, "cbbm_01f_lp0", 41),
            Pick("cbnm_02f_lp0", 22, "cbbm_02f_lp0", 50));
    }

    public GuideAnimationPose Choose(float speed, bool animate)
    {
        if (!animate || !float.IsFinite(speed) || speed < .12f)
            return new(Idle, 1, false);
        // Many minions only have a run clip. Their own gait is slowed for a stroll;
        // never request a human walk clip absent from the model's animation pack.
        var timeline = speed < 3 ? (Walk != 0 ? Walk : Run) : (Run != 0 ? Run : Walk);
        if (timeline == 0) return new(Idle, 1, false);
        var referenceSpeed = timeline == Walk ? 1.8f : 4.5f;
        return new(timeline, Math.Clamp(speed / referenceSpeed, .4f, 1.8f), true);
    }
}

public readonly record struct GuideAnimationPose(ushort Timeline, float Speed, bool Moving);

public static class GuideAnimationPack
{
    /// <summary>Combines a model's authored resident packs, including the separate demihuman movement packs.</summary>
    public static GuideAnimationSet ReadResident(string root, int variant, int modelType, Func<string, byte[]?> read,
        GuideAnimationLoadTable? loadTable = null)
    {
        string[] packs = modelType switch
        {
            1 => ["idle", "move_a", "move_b"],
            2 => ["demihuman", "idle", "move_a", "move_b", "mount"],
            3 => ["monster", "mount", "idle", "move_a", "move_b"],
            _ => [],
        };
        var names = new HashSet<string>(StringComparer.Ordinal);
        var directory = $"{root.TrimEnd('/')}/animation/a{Math.Clamp(variant, 1, ushort.MaxValue):D4}/bt_common/resident/";
        foreach (var pack in packs)
        {
            var path = directory + pack + ".pap";
            if (read(loadTable?.ResolvePath(path) ?? path) is { } bytes) names.UnionWith(Names(bytes));
        }
        return GuideAnimationSet.FromNames(names);
    }

    /// <summary>Reads only the bounded PAP name table; no Havok code or model data is executed.</summary>
    public static string[] Names(ReadOnlySpan<byte> bytes)
    {
        const int headerSize = 26, entrySize = 40;
        if (bytes.Length < headerSize || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0x20706170) return [];
        var count = BinaryPrimitives.ReadInt16LittleEndian(bytes[8..]);
        var offset = BinaryPrimitives.ReadInt32LittleEndian(bytes[14..]);
        var dataOffset = BinaryPrimitives.ReadInt32LittleEndian(bytes[18..]);
        if (count < 0 || count > 4096 || offset < headerSize || dataOffset < offset || dataOffset > bytes.Length || (long)offset + count * entrySize > dataOffset) return [];
        var names = new string[count];
        for (var i = 0; i < count; i++)
        {
            var name = bytes.Slice(offset + i * entrySize, 32);
            var end = name.IndexOf((byte)0);
            if (end < 0) return [];
            names[i] = Encoding.ASCII.GetString(name[..end]);
        }
        return names;
    }
}
