using System.Numerics;
using System.Text.Json;

namespace XivWayfinder.Core;

/// <summary>Bounded glTF 2 importer with embedded or local buffers and PNG/JPEG images.</summary>
internal sealed class GuideGltfReader(JsonElement root, byte[]? embeddedBin, GuideModelResources resources)
{
    private const int MaxVertices = GuideMesh.MaxTriangles * 3;
    private readonly List<Vector3> vertices = [];
    private readonly List<Vector2> uv = [];
    private readonly List<Vector4> vertexColors = [];
    private readonly List<int> indices = [];
    private readonly List<Vector4> faceColors = [];
    private readonly List<int> faceMaterials = [];
    private readonly List<GuideMeshBinding> bindings = [];
    private readonly List<GuideMeshImage> images = [];
    private readonly List<GuideMeshMaterial> materials = [];
    private readonly List<(int TexCoord, Vector2 Offset, Vector2 Scale, float Rotation)> textureTransforms = [];
    private GuideMeshNode[] nodes = [];
    private GuideMeshSkin[] skins = [];
    private int[] nodeOrder = [];
    private int morphVertices;
    private long decodedValues;
    private byte[][] buffers = [];
    private int[] bufferLengths = [];
    private bool quantized;
    private bool meshoptRequired, meshoptKhrRequired;
    private readonly Dictionary<int, byte[]> meshoptViews = [];
    private long meshoptDecodedBytes;

