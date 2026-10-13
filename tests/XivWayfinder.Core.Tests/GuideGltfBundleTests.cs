using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;

namespace XivWayfinder.Core.Tests;

public sealed class GuideGltfBundleTests
{
    [Fact]
    public void ExternalBuffersKeepSkeletalAnimationAndTextureAppearance()
    {
        using var file = new Fixture();
        file.Attributes["JOINTS_0"] = file.Accessor(new byte[12], "VEC4", 5121, 3);
        file.Attributes["WEIGHTS_0"] = file.Floats([1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0], "VEC4");
        file.Root["nodes"] = JsonNode.Parse("""[{"mesh":0,"skin":0},{}]""");
        file.Root["skins"] = JsonNode.Parse("""[{"joints":[1]}]""");
        file.Root["scenes"] = JsonNode.Parse("""[{"nodes":[0,1]}]""");
        file.Animate(1);
        file.Texture("textures/pretty%20fur.png");
        var mesh = file.Load();
        Assert.Equal("Walk", Assert.Single(mesh.Animations).Name);
        var pose = new Vector3[mesh.Vertices.Length];
        mesh.SamplePose(0, .5f, pose);
        Assert.Equal(mesh.Vertices[0] + Vector3.UnitZ, pose[0]);
        Assert.Equal("image/png", Assert.Single(mesh.Images).MimeType);
        Assert.Equal(0, mesh.Materials[0].BaseColorImage);
        Assert.Equal(new Vector2(1, 0), mesh.TextureCoordinates[1]);
    }

    [Theory]
    [InlineData("application/octet-stream")]
    [InlineData("application/gltf-buffer")]
    public void DataUriBuffersAndTextureCanBeLoadedWithoutSidecars(string mime)
    {
        using var file = new Fixture();
        file.Texture("textures/pretty%20fur.png");
        foreach (var item in file.Root["buffers"]!.AsArray())
        {
            var bytes = File.ReadAllBytes(Path.Combine(file.DirectoryPath, item!["uri"]!.GetValue<string>()));
            item["uri"] = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
        }
        file.Root["images"]![0]!["uri"] = "data:image/png;base64," + Convert.ToBase64String(Fixture.Png);
        Assert.Single(file.Load().Images);
    }

    [Fact]
    public void GlbMayUseAnEmbeddedBufferAndExternalAnimationBuffers()
    {
        using var file = new Fixture();
        file.Animate(0);
        var mesh = file.LoadGlb(embedFirst: true);
        var pose = new Vector3[mesh.Vertices.Length];
        mesh.SamplePose(0, .5f, pose);
        Assert.Equal(mesh.Vertices[0] + Vector3.UnitZ, pose[0]);
    }

    [Fact]
    public void GlbCanUseOnlyExternalBuffersWithoutABinaryChunk()
    {
        using var file = new Fixture();
        Assert.Equal(3, file.LoadGlb(embedFirst: false).Vertices.Length);
    }

    [Fact]
    public void SparseAccessorReadsIndicesAndValuesFromDifferentExternalBuffers()
    {
        using var file = new Fixture();
        var indices = file.Accessor([1], "SCALAR", 5121, 1);
        var values = file.Floats([2, 0, 0], "VEC3");
        file.Root["accessors"]![0]!["sparse"] = new JsonObject
        {
            ["count"] = 1,
            ["indices"] = new JsonObject { ["bufferView"] = indices, ["componentType"] = 5121 },
            ["values"] = new JsonObject { ["bufferView"] = values }
        };
        Assert.Equal(new Vector3(.5f, -.25f, 0), file.Load().Vertices[1]);
    }

    [Fact]
    public void RepeatedEmbeddedImageViewsCannotMultiplyTextureMemoryWithoutLimit()
    {
        using var file = new Fixture();
        var bytes = new byte[16 * 1024 * 1024]; Fixture.Png.CopyTo(bytes, 0);
        var view = file.Accessor(bytes, "SCALAR", 5121, bytes.Length);
        file.Root["images"] = new JsonArray();
        for (var i = 0; i < 3; i++) file.Root["images"]!.AsArray().Add(new JsonObject { ["bufferView"] = view, ["mimeType"] = "image/png" });
        file.Root["textures"] = JsonNode.Parse("""[{"source":0},{"source":1},{"source":2}]""");
        file.Root["materials"] = JsonNode.Parse("""[{"pbrMetallicRoughness":{"baseColorTexture":{"index":0}}},{"pbrMetallicRoughness":{"baseColorTexture":{"index":1}}},{"pbrMetallicRoughness":{"baseColorTexture":{"index":2}}}]""");
        var error = Assert.Throws<InvalidDataException>(() => file.Load());
        Assert.Contains("texture data", error.Message);
    }

