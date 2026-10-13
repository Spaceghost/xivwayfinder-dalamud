using System.Numerics;
using System.Text.Json.Nodes;

namespace XivWayfinder.Core.Tests;

public sealed class GuideMeshoptTests
{
    private static string Fixture(string name) => Path.Combine(CommandAndIpcTests.RepoRoot(), "tests", "XivWayfinder.Core.Tests", "Fixtures", "Meshopt", name);

    [Fact]
    public void DistributedCoreAssemblyCarriesTheMeshoptimizerLicense()
    {
        using var resource = typeof(GuideMesh).Assembly.GetManifestResourceStream("XivWayfinder.Core.ThirdParty.meshoptimizer.LICENSE.md");
        Assert.NotNull(resource); using var reader = new StreamReader(resource);
        var license = reader.ReadToEnd();
        Assert.Contains("Arseny Kapoulkine", license);
        Assert.Contains("Permission is hereby granted", license);
    }

    public static IEnumerable<object[]> UpstreamVectors()
    {
        foreach (var entry in JsonNode.Parse(File.ReadAllText(Fixture("upstream-vectors.json")))!.AsArray())
            yield return [entry!["name"]!.GetValue<string>(), entry["count"]!.GetValue<int>(), entry["stride"]!.GetValue<int>(),
                entry["mode"]!.GetValue<string>(), entry["filter"]!.GetValue<string>(),
                entry["encoded"]!.GetValue<string>(), entry["expected"]!.GetValue<string>()];
    }

    [Theory]
    [MemberData(nameof(UpstreamVectors))]
    public void OfficialDecoderVectorsMatch(string name, int count, int stride, string mode, string filter, string encoded, string expected)
    {
        _ = name;
        var source = Convert.FromHexString(encoded);
        Assert.Equal(Convert.FromHexString(expected), GuideMeshoptDecoder.Decode(source, count, stride, mode, filter, true));
        if (source[0] != 0xa1 && filter != "COLOR")
            Assert.Equal(Convert.FromHexString(expected), GuideMeshoptDecoder.Decode(source, count, stride, mode, filter, false));
        Assert.Throws<InvalidDataException>(() => GuideMeshoptDecoder.Decode(source.AsSpan(0, source.Length - 1), count, stride, mode, filter, true));
        Assert.Throws<InvalidDataException>(() => GuideMeshoptDecoder.Decode([.. source, 0], count, stride, mode, filter, true));
    }

    [Fact]
    public void RequiredKhronosModelMatchesItsUncompressedGeometryColorsNormalsAndAnimation()
    {
        using var files = new Files();
        var compressed = GuideMesh.Load(Fixture("MeshoptCubeTest.gltf"));
        var root = JsonNode.Parse(File.ReadAllText(Fixture("MeshoptCubeTestFallback.gltf")))!;
        foreach (var view in root["bufferViews"]!.AsArray()) view!.AsObject().Remove("extensions");
        foreach (var buffer in root["buffers"]!.AsArray()) buffer!.AsObject().Remove("extensions");
        foreach (var name in new[] { "MeshoptCubeTest.bin", "MeshoptCubeTestFallback.bin" }) File.Copy(Fixture(name), Path.Combine(files.Directory, name));
        File.WriteAllText(files.Path, root.ToJsonString());
        var expected = GuideMesh.Load(files.Path);
        Assert.Equal(expected.Indices.Length, compressed.Indices.Length);
        // The triangle codec may cyclically rotate triangle corners; winding and
        // referenced geometry must remain identical to the authored fallback.
        for (var i = 0; i < expected.Indices.Length; i += 3)
            Assert.Contains(Enumerable.Range(0, 3), rotation => Enumerable.Range(0, 3)
                .All(corner => expected.Indices[i + corner] == compressed.Indices[i + (corner + rotation) % 3]));
        Assert.Equal(expected.Vertices.Length, compressed.Vertices.Length);
        Assert.Equal(expected.TextureCoordinates, compressed.TextureCoordinates);
        Assert.Single(compressed.Animations);
        for (var i = 0; i < expected.Vertices.Length; i++)
        {
            Assert.True(Vector3.Distance(expected.Vertices[i], compressed.Vertices[i]) < 1e-5f);
            Assert.True(Vector3.Distance(expected.Normals[i], compressed.Normals[i]) < .02f);
            Assert.True(Vector4.Distance(expected.VertexColors[i], compressed.VertexColors[i]) < .02f);
        }
        var expectedPose = new Vector3[expected.Vertices.Length]; var actualPose = new Vector3[expected.Vertices.Length];
        for (var frame = 0; frame <= 30; frame++)
        {
            var time = frame * compressed.Animations[0].DurationSeconds / 30;
            expected.SamplePose(0, time, expectedPose); compressed.SamplePose(0, time, actualPose);
            for (var i = 0; i < actualPose.Length; i++) Assert.True(Vector3.Distance(expectedPose[i], actualPose[i]) < .0001f);
        }
    }

