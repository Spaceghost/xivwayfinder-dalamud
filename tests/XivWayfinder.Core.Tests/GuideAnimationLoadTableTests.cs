using System.Buffers.Binary;
using System.Text;

namespace XivWayfinder.Core.Tests;

public sealed class GuideAnimationLoadTableTests
{
    [Fact]
    public void HighlanderKeepsAuthoredWalkAndInheritsIdleReturningToRest()
    {
        var table = GuideAnimationLoadTable.Read(Table(301, 1,
            [(101, 1, 0), (301, 1, 0), (101, 1, 0)]));
        Assert.NotNull(table);
        var files = new Dictionary<string, byte[]>
        {
            [Path(101, 1, "idle")] = Pap("cbnm_id0"),
            [Path(301, 1, "move_a")] = Pap("cbnm_01f_lp0"),
            [Path(101, 1, "move_b")] = Pap("cbnm_02f_lp0"),
        };
        var clips = GuideAnimationPack.ReadResident("chara/human/c0301", 1, 1, files.GetValueOrDefault, table);
        Assert.Equal(new GuideAnimationSet(3, 13, 22), clips);
        Assert.Equal(new ushort[] { 3, 13, 3 }, new[] { 0f, 1.5f, 0f }.Select(speed => clips.Choose(speed, true).Timeline));
        Assert.False(clips.Choose(0, true).Moving);
    }

    [Fact]
    public void FemaleLalafellUsesOnlyExplicitSharedPackSources()
    {
        var table = GuideAnimationLoadTable.Read(Table(1201, 1,
            [(1101, 1, 0), (1101, 1, 0), (1101, 1, 0)]));
        var files = new Dictionary<string, byte[]>
        {
            [Path(1101, 1, "idle")] = Pap("cbnm_id0"),
            [Path(1101, 1, "move_a")] = Pap("cbnm_01f_lp0"),
            [Path(1101, 1, "move_b")] = Pap("cbnm_02f_lp0"),
        };
        Assert.Equal(new GuideAnimationSet(3, 13, 22), GuideAnimationPack.ReadResident(
            "chara/human/c1201", 1, 1, files.GetValueOrDefault, table));
        Assert.Equal(Path(201, 1, "idle"), table!.ResolvePath(Path(201, 1, "idle")));
    }

    [Fact]
    public void ExactVariantAndSourceDirectoryAreRetainedAcrossSparseMaskWords()
    {
        var table = GuideAnimationLoadTable.Read(Table(1201, 2,
            [(1101, 7, 10), (1101, 9, 0), (101, 3, 10)]));
        Assert.NotNull(table);
        Assert.Equal(Path(1101, 7, "idle", "bt_swd_emp"), table.ResolvePath(Path(1201, 2, "idle")));
        Assert.Equal(Path(1101, 9, "move_a"), table.ResolvePath(Path(1201, 2, "move_a")));
        Assert.Equal(Path(101, 3, "move_b", "bt_swd_emp"), table.ResolvePath(Path(1201, 2, "move_b")));
        Assert.Equal(Path(1201, 1, "idle"), table.ResolvePath(Path(1201, 1, "idle")));
    }

    [Fact]
    public void TableNeverClaimsAClipThatIsAbsentFromTheResolvedPap()
    {
        var table = GuideAnimationLoadTable.Read(Table(1201, 1,
            [(1101, 1, 0), (1101, 1, 0), (1101, 1, 0)]));
        var files = new Dictionary<string, byte[]>
        {
            [Path(1201, 1, "idle")] = Pap("cbnm_id0"), // Not the game's selected source.
            [Path(1101, 1, "move_a")] = Pap("some_unrelated_clip"),
        };
        Assert.Equal(default, GuideAnimationPack.ReadResident("chara/human/c1201", 1, 1, files.GetValueOrDefault, table));
    }

