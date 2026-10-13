namespace XivWayfinder.Core;

/// <summary>Loads a selected model and its bounded local glTF resources. URIs never access the network.</summary>
internal sealed class GuideModelResources(string modelPath)
{
    internal const int MaxBytes = 32 * 1024 * 1024;
    private readonly string directory = Path.GetDirectoryName(Path.GetFullPath(modelPath))!;
    private readonly Dictionary<string, byte[]> files = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly Dictionary<string, (byte[] Data, string Mime)> embedded = new(StringComparer.Ordinal);
    private long fileBytes;
    private long embeddedBytes;

    public byte[] ReadModel() => ReadFile(Path.GetFullPath(modelPath));

    public (byte[] Data, string Mime) ReadUri(string uri, bool image)
    {
        if (uri.StartsWith("data:", StringComparison.Ordinal))
        {
            var comma = uri.IndexOf(',');
            if (comma < 0 || !uri.AsSpan(0, comma).EndsWith(";base64", StringComparison.Ordinal))
                throw Error("Embedded model resources must use base64 data URIs.");
            var mime = uri[5..(comma - 7)];
            if (image ? mime is not ("image/png" or "image/jpeg") : mime is not ("application/octet-stream" or "application/gltf-buffer"))
                throw Error("Use binary model buffers and PNG or JPEG textures.");
            if (embedded.TryGetValue(uri, out var cached)) return cached;
            // Bound the allocation before decoding. The JSON containing these strings is bounded too.
            if ((long)(uri.Length - comma - 1) * 3 / 4 > MaxBytes - embeddedBytes)
                throw Error("Embedded model resources exceed 32 MB.");
            var data = Convert.FromBase64String(uri[(comma + 1)..]);
            embeddedBytes += data.Length;
            embedded[uri] = (data, mime);
            return (data, mime);
        }

        // glTF relative resource URIs have no scheme, authority, query or fragment. Decode once,
        // before checking confinement, so percent-encoded separators cannot bypass the boundary.
        if (string.IsNullOrWhiteSpace(uri) || uri.IndexOfAny(['?', '#']) >= 0 || Uri.TryCreate(uri, UriKind.Absolute, out _))
            throw Error("Keep model buffers and textures inside the model's folder; remote URIs are not supported.");
        var relative = Uri.UnescapeDataString(uri).Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        if (relative.IndexOf('\0') >= 0 || relative.Contains(':') || Path.IsPathRooted(relative))
            throw Error("Model resource paths must be relative to the model's folder.");
        var resolved = Path.GetFullPath(Path.Combine(directory, relative));
        EnsureInside(directory, resolved);
        var physicalRoot = PhysicalPath(directory);
        var physical = physicalRoot;
        foreach (var piece in Path.GetRelativePath(directory, resolved).Split(Path.DirectorySeparatorChar))
        {
            physical = ResolveLink(Path.Combine(physical, piece));
            EnsureInside(physicalRoot, physical);
        }
        var bytes = ReadFile(physical);
        return (bytes, image ? ImageMime(bytes) : "application/octet-stream");
    }

    private byte[] ReadFile(string path)
    {
        if (files.TryGetValue(path, out var cached)) return cached;
        using var stream = File.OpenRead(path);
        if (stream.Length > MaxBytes - fileBytes)
            throw Error("The model, buffers and textures must total at most 32 MB.");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        fileBytes += bytes.Length;
        files.Add(path, bytes);
        return bytes;
    }

    private static string ImageMime(ReadOnlySpan<byte> bytes)
        => bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ? "image/png"
            : bytes.Length >= 2 && bytes[0] == 255 && bytes[1] == 216 ? "image/jpeg"
            : throw Error("Use PNG or JPEG model textures.");

    private static string PhysicalPath(string path)
    {
        var physical = Path.GetPathRoot(path)!;
        foreach (var piece in path[physical.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            physical = ResolveLink(Path.Combine(physical, piece));
        return physical;
    }

    private static string ResolveLink(string path)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        return info.LinkTarget is null ? path : info.ResolveLinkTarget(true)?.FullName ?? throw Error("Cannot resolve a model resource link.");
    }

    private static void EnsureInside(string root, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, comparison))
            throw Error("Model resource files and links must stay inside the model's folder.");
    }

    private static InvalidDataException Error(string message) => new(message);
}