    [Fact]
    public void OptionalExtensionSkipsItsMarkedFallbackUriAndSharedViewsDecodeOnce()
    {
        using var files = new Files();
        var root = JsonNode.Parse(File.ReadAllText(Fixture("MeshoptCubeTestFallback.gltf")))!;
        root["buffers"]![1]!["uri"] = "missing-uncompressed-fallback.bin";
        File.Copy(Fixture("MeshoptCubeTest.bin"), Path.Combine(files.Directory, "MeshoptCubeTest.bin"));
        File.WriteAllText(files.Path, root.ToJsonString());
        var mesh = GuideMesh.Load(files.Path);
        Assert.NotEmpty(mesh.Indices); Assert.Single(mesh.Animations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AggregateDecodedBudgetCountsDistinctViewsAndAllowsReusedViews(bool sharedView)
    {
        using var files = new Files(); const int count = 21845, copies = 129;
        var source = TriangleSource(count); var root = TriangleRoot(source, "KHR_meshopt_compression");
        root["buffers"]![1]!["byteLength"] = count * 12;
        root["buffers"]!.AsArray().Add(new JsonObject { ["byteLength"] = 6, ["uri"] = "data:application/octet-stream;base64,AAABAAIA" });
        var template = root["bufferViews"]![0]!;
        template["byteLength"] = count * 12;
        template["extensions"]!["KHR_meshopt_compression"]!["count"] = count;
        var viewCount = sharedView ? 1 : copies;
        for (var i = 1; i < viewCount; i++) root["bufferViews"]!.AsArray().Add(template.DeepClone());
        root["bufferViews"]!.AsArray().Add(new JsonObject { ["buffer"] = 2, ["byteLength"] = 6 });
        root["accessors"] = new JsonArray(); root["meshes"]![0]!["primitives"] = new JsonArray();
        for (var i = 0; i < copies; i++)
        {
            root["accessors"]!.AsArray().Add(new JsonObject { ["bufferView"] = sharedView ? 0 : i, ["componentType"] = 5126, ["count"] = count, ["type"] = "VEC3" });
            root["meshes"]![0]!["primitives"]!.AsArray().Add(new JsonObject { ["attributes"] = new JsonObject { ["POSITION"] = i }, ["indices"] = copies });
        }
        root["accessors"]!.AsArray().Add(new JsonObject { ["bufferView"] = viewCount, ["componentType"] = 5123, ["count"] = 3, ["type"] = "SCALAR" });
        File.WriteAllText(files.Path, root.ToJsonString());
        if (sharedView) Assert.Equal(copies * 3, GuideMesh.Load(files.Path).Vertices.Length);
        else Assert.Contains("Decoded meshopt views must total at most 32 MB", Assert.Throws<InvalidDataException>(() => GuideMesh.Load(files.Path)).Message);
    }

    [Theory]
    [InlineData("EXT_meshopt_compression")]
    [InlineData("KHR_meshopt_compression")]
    public void RequiredCompressionLoadsThroughGltfAndGlbWithAnUnmarkedPlaceholder(string extension)
    {
        using var files = new Files();
        var source = TriangleSource(); var root = TriangleRoot(source, extension);
        File.WriteAllText(files.Path, root.ToJsonString());
        var gltf = GuideMesh.Load(files.Path);
        Assert.Equal(3, gltf.Vertices.Length); Assert.Equal(new[] { 0, 1, 2 }, gltf.Indices);
        root["buffers"]![0]!.AsObject().Remove("uri");
        var json = System.Text.Encoding.UTF8.GetBytes(root.ToJsonString());
        Array.Resize(ref json, (json.Length + 3) & ~3);
        for (var i = json.Length - 1; json[i] == 0; i--) json[i] = 32;
        Array.Resize(ref source, (source.Length + 3) & ~3);
        var glbPath = Path.ChangeExtension(files.Path, ".glb");
        using (var stream = File.Create(glbPath))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(0x46546c67); writer.Write(2); writer.Write(28 + json.Length + source.Length);
            writer.Write(json.Length); writer.Write(0x4e4f534a); writer.Write(json);
            writer.Write(source.Length); writer.Write(0x004e4942); writer.Write(source);
        }
        Assert.Equal(gltf.Vertices, GuideMesh.Load(glbPath).Vertices);
    }

    [Theory]
    [InlineData("missing-required")]
    [InlineData("ordinary-placeholder-view")]
    [InlineData("compressed-placeholder-source")]
    [InlineData("both-extensions")]
    [InlineData("short-fallback")]
    [InlineData("stride-mismatch")]
    [InlineData("count-mismatch")]
    [InlineData("source-range")]
    [InlineData("negative-offset")]
    [InlineData("expansion-limit")]
    [InlineData("unknown-mode")]
    [InlineData("unknown-filter")]
    public void InvalidCompressedModelMetadataIsRejectedBeforeLoadingGeometry(string invalid)
    {
        using var files = new Files(); var root = TriangleRoot(TriangleSource(), "KHR_meshopt_compression");
        var view = root["bufferViews"]![0]!; var extension = view["extensions"]!["KHR_meshopt_compression"]!;
        switch (invalid)
        {
            case "missing-required": root.AsObject().Remove("extensionsRequired"); break;
            case "ordinary-placeholder-view": view.AsObject().Remove("extensions"); break;
            case "compressed-placeholder-source": extension["buffer"] = 1; break;
            case "both-extensions": view["extensions"]!["EXT_meshopt_compression"] = extension.DeepClone(); break;
            case "short-fallback": root["buffers"]![1]!["byteLength"] = 1; break;
            case "stride-mismatch": view["byteStride"] = 16; break;
            case "count-mismatch": extension["count"] = 4; break;
            case "source-range": extension["byteOffset"] = int.MaxValue; break;
            case "negative-offset": extension["byteOffset"] = -1; break;
            case "expansion-limit": root["buffers"]![1]!["byteLength"] = int.MaxValue; break;
            case "unknown-mode": extension["mode"] = "MESH"; break;
            case "unknown-filter": extension["filter"] = "BASIS"; break;
        }
        File.WriteAllText(files.Path, root.ToJsonString());
        Assert.Throws<InvalidDataException>(() => GuideMesh.Load(files.Path));
    }

    [Fact]
    public void MalformedDecoderInputsHaveBoundedFailures()
    {
        var source = TriangleSource();
        Assert.Throws<InvalidDataException>(() => GuideMeshoptDecoder.Decode(source, int.MaxValue, 12, "ATTRIBUTES", "NONE", true));
        Assert.Throws<InvalidDataException>(() => GuideMeshoptDecoder.Decode(source, 3, 3, "ATTRIBUTES", "NONE", true));
        Assert.Throws<InvalidDataException>(() => GuideMeshoptDecoder.Decode(source, 3, 12, "ATTRIBUTES", "COLOR", false));
        var badVersion = (byte[])source.Clone(); badVersion[0] = 0xa2;
        Assert.Throws<InvalidDataException>(() => GuideMeshoptDecoder.Decode(badVersion, 3, 12, "ATTRIBUTES", "NONE", true));
        Assert.Throws<InvalidDataException>(() => GuideMeshoptDecoder.Decode([0xd1, 128, 128, 128, 128, 128, 0, 0, 0, 0], 1, 4, "INDICES", "NONE", true));
        Assert.Throws<InvalidDataException>(() => GuideMeshoptDecoder.Decode([0xe1, 0, .. new byte[16]], 3, 4, "TRIANGLES", "NONE", true));
        Assert.Throws<InvalidDataException>(() => GuideMeshoptDecoder.Decode([0xe1, 0xf0, .. new byte[15], 1], 3, 4, "TRIANGLES", "NONE", true));
        var v1 = new byte[26]; v1[0] = 0xa1; v1[1] = 0xaa; v1[^1] = 0x13;
        Assert.Throws<InvalidDataException>(() => GuideMeshoptDecoder.Decode(v1, 1, 4, "ATTRIBUTES", "NONE", true));
        v1[^1] = 0x10;
        Assert.Throws<InvalidDataException>(() => GuideMeshoptDecoder.Decode(v1, 1, 4, "ATTRIBUTES", "NONE", true));
        v1[^1] = 0;
        Assert.Throws<InvalidDataException>(() => GuideMeshoptDecoder.Decode(v1, 1, 4, "ATTRIBUTES", "NONE", false));
        Assert.Throws<InvalidDataException>(() => GuideMeshoptDecoder.Decode(v1, 1, 4, "ATTRIBUTES", "OCTAHEDRAL", true));
        Assert.Throws<InvalidDataException>(() => GuideMeshoptDecoder.Decode(v1, 1, 4, "ATTRIBUTES", "COLOR", true));
    }

    // Literal v0 attribute blocks exercise ordinary glTF POSITION floats independently
    // of the official quantized sample. This is a tiny test encoder, not production.
    private static byte[] TriangleSource(int count = 3)
    {
        var positions = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }; var raw = new byte[36];
        Buffer.BlockCopy(positions, 0, raw, 0, raw.Length); var result = new List<byte> { 0xa0 };
        for (var first = 0; first < count; first += 256)
        {
            var groups = (Math.Min(count - first, 256) + 15) / 16;
            for (var lane = 0; lane < 12; lane++)
            {
                var headers = new byte[(groups + 3) / 4]; if (first == 0) headers[0] = 3;
                result.AddRange(headers);
                if (first > 0) continue;
                byte previous = 0;
                for (var i = 0; i < 16; i++)
                {
                    var current = i < 3 ? raw[i * 12 + lane] : (byte)0;
                    var delta = unchecked((sbyte)(current - previous));
                    result.Add(unchecked((byte)((delta << 1) ^ (delta >> 7)))); previous = current;
                }
            }
        }
        result.AddRange(new byte[32]); return result.ToArray();
    }