    [Fact]
    public void MalformedMetadataFallsBackToTheSameDirectPack()
    {
        var bad = Table(1201, 1, [(1101, 1, 0), (1101, 1, 0), (1101, 1, 0)]);
        BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(8), uint.MaxValue);
        var table = GuideAnimationLoadTable.Read(bad);
        Assert.Null(table);
        Assert.Equal(new GuideAnimationSet(3, 0, 0), GuideAnimationPack.ReadResident("chara/human/c1201", 1, 1,
            path => path == Path(1201, 1, "idle") ? Pap("cbnm_id0") : null, table));
    }

    [Fact]
    public void FailedOptionalReadCachesFallbackAndKeepsDirectClipsAvailable()
    {
        var attempts = 0;
        var table = new Lazy<GuideAnimationLoadTable?>(() => GuideAnimationLoadTable.ReadOptional(() =>
        {
            attempts++;
            throw new IOException("PLT resource read failed");
        }));
        for (var i = 0; i < 2; i++)
            Assert.Equal(new GuideAnimationSet(3, 0, 0), GuideAnimationPack.ReadResident("chara/human/c1101", 1, 1,
                path => path == Path(1101, 1, "idle") ? Pap("cbnm_id0") : null, table.Value));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void OptionalMetadataBoundaryDoesNotHideResidentPackReadFailures()
    {
        var table = GuideAnimationLoadTable.ReadOptional(() => throw new IOException("PLT unavailable"));
        var failure = new IOException("Resident PAP unavailable");
        Assert.Same(failure, Assert.Throws<IOException>(() => GuideAnimationPack.ReadResident("chara/human/c1101", 1, 1,
            _ => throw failure, table)));
    }

    [Fact]
    public void TruncatedTablesAndUnboundedStringsAreRejected()
    {
        var valid = Table(1201, 1, [(1101, 1, 0), (1101, 1, 0), (1101, 1, 0)]);
        foreach (var length in new[] { 0, 15, 16, 150, valid.Length - 1 })
            Assert.Null(GuideAnimationLoadTable.Read(valid.AsSpan(0, length)));
        var bad = valid.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(24), uint.MaxValue);
        Assert.Null(GuideAnimationLoadTable.Read(bad));
        bad = valid.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(bad.AsSpan(16), ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(bad, ushort.MaxValue);
        Assert.Null(GuideAnimationLoadTable.Read(bad));
    }

    [Fact]
    public void OutOfRangeSparseSourcesCannotReadStringDataAsModelIdentity()
    {
        var bad = Table(1201, 1, [(1101, 1, 0), (1101, 1, 0), (1101, 1, 0)]);
        var modelAt = 16 + 268 * 12;
        BinaryPrimitives.WriteUInt16LittleEndian(bad.AsSpan(modelAt + 6), ushort.MaxValue);
        Assert.Null(GuideAnimationLoadTable.Read(bad));
    }

    // Synthetic PLT: three selected packs deliberately span mask words and a gap.
    // This exercises rank accumulation without storing any proprietary game data.
    private static byte[] Table(uint model, ushort variant, (uint Model, ushort Variant, ushort Directory)[] sources)
    {
        const int count = 268, modelAt = 16 + count * 12, overridesAt = modelAt + 168, directoriesAt = overridesAt + 12 * 8;
        var directories = Encoding.ASCII.GetBytes("bt_common\0bt_swd_emp\0");
        var names = Encoding.ASCII.GetBytes("resident/idle\0resident/move_a\0resident/move_b\0unused\0");
        var namesAt = directoriesAt + directories.Length;
        var b = new byte[namesAt + names.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(b, count);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), directoriesAt);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), (uint)namesAt);
        directories.CopyTo(b, directoriesAt); names.CopyTo(b, namesAt);
        for (var i = 0; i < count; i++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16 + i * 12 + 8), 46);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(24), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16 + 260 * 12 + 8), 14);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16 + 267 * 12 + 8), 30);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(modelAt), model);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(modelAt + 4), variant);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(modelAt + 8), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(modelAt + 16), 6);
        for (var group = 0; group < 3; group++)
            for (var entry = 0; entry < 4; entry++)
            {
                var at = overridesAt + (group * 4 + entry) * 8;
                BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), sources[group].Model);
                BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at + 4), sources[group].Variant);
                BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at + 6), sources[group].Directory);
            }
        return b;
    }

    private static string Path(uint model, int variant, string pack, string directory = "bt_common") =>
        $"chara/human/c{model:D4}/animation/a{variant:D4}/{directory}/resident/{pack}.pap";

    private static byte[] Pap(params string[] names)
    {
        var b = new byte[26 + 40 * names.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(b, 0x20706170);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(8), (short)names.Length);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(14), 26);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(18), b.Length);
        for (var i = 0; i < names.Length; i++) Encoding.ASCII.GetBytes(names[i], b.AsSpan(26 + i * 40));
        return b;
    }
}
