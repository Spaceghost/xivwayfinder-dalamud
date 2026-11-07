using System.Text.Json;
using XivWayfinder.Core;

namespace XivWayfinder.Core.Tests;

public sealed class AutoTeleportJournalTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("wayfinder-teleport-journal-").FullName;
    private string PathFor(ulong owner = 12345) => Path.Combine(directory, AutoTeleportJournal.CharacterFileName(owner));

    [Fact]
    public void CompleteAtomicRoundTripLeavesNoTemporaryFileAndContainsNoPlayerMetadata()
    {
        var journal = new AutoTeleportJournal(PathFor()); Assert.Null(journal.Read());
        var state = new AutoTeleportState { OriginTerritory = 100, Attempts = [new(42, 0)], LastAttemptUtcSeconds = 1_800_000_000 };
        journal.Write(state);
        var restored = new AutoTeleportJournal(PathFor()).Read()!;
        Assert.Equal(state.Attempts, restored.Attempts); Assert.Equal(state.LastAttemptUtcSeconds, restored.LastAttemptUtcSeconds);
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        using var json = JsonDocument.Parse(File.ReadAllText(PathFor()));
        Assert.Equal(6, json.RootElement.EnumerateObject().Count());
        Assert.DoesNotContain("12345", File.ReadAllText(PathFor()));
    }

    [Fact]
    public void SeparateCharactersKeepIndependentAttemptsAndSwitchingBackRestoresTheOriginal()
    {
        var a = new AutoTeleportJournal(PathFor(12345)); var b = new AutoTeleportJournal(PathFor(98765));
        Assert.Null(a.Read()); Assert.Null(b.Read());
        var state = new AutoTeleportState { OriginTerritory = 100, Attempts = [new(42, 0)], LastAttemptUtcSeconds = 1_800_000_000 };
        a.Write(state); b.Write(new() { OriginTerritory = 100 });
        Assert.Single(new AutoTeleportJournal(PathFor(12345)).Read()!.Attempts);
        Assert.Empty(new AutoTeleportJournal(PathFor(98765)).Read()!.Attempts);
        Assert.NotEqual(PathFor(12345), PathFor(98765));
        Assert.DoesNotContain("12345", AutoTeleportJournal.CharacterFileName(12345));
        Assert.Throws<ArgumentOutOfRangeException>(() => AutoTeleportJournal.CharacterFileName(0));
    }

    [Fact]
    public void StaleSecondInstanceCannotOverwriteDurableReservation()
    {
        var first = new AutoTeleportJournal(PathFor()); var second = new AutoTeleportJournal(PathFor());
        Assert.Null(first.Read()); Assert.Null(second.Read());
        var state = new AutoTeleportState { OriginTerritory = 100, Attempts = [new(42, 0)], LastAttemptUtcSeconds = 1_800_000_000 };
        first.Write(state);
        Assert.Throws<IOException>(() => second.Write(new() { OriginTerritory = 100 }));
        Assert.Single(new AutoTeleportJournal(PathFor()).Read()!.Attempts);
    }

    [Fact]
    public void FailedLockKeepsPriorWholeStateAndDoesNotCreateTemporaryReplacement()
    {
        var journal = new AutoTeleportJournal(PathFor()); journal.Read(); journal.Write(new() { OriginTerritory = 100 });
        var before = File.ReadAllBytes(PathFor());
        using (var busy = new FileStream(PathFor() + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<IOException>(() => journal.Write(new() { OriginTerritory = 101 }));
        Assert.Equal(before, File.ReadAllBytes(PathFor())); Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{broken")]
    [InlineData("[]")]
    public void CorruptOrIncompleteFileFailsClosedRatherThanBeingTreatedAsMissing(string content)
    {
        File.WriteAllText(PathFor(), content);
        var state = new AutoTeleportJournal(PathFor()).Read();
        Assert.NotNull(state); Assert.Equal(0, state.Version);
    }

    [Fact]
    public void OversizedFileHasBoundedReadAndCanBeReplacedOnlyAfterItWasObserved()
    {
        File.WriteAllText(PathFor(), new string('x', AutoTeleportState.MaximumJsonBytes + 100));
        var journal = new AutoTeleportJournal(PathFor()); Assert.Equal(0, journal.Read()!.Version);
        journal.Write(new()); Assert.Equal(1, new AutoTeleportJournal(PathFor()).Read()!.Version);
        Assert.True(new FileInfo(PathFor()).Length < AutoTeleportState.MaximumJsonBytes);
    }

    [Fact]
    public void WritingBeforeReadNeverBlindlyClobbersHistory()
    {
        var journal = new AutoTeleportJournal(PathFor());
        Assert.Throws<IOException>(() => journal.Write(new()));
        Assert.False(File.Exists(PathFor()));
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
