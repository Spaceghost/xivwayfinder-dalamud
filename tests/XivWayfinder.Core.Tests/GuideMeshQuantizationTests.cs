using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json.Nodes;

namespace XivWayfinder.Core.Tests;

public sealed partial class GuideMeshAnimationTests
{
    [Theory]
    [InlineData(5120, false)]
    [InlineData(5120, true)]
    [InlineData(5121, false)]
    [InlineData(5121, true)]
    [InlineData(5122, false)]
    [InlineData(5122, true)]
    [InlineData(5123, false)]
    [InlineData(5123, true)]
    public void QuantizedPositionsMatchTheirDequantizedFloatReference(int component, bool normalized)
    {
        using var quantized = new GlbFixture(); using var reference = new GlbFixture();
        quantized.Root["extensionsRequired"] = new JsonArray("KHR_mesh_quantization");
        var max = component switch { 5120 => 127, 5121 => 255, 5122 => 32767, _ => 65535 };
        quantized.Attributes["POSITION"] = QuantizedAccessor(quantized, [0, 0, 0, max, 0, 0, 0, max, 0], "VEC3", component, normalized);
        reference.Attributes["POSITION"] = reference.Floats(normalized ? [0, 0, 0, 1, 0, 0, 0, 1, 0] : [0, 0, 0, max, 0, 0, 0, max, 0], "VEC3");
        foreach (var file in new[] { quantized, reference })
        {
            file.Root["nodes"]![0]!["scale"] = new JsonArray(2, 1, 3);
            file.Root["nodes"]![0]!["translation"] = new JsonArray(1, 3, -2);
        }
        var expected = reference.Load(); var actual = quantized.Load();
        Assert.Equal(expected.Indices, actual.Indices);
        for (var i = 0; i < actual.Vertices.Length; i++) Near(expected.Vertices[i], actual.Vertices[i]);
    }

    [Theory]
    [InlineData(5120)]
    [InlineData(5122)]
    public void QuantizedMorphNormalsKeepTheirAuthoredValuesUntilAfterMorphing(int component)
    {
        using var file = new GlbFixture();
        file.Root["extensionsRequired"] = new JsonArray("KHR_mesh_quantization");
        var max = component == 5120 ? 127 : 32767; var diagonal = (int)(max * .7f);
        file.Attributes["NORMAL"] = QuantizedAccessor(file, [0, diagonal, diagonal, 0, diagonal, diagonal, 0, diagonal, diagonal], "VEC3", component, true);
        var target = QuantizedAccessor(file, [0, -max, 0, 0, -max, 0, 0, -max, 0], "VEC3", component, true);
        file.Primitive["targets"] = new JsonArray(new JsonObject { ["NORMAL"] = target });
        file.Clip("morph", 0, "weights", [0, 1], [0, 1]);
        var mesh = file.Load(); var positions = new Vector3[3]; var normals = new Vector3[3];
        mesh.SamplePose(0, .5f, positions, normals: normals);
        var expected = Vector3.Normalize(new(0, diagonal / (float)max - .5f, diagonal / (float)max));
        foreach (var normal in normals) Near(expected, normal);
    }

    [Fact]
    public void QuantizedSkinPositionsUseInverseBindDequantizationAndStillAnimate()
    {
        using var file = new GlbFixture(); file.Root["extensionsRequired"] = new JsonArray("KHR_mesh_quantization");
        file.Attributes["POSITION"] = QuantizedAccessor(file, [0, 0, 0, 100, 0, 0, 0, 100, 0], "VEC3", 5122, false);
        file.Attributes["NORMAL"] = QuantizedAccessor(file, [0, 0, 127, 0, 0, 127, 0, 0, 127], "VEC3", 5120, true);
        var inverse = file.Floats(MatrixValues(Matrix4x4.CreateScale(.01f)), "MAT4");
        file.Root["nodes"] = JsonNode.Parse("""[{"mesh":0,"skin":0},{}]""");
        file.Root["scenes"] = JsonNode.Parse("""[{"nodes":[0,1]}]""");
        file.Root["skins"] = new JsonArray(new JsonObject { ["joints"] = new JsonArray(1), ["inverseBindMatrices"] = inverse });
        file.Attributes["JOINTS_0"] = file.Bytes(new byte[12], "VEC4", false);
        file.Attributes["WEIGHTS_0"] = file.Floats([1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0], "VEC4");
        file.Clip("move", 1, "translation", [0, 1], [0, 0, 0, 0, 0, 2]);
        var mesh = file.Load(); var positions = new Vector3[3]; var normals = new Vector3[3];
        mesh.SamplePose(0, .5f, positions, normals: normals);
        Near(new(-.5f, -.5f, 1), positions[0]); Assert.All(normals, n => Assert.Equal(Vector3.UnitZ, n));
    }

    [Fact]
    public void SignedQuantizedMorphPositionsApplyBeforeNodeScale()
    {
        using var file = new GlbFixture(); file.Root["extensionsRequired"] = new JsonArray("KHR_mesh_quantization");
        var target = QuantizedAccessor(file, [0, 0, 0, 0, 0, 0, 0, -4, 0], "VEC3", 5122, false);
        file.Primitive["targets"] = new JsonArray(new JsonObject { ["POSITION"] = target });
        file.Root["nodes"]![0]!["scale"] = new JsonArray(1, 2, 1);
        file.Clip("morph", 0, "weights", [0, 1], [0, 1]);
        var mesh = file.Load(); var pose = new Vector3[3]; mesh.SamplePose(0, .5f, pose);
        Near(mesh.Vertices[2] - new Vector3(0, 2, 0), pose[2]);
    }