    private static JsonNode TriangleRoot(byte[] source, string extension) => JsonNode.Parse($$$$"""
        {"asset":{"version":"2.0"},"extensionsRequired":["{{{{extension}}}}"],
         "buffers":[{"byteLength":{{{{source.Length}}}},"uri":"data:application/octet-stream;base64,{{{{Convert.ToBase64String(source)}}}}"},{"byteLength":36}],
         "bufferViews":[{"buffer":1,"byteLength":36,"extensions":{"{{{{extension}}}}":{"buffer":0,"byteLength":{{{{source.Length}}}},"byteStride":12,"count":3,"mode":"ATTRIBUTES"}}}],
         "accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3"}],
         "meshes":[{"primitives":[{"attributes":{"POSITION":0}}]}]}
        """)!;

    private sealed class Files : IDisposable
    {
        public string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wayfinder-meshopt-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(Directory, "test.gltf");
        public Files()
        {
            System.IO.Directory.CreateDirectory(Directory);
            foreach (var image in System.IO.Directory.GetFiles(System.IO.Path.GetDirectoryName(Fixture("MeshoptCubeTest.gltf"))!, "*.png"))
                File.Copy(image, System.IO.Path.Combine(Directory, System.IO.Path.GetFileName(image)));
        }
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
}
