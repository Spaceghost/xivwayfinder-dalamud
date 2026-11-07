using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace XivWayfinder.Core.Tests;

public sealed class GuideModelArchiveTests
{
    private const string Obj = "v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n";

    [Fact]
    public async Task EverySupportedEntryIsListedAndUncommittedPackIsRemoved()
    {
        using var files = new PackFiles();
        files.Create(("nested/b.obj", Obj), ("nested/a.obj", Obj), ("readme.txt", "model pack"));
        var pack = await GuideModelArchive.OpenAsync(files.Zip, files.Cache);
        var directory = pack.DirectoryPath;
        Assert.Equal(new[] { "nested/a.obj", "nested/b.obj" }, pack.Models.Select(m => m.RelativePath));
        foreach (var entry in pack.Models) Assert.Single(GuideMesh.Load(entry.FullPath).Indices.Chunk(3));
        pack.Dispose();
        Assert.False(Directory.Exists(directory));
        Assert.True(File.Exists(files.Zip));
    }

    [Fact]
    public async Task RetainedPackRemainsUsableAfterClosingAndOpeningAnotherPack()
    {
        using var files = new PackFiles(); files.Create(("guide.obj", Obj));
        var retained = await GuideModelArchive.OpenAsync(files.Zip, files.Cache);
        var model = retained.Models.Single().FullPath;
        retained.Retain(); retained.Dispose();
        using (var discarded = await GuideModelArchive.OpenAsync(files.Zip, files.Cache))
            Assert.NotEqual(retained.DirectoryPath, discarded.DirectoryPath);
        Assert.Equal(3, GuideMesh.Load(model).Vertices.Length);
        Assert.Single(Directory.EnumerateDirectories(files.Cache));
    }

    [Fact]
    public async Task PastedRelativeAliasesResolveToTheActualPackEntryBeforeRetirement()
    {
        using var files = new PackFiles(); files.Create(("nested/guide.obj", Obj));
        using var pack = await GuideModelArchive.OpenAsync(files.Zip, files.Cache);
        var actual = pack.Models.Single();
        var alias = Path.Combine(pack.DirectoryPath, "nested", "..", "nested", ".", "guide.obj");
        Assert.True(pack.TryFindModel(alias, out var found));
        Assert.Same(actual, found);
        Assert.Equal(actual.FullPath, found.FullPath);
        Assert.True(File.Exists(found.FullPath));
        Assert.False(pack.TryFindModel(Path.Combine(pack.DirectoryPath, "another.obj"), out _));
        Assert.False(pack.TryFindModel("bad\0path", out _));
    }

    [Fact]
    public async Task PackOwnershipUsesThePlatformsPathCaseAndSeparatorRules()
    {
        using var files = new PackFiles(); files.Create(("nested/guide.obj", Obj));
        using var pack = await GuideModelArchive.OpenAsync(files.Zip, files.Cache);
        var actual = pack.Models.Single();
        Assert.Equal(OperatingSystem.IsWindows(), pack.TryFindModel(actual.FullPath.ToUpperInvariant(), out var changedCase));
        if (OperatingSystem.IsWindows())
        {
            Assert.Same(actual, changedCase);
            Assert.True(pack.TryFindModel(actual.FullPath.Replace('\\', '/'), out var changedSeparators));
            Assert.Same(actual, changedSeparators);
        }
        else Assert.True(pack.TryFindModel(actual.FullPath, out _));
    }

