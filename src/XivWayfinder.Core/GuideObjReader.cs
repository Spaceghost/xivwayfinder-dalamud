using System.Globalization;
using System.Numerics;
using System.Text;

namespace XivWayfinder.Core;

/// <summary>OBJ triangle geometry and its local diffuse MTL appearance.</summary>
internal sealed class GuideObjReader(string path)
{
    private sealed record Material(Vector4 Color, string? Image = null, Vector2 Scale = default,
        Vector2 Offset = default, bool Clamp = false, bool Unlit = false);
    private const long MaxBytes = 32 * 1024 * 1024;
    private readonly string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
    private readonly Dictionary<string, Material> sourceMaterials = new(StringComparer.Ordinal);
    private readonly HashSet<string> charged = new(StringComparer.Ordinal);
    private readonly HashSet<string> libraries = new(StringComparer.Ordinal);
    private long byteCount;

    public static GuideMesh Load(string path)
    {
        try { return new GuideObjReader(path).Read(); }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException or IndexOutOfRangeException)
        { throw new InvalidDataException("The OBJ contains invalid geometry or material data.", ex); }
    }

    private GuideMesh Read()
    {
        Charge(path);
        var positions = new List<Vector3>(); var colors = new List<Vector4>(); var coordinates = new List<Vector2>(); var normals = new List<Vector3>();
        var corners = new List<(int Position, int Uv, int Normal, string Material, string Smoothing)>();
        var selectedMaterial = ""; var smoothing = "";
        foreach (var tokens in Lines(path))
        {
            switch (tokens[0])
            {
                case "v":
                    if (tokens.Count < 4 || positions.Count >= GuideMesh.MaxTriangles * 3) throw Error("Use at most 60,000 OBJ vertices.");
                    positions.Add(new(Number(tokens[1]), Number(tokens[2]), Number(tokens[3])));
                    colors.Add(tokens.Count >= 7 ? new(Color(tokens[4]), Color(tokens[5]), Color(tokens[6]), tokens.Count >= 8 ? Color(tokens[7]) : 1) : Vector4.One);
                    break;
                case "vt":
                    if (tokens.Count < 2 || coordinates.Count >= GuideMesh.MaxTriangles * 3) throw Error("Too many or invalid OBJ texture coordinates.");
                    coordinates.Add(new(Number(tokens[1]), tokens.Count >= 3 ? Number(tokens[2]) : 0));
                    break;
                case "vn":
                    if (tokens.Count < 4 || normals.Count >= GuideMesh.MaxTriangles * 3) throw Error("Too many or invalid OBJ normals.");
                    normals.Add(GuideNormals.Normalize(new(Number(tokens[1]), Number(tokens[2]), Number(tokens[3]))));
                    break;
                case "s": smoothing = tokens.Count < 2 || tokens[1] is "off" or "0" ? "" : tokens[1]; break;
                case "mtllib":
                    for (var i = 1; i < tokens.Count; i++) ReadMaterials(LocalFile(directory, tokens[i]));
                    break;
                case "usemtl": selectedMaterial = string.Join(' ', tokens.Skip(1)); break;
                case "f":
                    if (tokens.Count < 4) throw Error("OBJ faces need at least three vertices.");
                    var a = Corner(tokens[1]);
                    for (var i = 2; i < tokens.Count - 1; i++)
                    {
                        if (corners.Count >= GuideMesh.MaxTriangles * 3) throw Error("Use a model with at most 20,000 triangles.");
                        corners.Add(a); corners.Add(Corner(tokens[i])); corners.Add(Corner(tokens[i + 1]));
                    }
                    break;
            }
        }
        (int, int, int, string, string) Corner(string value)
        {
            var fields = value.Split('/');
            var vertex = Index(fields[0], positions.Count);
            var uv = fields.Length > 1 && fields[1].Length > 0 ? Index(fields[1], coordinates.Count) : -1;
            var normal = fields.Length > 2 && fields[2].Length > 0 ? Index(fields[2], normals.Count) : -1;
            if (fields.Length > 3) throw Error("Invalid OBJ face corner.");
            return (vertex, uv, normal, selectedMaterial, smoothing);
        }
        var imageLookup = new Dictionary<string, int>(StringComparer.Ordinal); var images = new List<GuideMeshImage>(); long pixels = 0;
        var materialLookup = new Dictionary<string, int>(StringComparer.Ordinal); var materials = new List<GuideMeshMaterial>();
        foreach (var name in corners.Select(c => c.Material).Distinct(StringComparer.Ordinal))
        {
            var source = name.Length == 0 ? new Material(Vector4.One, Scale:Vector2.One)
                : sourceMaterials.TryGetValue(name, out var found) ? found : throw Error($"OBJ material '{name}' was not found. Include its MTL file.");
            var image = -1;
            if (source.Image is { } filename && !imageLookup.TryGetValue(filename, out image))
            {
                if (images.Count >= 256) throw Error("Use at most 256 guide textures.");
                Charge(filename);
                var bytes = File.ReadAllBytes(filename);
                if (bytes.Length > 16 * 1024 * 1024) throw Error("Each guide texture must be at most 16 MB.");
                var mime = Path.GetExtension(filename).ToLowerInvariant() switch { ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", _ => throw Error("Use PNG or JPEG diffuse textures.") };
                var (width, height) = GuideImageBounds.Read(mime, bytes);
                if (width > 4096 || height > 4096 || (pixels += (long)width * height) > 32_000_000) throw Error("Resize textures to at most 4,096 pixels per side and 32 megapixels combined.");
                image = images.Count; imageLookup[filename] = image; images.Add(new(mime, bytes, width, height));
            }
            materialLookup[name] = materials.Count;
            materials.Add(new(source.Color, image, false, source.Color.W < 1 || image >= 0 ? "BLEND" : "OPAQUE", .5f,
                source.Clamp ? 33071 : 10497, source.Clamp ? 33071 : 10497, source.Unlit));
        }
        var vertices = new List<Vector3>(); var vertexColors = new List<Vector4>(); var uvValues = new List<Vector2>(); var vertexNormals = new List<Vector3>();
        var indices = new int[corners.Count]; var faceMaterials = new int[corners.Count / 3]; var faceColors = new Vector4[faceMaterials.Length];
        var merged = new Dictionary<(int, int, int, int, string), int>();
        var smoothNormals = new Dictionary<(int, string), Vector3>();
        for (var i = 0; i < corners.Count; i += 3)
        {
            var a = positions[corners[i].Position]; var b = positions[corners[i + 1].Position]; var c = positions[corners[i + 2].Position];
            var face = Vector3.Cross(b - a, c - a);
            for (var k = i; k < i + 3; k++)
            {
                var corner = corners[k]; if (corner.Smoothing.Length == 0) continue;
                var key = (corner.Position, corner.Smoothing);
                smoothNormals[key] = smoothNormals.GetValueOrDefault(key) + face;
            }
        }
        for (var i = 0; i < corners.Count; i++)
        {
            var corner = corners[i]; var mat = materialLookup[corner.Material];
            var key = (corner.Position, corner.Uv, corner.Normal, mat, corner.Normal >= 0 ? "" : corner.Smoothing);
            if (!merged.TryGetValue(key, out var vertex))
            {
                vertex = vertices.Count; merged[key] = vertex; vertices.Add(positions[corner.Position]); vertexColors.Add(colors[corner.Position]);
                vertexNormals.Add(corner.Normal >= 0 ? normals[corner.Normal] : corner.Smoothing.Length > 0
                    ? GuideNormals.Normalize(smoothNormals.GetValueOrDefault((corner.Position, corner.Smoothing))) : Vector3.Zero);
                var source = corner.Material.Length == 0 ? new Material(Vector4.One, Scale:Vector2.One) : sourceMaterials[corner.Material];
                if (materials[mat].BaseColorImage >= 0 && corner.Uv < 0) throw Error("Textured OBJ faces need texture coordinates.");
                var uv = corner.Uv >= 0 ? coordinates[corner.Uv] * source.Scale + source.Offset : Vector2.Zero;
                if (!float.IsFinite(uv.X) || !float.IsFinite(uv.Y)) throw Error("The OBJ texture transform produces invalid coordinates.");
                // OBJ stores V from the bottom of the image; the renderer and GLB use image-top V.
                uvValues.Add(new(uv.X, 1 - uv.Y));
            }
            indices[i] = vertex;
            if (i % 3 == 0) { faceMaterials[i / 3] = mat; faceColors[i / 3] = materials[mat].BaseColorFactor; }
        }
        return new(vertices.ToArray(), indices, faceColors)
        { TextureCoordinates = uvValues.ToArray(), VertexColors = vertexColors.ToArray(), FaceMaterials = faceMaterials, Materials = materials.ToArray(), Images = images.ToArray(), Normals = vertexNormals.ToArray() };
    }

    private void ReadMaterials(string filename)
    {
        if (!libraries.Add(filename)) return;
        if (libraries.Count > 64 || Charge(filename) > 1024 * 1024) throw Error("Use smaller OBJ material libraries.");
        string? name = null;
        foreach (var t in Lines(filename))
        {
            if (t[0] == "newmtl")
            {
                name = string.Join(' ', t.Skip(1));
                if (name.Length == 0 || sourceMaterials.Count >= 1024) throw Error("Invalid or excessive OBJ materials.");
                sourceMaterials[name] = new(Vector4.One, Scale:Vector2.One); continue;
            }
            if (name is null) continue;
            var material = sourceMaterials[name];
            switch (t[0])
            {
                case "Kd":
                    if (t.Count < 4) throw Error("Invalid MTL diffuse color.");
                    material = material with { Color = new(Color(t[1]), Color(t[2]), Color(t[3]), material.Color.W) }; break;
                case "d":
                case "Tr":
                    var at = t.Count > 1 && t[1] == "-halo" ? 2 : 1;
                    if (at >= t.Count) throw Error("Invalid MTL opacity.");
                    material = material with { Color = material.Color with { W = t[0] == "Tr" ? 1 - Color(t[at]) : Color(t[at]) } }; break;
                case "illum": material = material with { Unlit = t.Count > 1 && t[1] == "0" }; break;
                case "map_Kd":
                    var scale = Vector2.One; var offset = Vector2.Zero; var clamp = false; var i = 1;
                    while (i < t.Count && t[i].StartsWith('-'))
                    {
                        var option = t[i++];
                        if (option is "-s" or "-o")
                        {
                            var values = option == "-s" ? Vector3.One : Vector3.Zero; var count = 0;
                            while (i < t.Count && count < 3 && float.TryParse(t[i], NumberStyles.Float, CultureInfo.InvariantCulture, out _)) values[count++] = Number(t[i++]);
                            if (count == 0) throw Error("Invalid MTL texture transform.");
                            if (option == "-s") scale = new(values.X, values.Y); else offset = new(values.X, values.Y);
                        }
                        else if (option == "-clamp" && i < t.Count)
                        { clamp = t[i++] switch { "on" => true, "off" => false, _ => throw Error("Invalid MTL clamp option.") }; }
                        else throw Error($"Unsupported MTL texture option '{option}'. Export a diffuse map with scale, offset or clamp.");
                    }
                    if (i >= t.Count) throw Error("MTL diffuse texture needs a filename.");
                    material = material with { Image = LocalFile(Path.GetDirectoryName(filename)!, string.Join(' ', t.Skip(i))), Scale = scale, Offset = offset, Clamp = clamp }; break;
            }
            sourceMaterials[name] = material;
        }
    }

    private string LocalFile(string relativeTo, string reference)
    {
        reference = reference.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(reference)) throw Error("Keep OBJ material files and textures inside the model's folder.");
        var resolved = Path.GetFullPath(Path.Combine(relativeTo, reference));
        EnsureInside(resolved);
        // Resolve every symlink component so a sidecar cannot escape the selected model's folder.
        var rootInfo = new DirectoryInfo(directory); var physicalRoot = rootInfo.ResolveLinkTarget(true)?.FullName ?? rootInfo.FullName;
        var physical = physicalRoot;
        foreach (var piece in Path.GetRelativePath(directory, resolved).Split(Path.DirectorySeparatorChar))
        {
            physical = Path.Combine(physical, piece);
            FileSystemInfo info = Directory.Exists(physical) ? new DirectoryInfo(physical) : new FileInfo(physical);
            if (info.LinkTarget is not null) physical = info.ResolveLinkTarget(true)?.FullName ?? throw Error("Cannot resolve an OBJ asset link.");
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!physical.StartsWith(Path.TrimEndingDirectorySeparator(physicalRoot) + Path.DirectorySeparatorChar, comparison)) throw Error("OBJ asset links must stay inside the model's folder.");
        }
        return resolved;

        void EnsureInside(string file)
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!file.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar, comparison)) throw Error("Keep OBJ material files and textures inside the model's folder.");
        }
    }
    private long Charge(string filename)
    {
        // FileInfo.Length can report a symlink's own size. Stream.Length measures the bytes we will read.
        using var source = File.OpenRead(filename);
        if (charged.Add(Path.GetFullPath(filename)) && (byteCount += source.Length) > MaxBytes)
            throw Error("The OBJ, material files and textures must total at most 32 MB.");
        return source.Length;
    }
    private static IEnumerable<List<string>> Lines(string filename)
    {
        foreach (var line in File.ReadLines(filename))
        {
            if (line.Length > 131072) throw Error("An OBJ or MTL line is too long.");
            var tokens = new List<string>(); var value = new StringBuilder(); var quote = '\0';
            foreach (var c in line)
            {
                if (quote != '\0') { if (c == quote) quote = '\0'; else value.Append(c); }
                else if (value.Length == 0 && c is '\'' or '"') quote = c;
                else if (c == '#') break;
                else if (char.IsWhiteSpace(c)) { if (value.Length > 0) { tokens.Add(value.ToString()); value.Clear(); } }
                else value.Append(c);
            }
            if (quote != '\0') throw Error("An OBJ asset filename has an unmatched quote.");
            if (value.Length > 0) tokens.Add(value.ToString());
            if (tokens.Count > 0) yield return tokens;
        }
    }
    private static int Index(string text, int count)
    {
        var value = int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
        var index = value < 0 ? count + value : value - 1;
        return index >= 0 && index < count ? index : throw Error("Invalid OBJ face or texture index.");
    }
    private static float Number(string text) => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && float.IsFinite(value) ? value : throw Error("Invalid OBJ or MTL number.");
    private static float Color(string text) => Math.Clamp(Number(text), 0, 1);
    private static InvalidDataException Error(string message) => new(message);
}