    [Theory]
    [InlineData("../outside.bin")]
    [InlineData("%2e%2e/outside.bin")]
    [InlineData("..%2foutside.bin")]
    [InlineData("..%5coutside.bin")]
    [InlineData("https://example.com/model.bin")]
    [InlineData("file:///tmp/model.bin")]
    [InlineData("/tmp/model.bin")]
    [InlineData("C:%5ctemp%5cmodel.bin")]
    [InlineData("buffer.bin?query")]
    [InlineData("buffer.bin#fragment")]
    public void ResourceReferencesCannotLeaveModelFolderOrUseTheNetwork(string uri)
    {
        using var file = new Fixture();
        file.Root["buffers"]![0]!["uri"] = uri;
        Assert.Throws<InvalidDataException>(() => file.Load());
    }

    [Fact]
    public void ExternalResourceSymlinkCannotLeaveFolder()
    {
        if (OperatingSystem.IsWindows()) return; // Creating links may require developer mode on Windows hosts.
        using var file = new Fixture();
        var outside = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".bin");
        try
        {
            File.WriteAllBytes(outside, new byte[36]);
            File.CreateSymbolicLink(Path.Combine(file.DirectoryPath, "linked.bin"), outside);
            file.Root["buffers"]![0]!["uri"] = "linked.bin";
            Assert.Throws<InvalidDataException>(() => file.Load());
        }
        finally { File.Delete(outside); }
    }

    [Fact]
    public void LocalSymlinkAndEncodedNonAsciiResourceNamesAreAllowed()
    {
        if (OperatingSystem.IsWindows()) return;
        using var file = new Fixture();
        var name = file.Root["buffers"]![0]!["uri"]!.GetValue<string>();
        File.CreateSymbolicLink(Path.Combine(file.DirectoryPath, "sphère.bin"), name);
        file.Root["buffers"]![0]!["uri"] = "sph%C3%A8re.bin";
        Assert.Equal(3, file.Load().Vertices.Length);
    }

    [Fact]
    public void AllFilesShareTheTotalSizeLimitBeforeReadingLargeResource()
    {
        using var file = new Fixture();
        var name = file.Root["buffers"]![0]!["uri"]!.GetValue<string>();
        using (var stream = File.OpenWrite(Path.Combine(file.DirectoryPath, name))) stream.SetLength(32 * 1024 * 1024);
        Assert.Throws<InvalidDataException>(() => file.Load());
    }

    [Theory]
    [InlineData("missing-uri")]
    [InlineData("short-buffer")]
    [InlineData("invalid-buffer-index")]
    [InlineData("view-outside-declared-length")]
    [InlineData("image-mime-mismatch")]
    public void InvalidBundleResourcesFailBeforeSampling(string kind)
    {
        using var file = new Fixture();
        switch (kind)
        {
            case "missing-uri": file.Root["buffers"]![0]!.AsObject().Remove("uri"); break;
            case "short-buffer": file.Root["buffers"]![0]!["byteLength"] = 40; break;
            case "invalid-buffer-index": file.Root["bufferViews"]![0]!["buffer"] = 1; break;
            case "view-outside-declared-length": file.Root["buffers"]![0]!["byteLength"] = 32; break;
            case "image-mime-mismatch": file.Texture("fur.png"); file.Root["images"]![0]!["mimeType"] = "image/jpeg"; break;
        }
        Assert.Throws<InvalidDataException>(() => file.Load());
    }

    [Theory]
    [InlineData(5, false)]
    [InlineData(5, true)]
    [InlineData(6, false)]
    [InlineData(6, true)]
    public void TriangleStripsAndFansKeepCounterClockwiseWinding(int mode, bool indexed)
    {
        using var file = new Fixture();
        file.Attributes["POSITION"] = file.Floats(mode == 5 ? [0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 0] : [0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0], "VEC3");
        file.Primitive["mode"] = mode;
        if (indexed) file.Primitive["indices"] = file.Accessor([0, 1, 2, 3], "SCALAR", 5121, 4);
        var mesh = file.Load();
        Assert.Equal(6, mesh.Indices.Length);
        Assert.Equal(2, mesh.FaceColors.Length);
        for (var i = 0; i < mesh.Indices.Length; i += 3)
        {
            var a = mesh.Vertices[mesh.Indices[i]]; var b = mesh.Vertices[mesh.Indices[i + 1]]; var c = mesh.Vertices[mesh.Indices[i + 2]];
            Assert.True(Vector3.Cross(b - a, c - a).Z > 0);
        }
    }

    [Fact]
    public void DegenerateStripConnectorsDoNotCreateZeroAreaTriangles()
    {
        using var file = new Fixture();
        file.Primitive["mode"] = 5;
        file.Primitive["indices"] = file.Accessor([0, 1, 2, 2, 1, 1, 0, 2], "SCALAR", 5121, 8);
        Assert.Equal(6, file.Load().Indices.Length);
    }

    [Fact]
    public void StripExpansionStillEnforcesTriangleLimit()
    {
        using var file = new Fixture();
        var positions = new float[(GuideMesh.MaxTriangles + 3) * 3];
        for (var i = 0; i < positions.Length; i++) positions[i] = i;
        file.Attributes["POSITION"] = file.Floats(positions, "VEC3");
        file.Primitive["mode"] = 5;
        Assert.Throws<InvalidDataException>(() => file.Load());
    }

    private sealed class Fixture : IDisposable
    {
        public static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg==");
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "wayfinder-gltf-" + Guid.NewGuid());
        public JsonObject Root { get; } = JsonNode.Parse("""{"asset":{"version":"2.0"},"buffers":[],"bufferViews":[],"accessors":[],"meshes":[{"primitives":[{"attributes":{}}]}],"nodes":[{"mesh":0}],"scenes":[{"nodes":[0]}]}""")!.AsObject();
        public JsonObject Primitive => Root["meshes"]![0]!["primitives"]![0]!.AsObject();
        public JsonObject Attributes => Primitive["attributes"]!.AsObject();
        public Fixture() { Directory.CreateDirectory(DirectoryPath); Attributes["POSITION"] = Floats([0, 0, 0, 1, 0, 0, 0, 1, 0], "VEC3"); }
        public int Accessor(byte[] bytes, string type, int component, int count)
        {
            var buffers = Root["buffers"]!.AsArray(); var id = buffers.Count;
            var filename = $"buffer{id}.bin";
            File.WriteAllBytes(Path.Combine(DirectoryPath, filename), bytes);
            buffers.Add(new JsonObject { ["uri"] = filename, ["byteLength"] = bytes.Length });
            Root["bufferViews"]!.AsArray().Add(new JsonObject { ["buffer"] = id, ["byteLength"] = bytes.Length });
            Root["accessors"]!.AsArray().Add(new JsonObject { ["bufferView"] = id, ["componentType"] = component, ["count"] = count, ["type"] = type });
            return id;
        }
        public int Floats(float[] values, string type) => Accessor(values.SelectMany(BitConverter.GetBytes).ToArray(), type, 5126, values.Length / (type == "VEC4" ? 4 : type == "VEC3" ? 3 : type == "VEC2" ? 2 : 1));
        public void Animate(int node)
        {
            var input = Floats([0, 1], "SCALAR"); var output = Floats([0, 0, 0, 0, 0, 2], "VEC3");
            Root["animations"] = new JsonArray(new JsonObject
            {
                ["name"] = "Walk",
                ["samplers"] = new JsonArray(new JsonObject { ["input"] = input, ["output"] = output }),
                ["channels"] = new JsonArray(new JsonObject { ["sampler"] = 0, ["target"] = new JsonObject { ["node"] = node, ["path"] = "translation" } })
            });
        }
        public void Texture(string uri)
        {
            var path = Path.Combine(DirectoryPath, Uri.UnescapeDataString(uri));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, Png);
            Attributes["TEXCOORD_0"] = Floats([0, 0, 1, 0, 0, 1], "VEC2");
            Root["images"] = new JsonArray(new JsonObject { ["uri"] = uri });
            Root["textures"] = JsonNode.Parse("""[{"source":0}]""");
            Root["materials"] = JsonNode.Parse("""[{"pbrMetallicRoughness":{"baseColorTexture":{"index":0}}}]""");
            Primitive["material"] = 0;
        }
        public GuideMesh Load()
        {
            var path = Path.Combine(DirectoryPath, "model.GLTF"); File.WriteAllText(path, Root.ToJsonString()); return GuideMesh.Load(path);
        }
        public GuideMesh LoadGlb(bool embedFirst)
        {
            byte[]? bin = null;
            if (embedFirst)
            {
                var first = Root["buffers"]![0]!.AsObject(); bin = File.ReadAllBytes(Path.Combine(DirectoryPath, first["uri"]!.GetValue<string>())); first.Remove("uri");
            }
            var json = Encoding.UTF8.GetBytes(Root.ToJsonString()); var length = (json.Length + 3) / 4 * 4;
            var path = Path.Combine(DirectoryPath, "model.glb");
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                writer.Write(0x46546c67u); writer.Write(2u); writer.Write((uint)(20 + length + (bin is null ? 0 : 8 + bin.Length)));
                writer.Write(length); writer.Write(0x4e4f534au); writer.Write(json);
                for (var i = json.Length; i < length; i++) writer.Write((byte)32);
                if (bin is not null) { writer.Write(bin.Length); writer.Write(0x004e4942u); writer.Write(bin); }
            }
            return GuideMesh.Load(path);
        }
        public void Dispose() => Directory.Delete(DirectoryPath, true);
    }
}
