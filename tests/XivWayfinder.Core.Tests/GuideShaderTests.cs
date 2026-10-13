using System.Security.Cryptography;
using System.Text;

namespace XivWayfinder.Core.Tests;

/// <summary>Checks the actual shipped D3D11 bytecode, not a second copy of the HLSL declarations.</summary>
public sealed class GuideShaderTests
{
    private static string ShaderDirectory => Path.Combine(CommandAndIpcTests.RepoRoot(), "src", "XivWayfinder.Plugin", "Shaders");

    [Theory]
    [InlineData("GuideVertex")]
    [InlineData("GuidePixel")]
    public void CompiledConstantsMatchTheNativeBufferLayout(string name)
    {
        var shader = Load(name);
        var data = Chunk(shader, "RDEF");
        Assert.Equal(1u, U32(shader, data));
        var buffer = data + (int)U32(shader, data + 4);
        Assert.Equal("Guide", Name(shader, data + (int)U32(shader, buffer)));
        Assert.Equal(176u, U32(shader, buffer + 12));
        var expected = new Dictionary<string, (uint Offset, uint Size)>
        {
            ["ViewProjection"] = (0, 64),
            ["InverseViewProjection"] = (64, 64),
            ["Camera"] = (128, 16),
            ["View"] = (144, 16),
            ["Material"] = (160, 16),
        };
        var count = U32(shader, buffer + 4);
        Assert.Equal((uint)expected.Count, count);
        var variables = data + (int)U32(shader, buffer + 8);
        for (var i = 0; i < count; i++)
        {
            var variable = variables + i * 40;
            var field = Name(shader, data + (int)U32(shader, variable));
            Assert.True(expected.Remove(field, out var expectedField), $"Unexpected constant {field}");
            Assert.Equal(expectedField.Offset, U32(shader, variable + 4));
            Assert.Equal(expectedField.Size, U32(shader, variable + 8));
            var type = data + (int)U32(shader, variable + 16);
            Assert.Equal((ushort)3, U16(shader, type + 2)); // D3D_SVT_FLOAT
            Assert.Equal((ushort)4, U16(shader, type + 6));
            if (expectedField.Size == 64)
            {
                Assert.Equal((ushort)2, U16(shader, type)); // D3D_SVC_MATRIX_ROWS, matching Matrix4x4 memory.
                Assert.Equal((ushort)4, U16(shader, type + 4));
            }
            else
            {
                Assert.Equal((ushort)1, U16(shader, type)); // D3D_SVC_VECTOR
                Assert.Equal((ushort)1, U16(shader, type + 4));
            }
        }
        Assert.Empty(expected);
    }

    [Fact]
    public void CompiledInputsMatchTheVertexStreamAndLinkToThePixelShader()
    {
        var vertex = Load("GuideVertex");
        var pixel = Load("GuidePixel");
        Assert.Equal(0x10050u, U32(vertex, Chunk(vertex, "SHDR"))); // vs_5_0
        Assert.Equal(0x00050u, U32(pixel, Chunk(pixel, "SHDR"))); // ps_5_0
        Assert.Equal(new[]
        {
            new Signature("POSITION", 0, 3, 7, 0), new Signature("NORMAL", 0, 3, 7, 1),
            new Signature("COLOR", 0, 3, 15, 2), new Signature("TEXCOORD", 0, 3, 3, 3),
        }, Signatures(vertex, "ISGN"));
        Assert.Equal(Signatures(vertex, "OSGN"), Signatures(pixel, "ISGN"));
    }

    [Fact]
    public void CompiledPixelShaderWritesPrivateDepthAndBindsTheCorrectSceneResources()
    {
        var shader = Load("GuidePixel");
        Assert.Equal(new[]
        {
            new Signature("SV_TARGET", 0, 3, 15, 0),
            new Signature("SV_DEPTH", 0, 3, 1, uint.MaxValue),
        }, Signatures(shader, "OSGN"));
        var data = Chunk(shader, "RDEF");
        var expected = new Dictionary<string, (uint Type, uint Slot)>
        {
            ["Guide"] = (0, 0),
            ["ModelSampler"] = (3, 0),
            ["SceneDepth"] = (2, 0),
            ["BaseColor"] = (2, 1),
        };
        var count = U32(shader, data + 8);
        Assert.Equal((uint)expected.Count, count);
        var bindings = data + (int)U32(shader, data + 12);
        for (var i = 0; i < count; i++)
        {
            var binding = bindings + i * 32;
            var name = Name(shader, data + (int)U32(shader, binding));
            Assert.True(expected.Remove(name, out var wanted), $"Unexpected shader resource {name}");
            Assert.Equal(wanted.Type, U32(shader, binding + 4));
            Assert.Equal(wanted.Slot, U32(shader, binding + 20));
            Assert.Equal(1u, U32(shader, binding + 24));
            if (wanted.Type == 2) Assert.Equal(4u, U32(shader, binding + 12)); // Texture2D, not MSAA or arrays.
        }
        Assert.Empty(expected);
    }

    [Fact]
    public void ShippedBytecodeAndSourcesMatchTheSameCompilerRun()
    {
        var expected = new HashSet<string> { "GuideVertex.hlsl", "GuidePixel.hlsl", "GuideVertex.dxbc", "GuidePixel.dxbc" };
        foreach (var line in File.ReadAllLines(Path.Combine(ShaderDirectory, "GuideShaders.sha256")))
        {
            var entry = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, entry.Length);
            Assert.True(expected.Remove(entry[1]), $"Unexpected or duplicate shader manifest entry {entry[1]}");
            var actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(ShaderDirectory, entry[1]))));
            Assert.True(entry[0] == actual, $"{entry[1]} changed. Rebuild with tools/build-guide-shaders.sh.");
        }
        Assert.Empty(expected);
    }

    private readonly record struct Signature(string Name, uint Index, uint ComponentType, byte Mask, uint Register);

    private static Signature[] Signatures(byte[] shader, string kind)
    {
        var data = Chunk(shader, kind);
        var result = new Signature[U32(shader, data)];
        for (var i = 0; i < result.Length; i++)
        {
            var descriptor = data + 8 + i * 24;
            result[i] = new(Name(shader, data + (int)U32(shader, descriptor)).ToUpperInvariant(),
                U32(shader, descriptor + 4), U32(shader, descriptor + 12), shader[descriptor + 20], U32(shader, descriptor + 16));
        }
        return result;
    }

    private static byte[] Load(string name)
    {
        var bytes = File.ReadAllBytes(Path.Combine(ShaderDirectory, name + ".dxbc"));
        Assert.Equal("DXBC", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal((uint)bytes.Length, U32(bytes, 24));
        return bytes;
    }

    private static int Chunk(byte[] bytes, string kind)
    {
        for (var i = 0; i < U32(bytes, 28); i++)
        {
            var chunk = (int)U32(bytes, 32 + i * 4);
            if (Encoding.ASCII.GetString(bytes, chunk, 4) == kind) return chunk + 8;
        }
        throw new InvalidDataException($"Compiled shader is missing its {kind} chunk.");
    }
    private static uint U32(byte[] bytes, int offset) => BitConverter.ToUInt32(bytes, offset);
    private static ushort U16(byte[] bytes, int offset) => BitConverter.ToUInt16(bytes, offset);
    private static string Name(byte[] bytes, int offset) => Encoding.ASCII.GetString(bytes, offset, Array.IndexOf(bytes, (byte)0, offset) - offset);
}