    [Fact]
    public async Task OriginalAmbientCgPackLoadsObjMaterialAndTextureWithoutChangingItsFiles()
    {
        using var files = new PackFiles();
        var fixture = Path.Combine(CommandAndIpcTests.RepoRoot(), "tests", "XivWayfinder.Core.Tests", "Fixtures", "3DApple002_LQ-1K-JPG.zip");
        Assert.Equal("AA9335B8CF667F2FE5093D2B1EBFD56697974A7AA68F170058230DA976653595",
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture))));
        using var pack = await GuideModelArchive.OpenAsync(fixture, files.Cache);
        var model = Assert.Single(pack.Models);
        var mesh = GuideMesh.Load(model.FullPath);
        Assert.Equal(998, mesh.Indices.Length / 3);
        Assert.NotEmpty(mesh.Images);
        Assert.Contains(mesh.Materials, material => material.BaseColorImage >= 0);
        Assert.Equal(9, Directory.GetFiles(pack.DirectoryPath, "*", SearchOption.AllDirectories).Length);
        using var original = ZipFile.OpenRead(fixture);
        foreach (var entry in original.Entries)
        {
            using var source = entry.Open(); using var memory = new MemoryStream(); source.CopyTo(memory);
            Assert.Equal(memory.ToArray(), File.ReadAllBytes(Path.Combine(pack.DirectoryPath, entry.FullName)));
        }
    }

    [Fact]
    public async Task NestedGltfPackKeepsRelativeBufferSidecars()
    {
        using var files = new PackFiles();
        var positions = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 };
        var data = new byte[positions.Length * 4]; Buffer.BlockCopy(positions, 0, data, 0, data.Length);
        using (var zip = ZipFile.Open(files.Zip, ZipArchiveMode.Create))
        {
            PackFiles.Add(zip, "collection/models/guide.gltf", Encoding.UTF8.GetBytes("""
                {"asset":{"version":"2.0"},"buffers":[{"uri":"data/mesh.bin","byteLength":36}],
                "bufferViews":[{"buffer":0,"byteLength":36}],"accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3"}],
                "meshes":[{"primitives":[{"attributes":{"POSITION":0}}]}],"nodes":[{"mesh":0}],"scenes":[{"nodes":[0]}],"scene":0}
                """));
            PackFiles.Add(zip, "collection/models/data/mesh.bin", data);
        }
        using var pack = await GuideModelArchive.OpenAsync(files.Zip, files.Cache);
        Assert.Equal(3, GuideMesh.Load(pack.Models.Single().FullPath).Vertices.Length);
    }

    [Theory]
    [InlineData("../escape.obj")]
    [InlineData("nested/../../escape.obj")]
    [InlineData("/absolute.obj")]
    [InlineData("C:/drive.obj")]
    [InlineData("C:drive.obj")]
    [InlineData("\\\\server\\share\\guide.obj")]
    [InlineData("nested\\..\\escape.obj")]
    [InlineData("name.obj:stream")]
    [InlineData("NUL.obj")]
    [InlineData("directory./guide.obj")]
    [InlineData("CONIN$.obj")]
    [InlineData("conout$.obj")]
    [InlineData("COM¹.obj")]
    [InlineData("lpt².obj")]
    [InlineData("nested//guide.obj")]
    public async Task UnsafeOrAmbiguousEntryPathsAreRejectedBeforeWriting(string path)
    {
        using var files = new PackFiles(); files.Create((path, Obj));
        await Assert.ThrowsAsync<InvalidDataException>(() => GuideModelArchive.OpenAsync(files.Zip, files.Cache));
        files.AssertNoExtractedFolders();
    }

    [Theory]
    [InlineData("guide.obj", "GUIDE.OBJ")]
    [InlineData("folder/guide.obj", "folder\\guide.obj")]
    [InlineData("folder", "folder/guide.obj")]
    public async Task DuplicatesAndFileFolderAliasesAreRejected(string first, string second)
    {
        using var files = new PackFiles(); files.Create((first, Obj), (second, Obj));
        await Assert.ThrowsAsync<InvalidDataException>(() => GuideModelArchive.OpenAsync(files.Zip, files.Cache));
        files.AssertNoExtractedFolders();
    }

    [Theory]
    [InlineData(0xa1ff0000u)] // Unix symbolic link
    [InlineData(0x21ff0000u)] // Unix character device
    [InlineData(0x00000400u)] // Windows reparse point
    public async Task LinkAndSpecialEntriesAreRejected(uint attributes)
    {
        using var files = new PackFiles();
        using (var zip = ZipFile.Open(files.Zip, ZipArchiveMode.Create))
        {
            var entry = PackFiles.Add(zip, "guide.obj", Encoding.UTF8.GetBytes(Obj));
            entry.ExternalAttributes = unchecked((int)attributes);
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => GuideModelArchive.OpenAsync(files.Zip, files.Cache));
        files.AssertNoExtractedFolders();
    }

    [Fact]
    public async Task CompressedExpansionCannotBypassThePackBudget()
    {
        using var files = new PackFiles();
        using (var zip = ZipFile.Open(files.Zip, ZipArchiveMode.Create))
        {
            PackFiles.Add(zip, "guide.obj", Encoding.UTF8.GetBytes(Obj));
            using var data = zip.CreateEntry("large.bin", CompressionLevel.SmallestSize).Open();
            var block = new byte[1024 * 1024];
            for (var i = 0; i < 33; i++) data.Write(block);
        }
        Assert.True(new FileInfo(files.Zip).Length < 100_000);
        await Assert.ThrowsAsync<InvalidDataException>(() => GuideModelArchive.OpenAsync(files.Zip, files.Cache));
        files.AssertNoExtractedFolders();
    }

    [Fact]
    public async Task ExcessiveEntriesAndUnsupportedOnlyPacksDoNotCreatePreviewFolders()
    {
        using var files = new PackFiles();
        files.Create(Enumerable.Range(0, GuideModelArchive.MaximumEntries + 1).Select(i => ($"{i}.obj", Obj)).ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => GuideModelArchive.OpenAsync(files.Zip, files.Cache));
        files.AssertNoExtractedFolders();
        File.Delete(files.Zip); files.Create(("guide.fbx", "unsupported model"));
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => GuideModelArchive.OpenAsync(files.Zip, files.Cache));
        Assert.Contains("no GLB, glTF or OBJ", exception.Message);
        files.AssertNoExtractedFolders();
    }

    [Fact]
    public async Task CancelBeforeOrDuringOpeningLeavesNoUncommittedPack()
    {
        using var files = new PackFiles();
        using (var zip = ZipFile.Open(files.Zip, ZipArchiveMode.Create))
        {
            PackFiles.Add(zip, "guide.obj", Encoding.UTF8.GetBytes(Obj));
            using var data = zip.CreateEntry("data.bin", CompressionLevel.SmallestSize).Open();
            var block = new byte[1024 * 1024];
            for (var i = 0; i < 30; i++) data.Write(block);
        }
        using var before = new CancellationTokenSource(); before.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GuideModelArchive.OpenAsync(files.Zip, files.Cache, before.Token));
        files.AssertNoExtractedFolders();
        using var during = new CancellationTokenSource();
        var opening = GuideModelArchive.OpenAsync(files.Zip, files.Cache, during.Token);
        await Task.Delay(1); during.Cancel();
        // Completion can win cancellation. The UI retires a completed result as
        // well, so either ordering must leave the original selection/files alone.
        try { using var completed = await opening; }
        catch (OperationCanceledException) { }
        files.AssertNoExtractedFolders();
        Assert.True(File.Exists(files.Zip));
    }

    private sealed class PackFiles : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "wayfinder-pack-tests-" + Guid.NewGuid().ToString("N"));
        public string Zip => Path.Combine(Root, "download.zip");
        public string Cache => Path.Combine(Root, "packs");
        public PackFiles() => Directory.CreateDirectory(Root);
        public void Create(params (string Name, string Text)[] entries)
        {
            using var zip = ZipFile.Open(Zip, ZipArchiveMode.Create);
            foreach (var entry in entries) Add(zip, entry.Name, Encoding.UTF8.GetBytes(entry.Text));
        }
        public static ZipArchiveEntry Add(ZipArchive zip, string name, byte[] bytes)
        {
            var entry = zip.CreateEntry(name); using var file = entry.Open(); file.Write(bytes); return entry;
        }
        public void AssertNoExtractedFolders() => Assert.True(!Directory.Exists(Cache) || !Directory.EnumerateFileSystemEntries(Cache).Any());
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