    [Fact]
    public void SignedUnnormalizedTextureCoordinatesUseTextureTransformDequantization()
    {
        using var file = new GlbFixture(); file.Root["extensionsRequired"] = new JsonArray("KHR_mesh_quantization", "KHR_texture_transform");
        file.Attributes["TEXCOORD_0"] = QuantizedAccessor(file, [-100, 0, 100, 0, 0, 100], "VEC2", 5122, false);
        SetFallbackTexture(file);
        file.Root["materials"]![0]!["pbrMetallicRoughness"]!["baseColorTexture"]!["extensions"] =
            JsonNode.Parse("""{"KHR_texture_transform":{"offset":[0.5,0],"scale":[0.005,0.01]}}""");
        var mesh = file.Load();
        Assert.Equal(Vector2.Zero, mesh.TextureCoordinates[0]); Assert.Equal(Vector2.UnitX, mesh.TextureCoordinates[1]);
        Assert.Equal(new Vector2(.5f, 1), mesh.TextureCoordinates[2]);
    }

    [Theory]
    [InlineData("undeclared-position")]
    [InlineData("unsigned-normal")]
    [InlineData("unnormalized-normal")]
    [InlineData("unsigned-morph")]
    public void InvalidQuantizationSemanticsAreRejected(string kind)
    {
        using var file = new GlbFixture(); file.Root["extensionsRequired"] = new JsonArray("KHR_mesh_quantization");
        switch (kind)
        {
            case "undeclared-position":
                file.Root.Remove("extensionsRequired");
                file.Attributes["POSITION"] = QuantizedAccessor(file, [0, 0, 0, 100, 0, 0, 0, 100, 0], "VEC3", 5122, false); break;
            case "unsigned-normal": file.Attributes["NORMAL"] = QuantizedAccessor(file, [0, 0, 255, 0, 0, 255, 0, 0, 255], "VEC3", 5121, true); break;
            case "unnormalized-normal": file.Attributes["NORMAL"] = QuantizedAccessor(file, [0, 0, 127, 0, 0, 127, 0, 0, 127], "VEC3", 5120, false); break;
            case "unsigned-morph": file.Primitive["targets"] = new JsonArray(new JsonObject { ["POSITION"] = QuantizedAccessor(file, new int[9], "VEC3", 5121, false) }); break;
        }
        Assert.Throws<InvalidDataException>(() => file.Load());
    }

    [Theory]
    [InlineData("KHR_texture_basisu", "image/ktx2")]
    [InlineData("EXT_texture_webp", "image/webp")]
    public void OptionalCompressedTextureUsesItsRegularFallbackWithoutLoadingUnusedImage(string extension, string mime)
    {
        using var file = new GlbFixture(); file.Attributes["TEXCOORD_0"] = file.Floats([0, 0, 1, 0, 0, 1], "VEC2");
        SetFallbackTexture(file);
        file.Root["images"]!.AsArray().Insert(0, new JsonObject { ["uri"] = "missing-compressed-texture", ["mimeType"] = mime });
        file.Root["textures"]![0]!["source"] = 1;
        file.Root["textures"]![0]!["extensions"] = new JsonObject { [extension] = new JsonObject { ["source"] = 0 } };
        file.Root["extensionsUsed"] = new JsonArray(extension);
        var mesh = file.Load();
        Assert.Equal("image/png", Assert.Single(mesh.Images).MimeType); Assert.Equal(0, mesh.Materials[0].BaseColorImage);
        file.Root["extensionsRequired"] = new JsonArray(extension);
        Assert.Contains(extension, Assert.Throws<InvalidDataException>(() => file.Load()).Message);
    }

    [Fact]
    public void SelectedTextureIsDeduplicatedAndAnInvalidSelectedSourceIsNotHidden()
    {
        using var file = new GlbFixture(); file.Attributes["TEXCOORD_0"] = file.Floats([0, 0, 1, 0, 0, 1], "VEC2"); SetFallbackTexture(file);
        file.Root["materials"]!.AsArray().Add(file.Root["materials"]![0]!.DeepClone());
        Assert.Single(file.Load().Images);
        file.Root["textures"]![0]!["source"] = 99;
        Assert.Throws<InvalidDataException>(() => file.Load());
    }

    private static void SetFallbackTexture(GlbFixture file)
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        file.Root["images"] = new JsonArray(new JsonObject { ["bufferView"] = file.Buffer(png), ["mimeType"] = "image/png" });
        file.Root["textures"] = JsonNode.Parse("""[{"source":0}]""");
        file.Root["materials"] = JsonNode.Parse("""[{"pbrMetallicRoughness":{"baseColorTexture":{"index":0}}}]""");
        file.Primitive["material"] = 0;
    }

    private static int QuantizedAccessor(GlbFixture file, int[] values, string type, int component, bool normalized)
    {
        var width = type == "VEC3" ? 3 : 2; var size = component is 5120 or 5121 ? 1 : 2;
        var stride = (width * size + 3) / 4 * 4; var count = values.Length / width; var bytes = new byte[count * stride];
        for (var i = 0; i < count; i++) for (var c = 0; c < width; c++)
        {
            var at = i * stride + c * size; var value = values[i * width + c];
            if (component is 5120 or 5121) bytes[at] = unchecked((byte)value);
            else BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), unchecked((ushort)value));
        }
        var view = file.Buffer(bytes); file.Root["bufferViews"]![view]!["byteStride"] = stride;
        return file.Accessor(new JsonObject { ["bufferView"] = view, ["type"] = type, ["componentType"] = component, ["normalized"] = normalized, ["count"] = count });
    }
}
