using System.Numerics;

namespace XivWayfinder.Core;

public sealed partial record GuideMesh(Vector3[] Vertices, int[] Indices, Vector4[] FaceColors)
{
    public const int MaxTriangles = 20000;
    public static GuideMesh Load(string path)
    {
        using (var source = File.OpenRead(path))
            if (source.Length > 32 * 1024 * 1024) throw new InvalidDataException("Models must be smaller than 32 MB.");
        var mesh = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".obj" => GuideObjReader.Load(path),
            ".glb" or ".gltf" => GuideGltfReader.Load(path),
            _ => throw new InvalidDataException("Choose an OBJ, GLB or glTF model.")
        };
        if (mesh.Vertices.Length == 0 || mesh.Indices.Length == 0) throw new InvalidDataException("The model has no triangles.");
        var min = new Vector3(float.PositiveInfinity); var max = new Vector3(float.NegativeInfinity);
        foreach (var v in mesh.Vertices)
        {
            if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z)) throw new InvalidDataException("The model has invalid coordinates.");
            min = Vector3.Min(min, v); max = Vector3.Max(max, v);
        }
        var size = max - min; var extent = MathF.Max(size.X, MathF.Max(size.Y, size.Z));
        if (!float.IsFinite(extent) || extent < 1e-6f) throw new InvalidDataException("The model has no usable size.");
        var center = min + size / 2;
        for (var i = 0; i < mesh.Vertices.Length; i++) mesh.Vertices[i] = (mesh.Vertices[i] - center) / extent;
        mesh.animation?.SetNormalization(center, extent);
        return mesh;
    }
}