    public static GuideMesh Load(string path)
    {
        try
        {
            var resources = new GuideModelResources(path);
            var source = resources.ReadModel();
            if (Path.GetExtension(path).Equals(".gltf", StringComparison.OrdinalIgnoreCase))
            {
                var offset = source.AsSpan().StartsWith(new byte[] { 239, 187, 191 }) ? 3 : 0;
                using var gltf = JsonDocument.Parse(source.AsMemory(offset));
                return new GuideGltfReader(gltf.RootElement, null, resources).Read() with
                { SourceSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(source)).ToLowerInvariant() };
            }
            using var stream = new MemoryStream(source, false); using var reader = new BinaryReader(stream);
            if (stream.Length < 20 || reader.ReadUInt32() != 0x46546C67 || reader.ReadUInt32() != 2 || reader.ReadUInt32() != stream.Length)
                throw Error("Invalid GLB 2 file.");
            byte[]? json = null, bin = null;
            while (stream.Position < stream.Length)
            {
                if (stream.Length - stream.Position < 8) throw Error("Truncated GLB chunk.");
                var length = reader.ReadUInt32(); var type = reader.ReadUInt32();
                if (length % 4 != 0 || length > stream.Length - stream.Position) throw Error("Invalid GLB chunk bounds.");
                var bytes = reader.ReadBytes((int)length);
                if (type == 0x4E4F534A)
                {
                    if (json is not null || stream.Position != 20 + length) throw Error("GLB JSON must be its first chunk.");
                    json = bytes;
                }
                else if (type == 0x004E4942)
                {
                    if (bin is not null) throw Error("GLB has duplicate binary chunks.");
                    bin = bytes;
                }
            }
            if (json is null) throw Error("GLB needs a JSON chunk.");
            using var doc = JsonDocument.Parse(json);
            return new GuideGltfReader(doc.RootElement, bin, resources).Read() with
            { SourceSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(source)).ToLowerInvariant() };
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException or IndexOutOfRangeException or OverflowException or EndOfStreamException or FormatException)
        { throw new InvalidDataException("The glTF model contains invalid or incompatible data.", ex); }
    }

    private GuideMesh Read()
    {
        if (root.GetProperty("asset").GetProperty("version").GetString() != "2.0") throw Error("Choose a glTF 2 model.");
        if (root.TryGetProperty("extensionsRequired", out var extensions))
            foreach (var extension in extensions.EnumerateArray())
            {
                if (extension.GetString() == "KHR_mesh_quantization") quantized = true;
                else if (extension.GetString() == "EXT_meshopt_compression") meshoptRequired = true;
                else if (extension.GetString() == "KHR_meshopt_compression") meshoptKhrRequired = true;
                else if (extension.GetString() is not ("KHR_texture_transform" or "KHR_materials_unlit"))
                    throw Error($"This model requires {extension.GetString()}. Export uncompressed glTF or GLB with standard materials.");
            }
        ReadBuffers(); ReadMaterials(); ReadNodes(); ReadSkins();
        var rendered = new HashSet<int>();
        void Visit(int id)
        {
            if (!rendered.Add(id)) throw Error("A scene contains repeated nodes.");
            var n = At("nodes", id);
            if (n.TryGetProperty("mesh", out var mesh)) ReadMesh(mesh.GetInt32(), id, n.TryGetProperty("skin", out var skin) ? Index(skin.GetInt32(), skins.Length) : -1);
            if (n.TryGetProperty("children", out var children)) foreach (var child in children.EnumerateArray()) Visit(child.GetInt32());
        }
        if (root.TryGetProperty("scenes", out var scenes))
        {
            var scene = scenes[Index(Number(root, "scene"), scenes.GetArrayLength())];
            if (scene.TryGetProperty("nodes", out var sceneNodes))
                foreach (var id in sceneNodes.EnumerateArray())
                {
                    var node = Index(id.GetInt32(), nodes.Length);
                    if (nodes[node].Parent != -1) throw Error("A scene root cannot have a parent.");
                    Visit(node);
                }
        }
        else if (nodes.Length > 0)
        {
            for (var i = 0; i < nodes.Length; i++) if (nodes[i].Parent == -1) Visit(i);
        }
        else
        {
            // Retain support for simple static exporters that omit their scene/node hierarchy.
            nodes = [new(-1, null, Vector3.Zero, Quaternion.Identity, Vector3.One, [])]; nodeOrder = [0];
            var meshes = root.GetProperty("meshes");
            for (var i = 0; i < meshes.GetArrayLength(); i++) ReadMesh(i, 0, -1);
        }
        var (clips, channels) = ReadAnimations();
        var animation = new GuideMeshAnimation(nodes, nodeOrder, bindings.ToArray(), channels);
        var bindPose = new Vector3[vertices.Count]; var bindNormals = new Vector3[vertices.Count];
        animation.Sample(-1, 0, bindPose, normals: bindNormals);
        return new GuideMesh(bindPose, indices.ToArray(), faceColors.ToArray())
        {
            animation = animation,
            Animations = clips,
            TextureCoordinates = uv.ToArray(),
            VertexColors = vertexColors.ToArray(),
            FaceMaterials = faceMaterials.ToArray(),
            Materials = materials.ToArray(),
            Images = images.ToArray(),
            Normals = bindNormals
        };
    }

    private void ReadBuffers()
    {
        var source = root.GetProperty("buffers"); var count = source.GetArrayLength();
        if (count is < 1 or > 256) throw Error("Use between 1 and 256 model buffers.");
        buffers = new byte[count][]; bufferLengths = new int[count];
        long declaredBytes = 0;
        for (var i = 0; i < count; i++)
        {
            var buffer = source[i]; var length = Number(buffer, "byteLength");
            if (length <= 0 || (declaredBytes += length) > GuideModelResources.MaxBytes)
                throw Error("Model buffers must total at most 32 MB.");
            byte[] bytes;
            var compressedFallback = Meshopt(buffer, out var fallbackExtension, out var fallbackKhr)
                && fallbackExtension.TryGetProperty("fallback", out var fallback) && fallback.GetBoolean();
            var placeholder = !buffer.TryGetProperty("uri", out var uri) && (i != 0 || embeddedBin is null);
            if (compressedFallback || placeholder)
            {
                // Required compressed models may declare an unbacked buffer for the
                // decoded layout. Never allocate/read it or let ordinary views use it.
                var references = 0;
                if (root.TryGetProperty("bufferViews", out var views))
                    foreach (var view in views.EnumerateArray())
                    {
                        var compressed = Meshopt(view, out var extension, out var khr);
                        if (compressed && Number(extension, "buffer", -1) == i)
                            throw Error("A meshopt fallback buffer cannot contain compressed source data.");
                        if (Number(view, "buffer", -1) != i) continue;
                        references++;
                        if (!compressed || compressedFallback && khr != fallbackKhr
                            || placeholder && !(khr ? meshoptKhrRequired : meshoptRequired))
                            throw Error("A meshopt fallback buffer can only back matching compressed views.");
                        var start = Number(view, "byteOffset"); var viewLength = Number(view, "byteLength");
                        if (start < 0 || viewLength <= 0 || (long)start + viewLength > length)
                            throw Error("Invalid meshopt fallback bounds.");
                    }
                if (references == 0 && placeholder) throw Error("A glTF buffer needs a local URI or compressed views.");
                buffers[i] = []; bufferLengths[i] = length; continue;
            }
            if (uri.ValueKind != JsonValueKind.Undefined) bytes = resources.ReadUri(uri.GetString() ?? "", false).Data;
            else
            {
                if (i != 0 || embeddedBin is null) throw Error("A glTF buffer needs a local URI or the first GLB binary chunk.");
                bytes = embeddedBin;
                if (bytes.Length - length > 3) throw Error("Invalid GLB binary padding.");
            }
            if (bytes.Length < length) throw Error("A model buffer is shorter than its declared length.");
            buffers[i] = bytes; bufferLengths[i] = length;
        }
    }

    private void ReadNodes()
    {
        if (!root.TryGetProperty("nodes", out var source)) return;
        var count = source.GetArrayLength(); if (count > 4096) throw Error("Use at most 4,096 model nodes.");
        var parents = Enumerable.Repeat(-1, count).ToArray();
        for (var i = 0; i < count; i++)
            if (source[i].TryGetProperty("children", out var children))
                foreach (var child in children.EnumerateArray())
                {
                    var id = Index(child.GetInt32(), count);
                    if (parents[id] != -1 || id == i) throw Error("Invalid model node hierarchy.");
                    parents[id] = i;
                }
        nodes = new GuideMeshNode[count];
        for (var i = 0; i < count; i++)
        {
            var n = source[i]; Matrix4x4? matrix = n.TryGetProperty("matrix", out var m) ? Matrix(Floats(m, 16)) : null;
            if (matrix is not null && (n.TryGetProperty("translation", out _) || n.TryGetProperty("rotation", out _) || n.TryGetProperty("scale", out _)))
                throw Error("A node cannot combine a matrix and TRS transforms.");
            var translation = n.TryGetProperty("translation", out var t) ? V3(Floats(t, 3), 0) : Vector3.Zero;
            var scale = n.TryGetProperty("scale", out var s) ? V3(Floats(s, 3), 0) : Vector3.One;
            var rotation = n.TryGetProperty("rotation", out var r) ? Q(Floats(r, 4), 0) : Quaternion.Identity;
            rotation = UnitQuaternion(rotation);
            var weights = Array.Empty<float>();
            if (n.TryGetProperty("mesh", out var meshId))
            {
                var mesh = At("meshes", meshId.GetInt32());
                var primitives = mesh.GetProperty("primitives");
                var targets = primitives.GetArrayLength() == 0 ? 0 : TargetCount(primitives[0]);
                if (targets > 64) throw Error("Use at most 64 morph targets per mesh.");
                foreach (var primitive in primitives.EnumerateArray()) if (TargetCount(primitive) != targets) throw Error("Mesh primitives must have matching morph targets.");
                weights = n.TryGetProperty("weights", out var w) ? Floats(w, targets)
                    : mesh.TryGetProperty("weights", out w) ? Floats(w, targets) : new float[targets];
            }
            nodes[i] = new(parents[i], matrix, translation, rotation, scale, weights);
        }
        var visited = new byte[count]; var ordered = new List<int>(count); var depths = new int[count];
        void Order(int i, int depth)
        {
            if (depth > 64 || visited[i] == 1) throw Error("Invalid or excessive model node hierarchy.");
            if (visited[i] == 2) return;
            visited[i] = 1; if (parents[i] >= 0) Order(parents[i], depth + 1);
            depths[i] = parents[i] < 0 ? 0 : depths[parents[i]] + 1;
            if (depths[i] > 64) throw Error("Use at most 64 nested model nodes.");
            visited[i] = 2; ordered.Add(i);
        }
        for (var i = 0; i < count; i++) Order(i, 0);
        nodeOrder = ordered.ToArray();
    }

    private void ReadSkins()
    {
        if (!root.TryGetProperty("skins", out var source)) return;
        if (source.GetArrayLength() > 256) throw Error("Too many skins.");
        skins = new GuideMeshSkin[source.GetArrayLength()];
        for (var s = 0; s < skins.Length; s++)
        {
            var skin = source[s]; var joints = skin.GetProperty("joints").EnumerateArray().Select(j => Index(j.GetInt32(), nodes.Length)).ToArray();
            if (joints.Length is < 1 or > 4096 || joints.Distinct().Count() != joints.Length) throw Error("Invalid skin joints.");
            if (skin.TryGetProperty("skeleton", out var skeleton)) Index(skeleton.GetInt32(), nodes.Length);
            var matrices = Enumerable.Repeat(Matrix4x4.Identity, joints.Length).ToArray();
            if (skin.TryGetProperty("inverseBindMatrices", out var accessor))
            {
                var values = Access(accessor.GetInt32(), "MAT4", out var component);
                // The glTF skin may use a prefix of a shared matrix accessor;
                // its count must be at least the joint count, not exactly equal.
                if (component != 5126 || values.Length < joints.Length * 16) throw Error("Invalid inverse bind matrices.");
                for (var i = 0; i < joints.Length; i++) matrices[i] = Matrix(values.AsSpan(i * 16, 16));
            }
            skins[s] = new(joints, matrices);
        }
    }

    private void ReadMesh(int id, int node, int skinIndex)
    {
        var skin = skinIndex < 0 ? null : skins[skinIndex];
        foreach (var p in At("meshes", id).GetProperty("primitives").EnumerateArray())
        {
            var primitiveMode = Number(p, "mode", 4);
            if (primitiveMode is not (4 or 5 or 6)) throw Error("Export the model with triangles, triangle strips or triangle fans.");
            var attributes = p.GetProperty("attributes");
            var data = Access(attributes.GetProperty("POSITION").GetInt32(), "VEC3", out var component);
            var count = data.Length / 3;
            if (component != 5126 && !(quantized && component is 5120 or 5121 or 5122 or 5123))
                throw Error("Integer positions need KHR_mesh_quantization with byte or short components.");
            // Accessor capacity can exceed the indexed primitive's geometry.
            // Apply the retained-vertex budget after dropping unused elements;
            // Access already bounds temporary decoding and the source resources.
            var first = vertices.Count;
            var firstIndex = indices.Count;
            var positions = new Vector3[count];
            for (var i = 0; i < count; i++) { positions[i] = V3(data, i * 3); vertices.Add(positions[i]); }
            var normals = Array.Empty<Vector3>();
            if (attributes.TryGetProperty("NORMAL", out var normalId))
            {
                var values = Access(normalId.GetInt32(), "VEC3", out var normalComponent);
                if (!NormalComponent(normalId.GetInt32(), normalComponent) || values.Length != count * 3)
                    throw Error("Normals need float or quantized signed normalized vectors matching the vertex count.");
                normals = new Vector3[count];
                // Normalize after morphing and normal transforms: quantized base normals
                // are approximate, and premature normalization changes authored morph deltas.
                for (var i = 0; i < count; i++) normals[i] = V3(values, i * 3);
            }
            var material = p.TryGetProperty("material", out var mat) ? Index(mat.GetInt32(), materials.Count - 1) : materials.Count - 1;
            var texture = textureTransforms[material];
            float[]? coordinates = null;
            if (attributes.TryGetProperty($"TEXCOORD_{texture.TexCoord}", out var tex))
            {
                coordinates = Access(tex.GetInt32(), "VEC2", out var uvComponent);
                if (uvComponent != 5126 && !(uvComponent is 5121 or 5123 && IsNormalized(tex.GetInt32()))
                    && !(quantized && uvComponent is 5120 or 5121 or 5122 or 5123))
                    throw Error("Texture coordinates need floats, normalized unsigned integers or KHR_mesh_quantization byte/short values.");
            }
            if (coordinates is not null && coordinates.Length != count * 2) throw Error("Texture coordinate count must match positions.");
            if (materials[material].BaseColorImage >= 0 && coordinates is null) throw Error("The material is missing its texture coordinates.");
            var sine = MathF.Sin(texture.Rotation); var cosine = MathF.Cos(texture.Rotation);
            for (var i = 0; i < count; i++)
            {
                var v = coordinates is null ? Vector2.Zero : new Vector2(coordinates[i * 2], coordinates[i * 2 + 1]) * texture.Scale;
                var transformedUv = new Vector2(cosine * v.X - sine * v.Y, sine * v.X + cosine * v.Y) + texture.Offset;
                if (!float.IsFinite(transformedUv.X) || !float.IsFinite(transformedUv.Y)) throw Error("The material texture transform produces invalid coordinates.");
                uv.Add(transformedUv);
            }
            float[]? colors = null; var colorWidth = 4;
            if (attributes.TryGetProperty("COLOR_0", out var colorsId))
            {
                var type = At("accessors", colorsId.GetInt32()).GetProperty("type").GetString();
                if (type is not ("VEC3" or "VEC4")) throw Error("Invalid vertex colors.");
                colorWidth = type == "VEC3" ? 3 : 4; colors = Access(colorsId.GetInt32(), type, out var colorComponent);
                if (colorComponent is not (5121 or 5123 or 5126) || (colorComponent != 5126 && !IsNormalized(colorsId.GetInt32()))) throw Error("Colors need floats or normalized unsigned integers.");
                if (colors.Length != count * colorWidth || colors.Any(f => f < 0 || f > 1)) throw Error("Invalid vertex colors.");
            }
            for (var i = 0; i < count; i++) vertexColors.Add(colors is null ? Vector4.One : new(colors[i * colorWidth], colors[i * colorWidth + 1], colors[i * colorWidth + 2], colorWidth == 4 ? colors[i * 4 + 3] : 1));
            var jointSets = new List<ushort[]>(); var weightSets = new List<Vector4[]>();
            if (skin is not null)
            {
                for (var set = 0; attributes.TryGetProperty($"JOINTS_{set}", out var jointsId); set++)
                {
                    if (set >= 8 || !attributes.TryGetProperty($"WEIGHTS_{set}", out var weightsId)) throw Error("Invalid or excessive joint influence sets.");
                    var joints = Access(jointsId.GetInt32(), "VEC4", out var jointComponent);
                    var weights = Access(weightsId.GetInt32(), "VEC4", out var weightComponent);
                    if (jointComponent is not (5121 or 5123) || IsNormalized(jointsId.GetInt32()) || weightComponent is not (5121 or 5123 or 5126)
                        || (weightComponent != 5126 && !IsNormalized(weightsId.GetInt32())) || joints.Length != count * 4 || weights.Length != count * 4)
                        throw Error("Invalid skin attributes.");
                    var jointValues = new ushort[count * 4]; var weightValues = new Vector4[count];
                    for (var i = 0; i < count * 4; i++)
                    {
                        if (joints[i] >= skin.Joints.Length || weights[i] < 0) throw Error("Invalid joint index or skin weight.");
                        jointValues[i] = (ushort)joints[i];
                    }
                    for (var i = 0; i < count; i++) weightValues[i] = V4(weights, i * 4);
                    jointSets.Add(jointValues); weightSets.Add(weightValues);
                }
                if (jointSets.Count == 0) throw Error("A skinned mesh must provide joint and weight attributes.");
                foreach (var attribute in attributes.EnumerateObject())
                    if ((attribute.Name.StartsWith("JOINTS_", StringComparison.Ordinal) || attribute.Name.StartsWith("WEIGHTS_", StringComparison.Ordinal))
                        && (!int.TryParse(attribute.Name[(attribute.Name.IndexOf('_') + 1)..], out var set) || set < 0 || set >= jointSets.Count))
                        throw Error("Joint and weight sets must be paired and consecutive.");
                for (var v = 0; v < count; v++)
                {
                    var total = 0f; foreach (var weights in weightSets) { var w = weights[v]; total += w.X + w.Y + w.Z + w.W; }
                    if (!float.IsFinite(total) || total < 1e-8f) throw Error("Every skinned vertex needs a nonzero weight.");
                    foreach (var weights in weightSets) weights[v] /= total;
                }
            }
            var targets = new List<Vector3[]>(); var normalTargets = new List<Vector3[]>();
            if (p.TryGetProperty("targets", out var morphs))
                foreach (var target in morphs.EnumerateArray())
                {
                    morphVertices += count; if (morphVertices > 2_000_000) throw Error("The model has too much morph target geometry.");
                    float[] offsets;
                    if (target.TryGetProperty("POSITION", out var targetId))
                    {
                        offsets = Access(targetId.GetInt32(), "VEC3", out var targetComponent);
                        if (targetComponent != 5126 && !(quantized && targetComponent is 5120 or 5122))
                            throw Error("Morph positions need floats or signed KHR_mesh_quantization byte/short values.");
                    }
                    else offsets = new float[count * 3];
                    if (offsets.Length != count * 3) throw Error("Morph target count must match positions.");
                    var values = new Vector3[count]; for (var v = 0; v < count; v++) values[v] = V3(offsets, v * 3);
                    targets.Add(values);
                    var normalDeltas = Array.Empty<Vector3>();
                    if (target.TryGetProperty("NORMAL", out var normalTargetId))
                    {
                        if (normals.Length == 0) throw Error("Morph normal targets need base vertex normals.");
                        morphVertices += count; if (morphVertices > 2_000_000) throw Error("The model has too much morph target geometry.");
                        var normalValues = Access(normalTargetId.GetInt32(), "VEC3", out var normalComponent);
                        if (!NormalComponent(normalTargetId.GetInt32(), normalComponent) || normalValues.Length != count * 3)
                            throw Error("Morph normals need float or quantized signed normalized vectors matching the vertex count.");
                        normalDeltas = new Vector3[count];
                        for (var v = 0; v < count; v++) normalDeltas[v] = V3(normalValues, v * 3);
                    }
                    normalTargets.Add(normalDeltas);
                }
            if (targets.Count != nodes[node].Weights.Length) throw Error("Morph targets require a matching mesh node.");
            bindings.Add(new(first, node, positions, skin, jointSets.ToArray(), weightSets.ToArray(), targets.ToArray(), normals, normalTargets.ToArray()));
            float[]? elementIndices = null;
            if (p.TryGetProperty("indices", out var index))
            {
                elementIndices = Access(index.GetInt32(), "SCALAR", out var indexComponent);
                if (indexComponent is not (5121 or 5123 or 5125) || IsNormalized(index.GetInt32())) throw Error("Invalid triangle index accessor.");
            }
            var elementCount = elementIndices?.Length ?? count;
            if (elementCount < 3 || (primitiveMode == 4 && elementCount % 3 != 0)) throw Error("Invalid triangle primitive size.");
            for (var i = 0; i < elementCount; i++)
            {
                var indexValue = elementIndices is null ? i : elementIndices[i];
                if (indexValue < 0 || indexValue >= count) throw Error("Triangle index is outside its vertex buffer.");
            }
            if (primitiveMode == 4)
                for (var i = 0; i < elementCount; i += 3) Triangle(i, i + 1, i + 2);
            else if (primitiveMode == 5)
                for (var i = 0; i < elementCount - 2; i++) Triangle(i + (i & 1), i + 1 - (i & 1), i + 2);
            else
                for (var i = 1; i < elementCount - 1; i++) Triangle(0, i, i + 1);
            CompactPrimitive(first, count, firstIndex);

            void Triangle(int a, int b, int c)
            {
                var ia = elementIndices is null ? a : (int)elementIndices[a];
                var ib = elementIndices is null ? b : (int)elementIndices[b];
                var ic = elementIndices is null ? c : (int)elementIndices[c];
                // Strips commonly repeat indices to connect otherwise separate triangle runs.
                if (ia == ib || ia == ic || ib == ic) return;
                if (indices.Count + 3 > MaxVertices) throw Error("Use at most 20,000 triangles.");
                indices.Add(first + ia); indices.Add(first + ib); indices.Add(first + ic);
                faceColors.Add(materials[material].BaseColorFactor); faceMaterials.Add(material);
            }
        }
    }

    private void CompactPrimitive(int first, int count, int firstIndex)
    {
        // glTF indices need not reference every attribute accessor element.
        // Unused vertices must not enlarge normalization/ground bounds or add
        // unrelated skin joints to in-place root detection. Keep source order
        // so already compact primitives and their animation buffers stay stable.
        var used = new bool[count];
        for (var i = firstIndex; i < indices.Count; i++) used[indices[i] - first] = true;
        var kept = 0; foreach (var value in used) if (value) kept++;
        if (first + kept > MaxVertices) throw Error("Use at most 60,000 vertices.");
        if (kept == count) return;
        var sources = new int[kept]; var remap = new int[count]; var next = 0;
        for (var i = 0; i < count; i++)
            if (used[i]) { sources[next] = i; remap[i] = next++; }
        for (var i = 0; i < kept; i++)
        {
            vertices[first + i] = vertices[first + sources[i]];
            uv[first + i] = uv[first + sources[i]];
            vertexColors[first + i] = vertexColors[first + sources[i]];
        }
        vertices.RemoveRange(first + kept, count - kept);
        uv.RemoveRange(first + kept, count - kept);
        vertexColors.RemoveRange(first + kept, count - kept);
        for (var i = firstIndex; i < indices.Count; i++) indices[i] = first + remap[indices[i] - first];
        if (kept == 0) { bindings.RemoveAt(bindings.Count - 1); return; }
        var binding = bindings[^1];
        bindings[^1] = binding with
        {
            Positions = Select(binding.Positions),
            Normals = Select(binding.Normals),
            Joints = binding.Joints.Select(j => Select(j, 4)).ToArray(),
            Weights = binding.Weights.Select(w => Select(w)).ToArray(),
            MorphTargets = binding.MorphTargets.Select(t => Select(t)).ToArray(),
            MorphNormals = binding.MorphNormals.Select(n => Select(n)).ToArray()
        };

        T[] Select<T>(T[] values, int width = 1)
        {
            if (values.Length == 0) return values;
            var result = new T[kept * width];
            for (var i = 0; i < kept; i++) Array.Copy(values, sources[i] * width, result, i * width, width);
            return result;
        }
    }

    private (GuideAnimationClip[], GuideMeshChannel[][]) ReadAnimations()
    {
        if (!root.TryGetProperty("animations", out var source)) return ([], []);
        if (source.GetArrayLength() > 128) throw Error("Use at most 128 animation clips.");
        var clips = new List<GuideAnimationClip>(); var allChannels = new List<GuideMeshChannel[]>(); var channelCount = 0;
        foreach (var animation in source.EnumerateArray())
        {
            var channels = new List<GuideMeshChannel>(); var targets = new HashSet<(int, GuideAnimationPath)>(); var duration = 0f;
            var samplers = animation.GetProperty("samplers");
            foreach (var channel in animation.GetProperty("channels").EnumerateArray())
            {
                if (++channelCount > 8192) throw Error("Too many animation channels.");
                var target = channel.GetProperty("target"); if (!target.TryGetProperty("node", out var nodeId)) continue;
                var node = Index(nodeId.GetInt32(), nodes.Length);
                var path = target.GetProperty("path").GetString() switch
                {
                    "translation" => GuideAnimationPath.Translation,
                    "rotation" => GuideAnimationPath.Rotation,
                    "scale" => GuideAnimationPath.Scale,
                    "weights" => GuideAnimationPath.Weights,
                    _ => throw Error("Unsupported animation target.")
                };
                if (!targets.Add((node, path))) throw Error("An animation repeats the same node property.");
                if (path != GuideAnimationPath.Weights && nodes[node].Matrix is not null) throw Error("An animated node must use TRS transforms.");
                var sampler = samplers[Index(Number(channel, "sampler"), samplers.GetArrayLength())];
                var interpolation = (sampler.TryGetProperty("interpolation", out var mode) ? mode.GetString() : "LINEAR") switch
                {
                    "LINEAR" => GuideInterpolation.Linear,
                    "STEP" => GuideInterpolation.Step,
                    "CUBICSPLINE" => GuideInterpolation.CubicSpline,
                    _ => throw Error("Unsupported animation interpolation.")
                };
                var times = Access(Number(sampler, "input"), "SCALAR", out var timeComponent);
                if (timeComponent != 5126 || times.Length == 0 || times[0] < 0 || (interpolation == GuideInterpolation.CubicSpline && times.Length < 2)) throw Error("Invalid animation timestamps.");
                for (var i = 1; i < times.Length; i++) if (times[i] <= times[i - 1]) throw Error("Animation timestamps must increase.");
                var width = path switch { GuideAnimationPath.Rotation => 4, GuideAnimationPath.Weights => nodes[node].Weights.Length, _ => 3 };
                if (width == 0) throw Error("Weight animation needs morph targets.");
                var values = Access(Number(sampler, "output"), path == GuideAnimationPath.Weights ? "SCALAR" : path == GuideAnimationPath.Rotation ? "VEC4" : "VEC3", out var valueComponent);
                if ((path is GuideAnimationPath.Translation or GuideAnimationPath.Scale) && valueComponent != 5126) throw Error("Translation and scale animations require float values.");
                if (valueComponent != 5126 && !IsNormalized(Number(sampler, "output"))) throw Error("Integer animation values must be normalized.");
                var factor = interpolation == GuideInterpolation.CubicSpline ? 3 : 1;
                if (values.Length != times.Length * width * factor) throw Error("Animation outputs must match their timestamps.");
                if (path == GuideAnimationPath.Rotation)
                    for (var i = 0; i < times.Length; i++)
                    {
                        var at = i * width * factor + (factor == 3 ? 4 : 0); var q = UnitQuaternion(Q(values, at));
                        values[at] = q.X; values[at + 1] = q.Y; values[at + 2] = q.Z; values[at + 3] = q.W;
                    }
                channels.Add(new(node, path, interpolation, times, values, width)); duration = Math.Max(duration, times[^1]);
            }
            if (channels.Count == 0) continue;
            var name = animation.TryGetProperty("name", out var named) ? named.GetString()?.Trim() : null;
            clips.Add(new(string.IsNullOrEmpty(name) ? $"Animation {clips.Count + 1}" : name, duration)); allChannels.Add(channels.ToArray());
        }
        return (clips.ToArray(), allChannels.ToArray());
    }

    private void ReadMaterials()
    {
        long decodedPixels = 0, imageBytes = 0;
        if (root.TryGetProperty("images", out var sourceImages) && sourceImages.GetArrayLength() > 256)
            throw Error("Use at most 256 model images.");
        var loadedImages = new Dictionary<int, int>();
        int ReadImage(int id)
        {
            if (loadedImages.TryGetValue(id, out var loaded)) return loaded;
            var image = At("images", id);
            string mime; byte[] bytes;
            if (image.TryGetProperty("bufferView", out var view))
            {
                if (image.TryGetProperty("uri", out _)) throw Error("A model image cannot have both a URI and a buffer view.");
                mime = image.GetProperty("mimeType").GetString() ?? ""; var (buffer, start, length, _) = View(view.GetInt32());
                if (length > 16 * 1024 * 1024) throw Error("Each model texture must be at most 16 MB.");
                if (length > GuideModelResources.MaxBytes - imageBytes) throw Error("Model texture data must total at most 32 MB.");
                bytes = buffer.AsSpan(start, length).ToArray();
            }
            else
            {
                (bytes, mime) = resources.ReadUri(image.GetProperty("uri").GetString() ?? "", true);
                if (image.TryGetProperty("mimeType", out var declared) && declared.GetString() != mime)
                    throw Error("The model image MIME type does not match its resource.");
            }
            if (mime is not ("image/png" or "image/jpeg")) throw Error("Use PNG or JPEG model textures.");
            if (bytes.Length > 16 * 1024 * 1024) throw Error("Each model texture must be at most 16 MB.");
            if ((imageBytes += bytes.Length) > GuideModelResources.MaxBytes) throw Error("Model texture data must total at most 32 MB.");
            var (width, height) = GuideImageBounds.Read(mime, bytes);
            if (width > 4096 || height > 4096 || (decodedPixels += (long)width * height) > 32_000_000)
                throw Error("Resize model textures to at most 4,096 pixels per side and 32 megapixels combined.");
            loaded = images.Count; images.Add(new(mime, bytes, width, height)); loadedImages.Add(id, loaded);
            return loaded;
        }
        if (root.TryGetProperty("materials", out var source))
        {
            if (source.GetArrayLength() > 1024) throw Error("Too many materials.");
            foreach (var material in source.EnumerateArray())
            {
                var color = Vector4.One; var image = -1; var texCoord = 0; var offset = Vector2.Zero; var scale = Vector2.One; var rotation = 0f; var wrapU = 10497; var wrapV = 10497;
                if (material.TryGetProperty("pbrMetallicRoughness", out var pbr))
                {
                    if (pbr.TryGetProperty("baseColorFactor", out var factor)) color = V4(Floats(factor, 4), 0);
                    if (color.X < 0 || color.X > 1 || color.Y < 0 || color.Y > 1 || color.Z < 0 || color.Z > 1 || color.W < 0 || color.W > 1) throw Error("Invalid material color.");
                    if (pbr.TryGetProperty("baseColorTexture", out var info))
                    {
                        var texture = At("textures", Number(info, "index"));
                        // An optional compressed texture may supply a regular PNG/JPEG source.
                        // Load that chosen image only; unused KTX/WebP or normal-map resources
                        // must not reject an otherwise supported base-color material.
                        image = ReadImage(Number(texture, "source", -1)); texCoord = Number(info, "texCoord");
                        if (texture.TryGetProperty("sampler", out var samplerId))
                        {
                            var sampler = At("samplers", samplerId.GetInt32()); wrapU = Number(sampler, "wrapS", 10497); wrapV = Number(sampler, "wrapT", 10497);
                            if (wrapU is not (33071 or 33648 or 10497) || wrapV is not (33071 or 33648 or 10497)) throw Error("Invalid texture wrapping.");
                        }
                        if (info.TryGetProperty("extensions", out var extensions) && extensions.TryGetProperty("KHR_texture_transform", out var transform))
                        {
                            if (transform.TryGetProperty("offset", out var o)) { var values = Floats(o, 2); offset = new(values[0], values[1]); }
                            if (transform.TryGetProperty("scale", out var s)) { var values = Floats(s, 2); scale = new(values[0], values[1]); }
                            if (transform.TryGetProperty("rotation", out var r)) rotation = Finite(r.GetSingle());
                            texCoord = Number(transform, "texCoord", texCoord);
                        }
                        if (texCoord < 0 || texCoord > 7) throw Error("Use texture coordinate sets 0 through 7.");
                    }
                }
                var alpha = material.TryGetProperty("alphaMode", out var mode) ? mode.GetString() ?? "OPAQUE" : "OPAQUE";
                if (alpha is not ("OPAQUE" or "MASK" or "BLEND")) throw Error("Invalid material alpha mode.");
                var cutoff = material.TryGetProperty("alphaCutoff", out var a) ? Finite(a.GetSingle()) : .5f;
                if (cutoff < 0) throw Error("Invalid alpha cutoff.");
                var unlit = material.TryGetProperty("extensions", out var exts) && exts.TryGetProperty("KHR_materials_unlit", out _);
                materials.Add(new(color, image, material.TryGetProperty("doubleSided", out var d) && d.GetBoolean(), alpha, cutoff, wrapU, wrapV, unlit));
                textureTransforms.Add((texCoord, offset, scale, rotation));
            }
        }
        materials.Add(new(Vector4.One)); textureTransforms.Add((0, Vector2.Zero, Vector2.One, 0));
    }

    private float[] Access(int id, string type, out int component)
    {
        var accessor = At("accessors", id);
        if (accessor.GetProperty("type").GetString() != type) throw Error("Incompatible model accessor.");
        component = Number(accessor, "componentType"); var width = ComponentSize(component);
        var elements = type switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT4" => 16, _ => throw Error("Unsupported model accessor.") };
        if (type == "MAT4" && component != 5126) throw Error("Matrices must use float components.");
        var count = Number(accessor, "count");
        if (count <= 0 || count > 1_000_000 || (decodedValues += (long)count * elements) > 16_000_000) throw Error("Too much decoded model data.");
        var normalized = accessor.TryGetProperty("normalized", out var n) && n.GetBoolean();
        if (normalized && component is 5125 or 5126) throw Error("Invalid normalized accessor.");
        var result = new float[count * elements]; var elementSize = width * elements;
        if (accessor.TryGetProperty("bufferView", out var viewId))
        {
            var (buffer, start, length, stride) = View(viewId.GetInt32()); stride = stride == 0 ? elementSize : stride;
            var offset = Number(accessor, "byteOffset");
            if (offset < 0 || offset % width != 0 || stride < elementSize || stride % width != 0 || (long)offset + (long)(count - 1) * stride + elementSize > length) throw Error("Invalid accessor buffer bounds.");
            for (var i = 0; i < count; i++) for (var e = 0; e < elements; e++) result[i * elements + e] = Value(buffer, start + offset + i * stride + e * width, component, normalized);
        }
        else if (Number(accessor, "byteOffset") != 0) throw Error("An accessor without a buffer view cannot have an offset.");
        if (accessor.TryGetProperty("sparse", out var sparse))
        {
            var sparseCount = Number(sparse, "count"); if (sparseCount <= 0 || sparseCount > count) throw Error("Invalid sparse accessor size.");
            var sparseIndices = sparse.GetProperty("indices"); var indexComponent = Number(sparseIndices, "componentType");
            if (indexComponent is not (5121 or 5123 or 5125)) throw Error("Invalid sparse index component.");
            var (indexBuffer, indexStart, indexLength, indexStride) = View(Number(sparseIndices, "bufferView"));
            var indexWidth = ComponentSize(indexComponent); var indexOffset = Number(sparseIndices, "byteOffset");
            var values = sparse.GetProperty("values"); var (valueBuffer, valueStart, valueLength, valueStride) = View(Number(values, "bufferView")); var valueOffset = Number(values, "byteOffset");
            if (indexStride != 0 || valueStride != 0 || indexOffset < 0 || valueOffset < 0 || indexOffset % indexWidth != 0 || valueOffset % width != 0 || (long)indexOffset + (long)sparseCount * indexWidth > indexLength || (long)valueOffset + (long)sparseCount * elementSize > valueLength)
                throw Error("Invalid sparse accessor bounds.");
            var previous = -1;
            for (var i = 0; i < sparseCount; i++)
            {
                var indexValue = Value(indexBuffer, indexStart + indexOffset + i * indexWidth, indexComponent, false);
                if (indexValue <= previous || indexValue >= count) throw Error("Sparse indices must increase and stay inside the accessor.");
                var at = (int)indexValue; previous = at;
                for (var e = 0; e < elements; e++) result[at * elements + e] = Value(valueBuffer, valueStart + valueOffset + i * elementSize + e * width, component, normalized);
            }
        }
        return result;
    }

    private (byte[] Buffer, int Start, int Length, int Stride) View(int id)
    {
        var view = At("bufferViews", id); var start = Number(view, "byteOffset"); var length = Number(view, "byteLength"); var stride = Number(view, "byteStride");
        var buffer = Index(Number(view, "buffer"), buffers.Length);
        if (start < 0 || length < 0 || (long)start + length > bufferLengths[buffer] || stride < 0 || stride > 252 || stride % 4 != 0) throw Error("Invalid model buffer view.");
        if (Meshopt(view, out var extension, out var khr))
        {
            if (!meshoptViews.TryGetValue(id, out var decoded))
            {
                var sourceBuffer = Index(extension.GetProperty("buffer").GetInt32(), buffers.Length);
                var sourceStart = Number(extension, "byteOffset"); var sourceLength = Number(extension, "byteLength");
                var count = Number(extension, "count"); var decodedStride = Number(extension, "byteStride");
                if (sourceStart < 0 || sourceLength <= 0 || (long)sourceStart + sourceLength > bufferLengths[sourceBuffer]
                    || buffers[sourceBuffer].Length < bufferLengths[sourceBuffer]
                    || count <= 0 || decodedStride <= 0 || (long)count * decodedStride != length
                    || stride != 0 && stride != decodedStride)
                    throw Error("Invalid meshopt buffer view bounds or stride.");
                if (meshoptDecodedBytes + length > GuideModelResources.MaxBytes)
                    throw Error("Decoded meshopt views must total at most 32 MB.");
                decoded = GuideMeshoptDecoder.Decode(buffers[sourceBuffer].AsSpan(sourceStart, sourceLength), count, decodedStride,
                    extension.GetProperty("mode").GetString() ?? "", extension.TryGetProperty("filter", out var filter) ? filter.GetString() ?? "" : "NONE", khr);
                meshoptDecodedBytes += length; meshoptViews.Add(id, decoded);
            }
            return (decoded, 0, length, stride);
        }
        if (buffers[buffer].Length < bufferLengths[buffer]) throw Error("An ordinary view cannot read a meshopt fallback buffer.");
        return (buffers[buffer], start, length, stride);
    }
    private static bool Meshopt(JsonElement value, out JsonElement extension, out bool khr)
    {
        extension = default; khr = false;
        if (!value.TryGetProperty("extensions", out var extensions)) return false;
        var ext = extensions.TryGetProperty("EXT_meshopt_compression", out var legacy);
        khr = extensions.TryGetProperty("KHR_meshopt_compression", out var current);
        if (ext && khr) throw Error("A buffer or view cannot declare both meshopt extensions.");
        extension = khr ? current : legacy;
        return ext || khr;
    }
    private static float Value(byte[] bin, int offset, int component, bool normalized)
        => Finite(component switch
        {
            5120 => normalized ? Math.Max((sbyte)bin[offset] / 127f, -1) : (sbyte)bin[offset],
            5121 => normalized ? bin[offset] / 255f : bin[offset],
            5122 => normalized ? Math.Max(BitConverter.ToInt16(bin, offset) / 32767f, -1) : BitConverter.ToInt16(bin, offset),
            5123 => normalized ? BitConverter.ToUInt16(bin, offset) / 65535f : BitConverter.ToUInt16(bin, offset),
            5125 => BitConverter.ToUInt32(bin, offset),
            5126 => BitConverter.ToSingle(bin, offset),
            _ => throw Error("Unsupported model component.")
        });
    private bool IsNormalized(int id) => At("accessors", id).TryGetProperty("normalized", out var n) && n.GetBoolean();
    private bool NormalComponent(int id, int component) => component == 5126
        || quantized && component is 5120 or 5122 && IsNormalized(id);
    private JsonElement At(string array, int id) { var a = root.GetProperty(array); return a[Index(id, a.GetArrayLength())]; }
    private static int Number(JsonElement el, string name, int fallback = 0) => el.TryGetProperty(name, out var n) ? n.GetInt32() : fallback;
    private static int Index(int id, int count) => id >= 0 && id < count ? id : throw Error("Model reference is out of bounds.");
    private static int TargetCount(JsonElement primitive) => primitive.TryGetProperty("targets", out var targets) ? targets.GetArrayLength() : 0;
    private static int ComponentSize(int component) => component switch { 5120 or 5121 => 1, 5122 or 5123 => 2, 5125 or 5126 => 4, _ => throw Error("Unsupported model component.") };
    private static float Finite(float value) => float.IsFinite(value) ? value : throw Error("The model contains nonfinite values.");
    private static float[] Floats(JsonElement array, int length)
    {
        if (array.GetArrayLength() != length) throw Error("Invalid model vector size.");
        return array.EnumerateArray().Select(v => Finite(v.GetSingle())).ToArray();
    }
    private static Quaternion UnitQuaternion(Quaternion q) => q.LengthSquared() > 1e-12f && float.IsFinite(q.LengthSquared()) ? Quaternion.Normalize(q) : throw Error("The model contains an invalid rotation.");
    private static Vector3 V3(float[] values, int at) => new(values[at], values[at + 1], values[at + 2]);
    private static Vector4 V4(float[] values, int at) => new(values[at], values[at + 1], values[at + 2], values[at + 3]);
    private static Quaternion Q(float[] values, int at) => new(values[at], values[at + 1], values[at + 2], values[at + 3]);
    private static Matrix4x4 Matrix(ReadOnlySpan<float> m) => new(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
    private static InvalidDataException Error(string message) => new(message);
}
