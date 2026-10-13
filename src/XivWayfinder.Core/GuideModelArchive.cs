using System.IO.Compression;

namespace XivWayfinder.Core;

public sealed record GuideArchiveModel(string RelativePath, string FullPath);

/// <summary>A local downloaded model pack extracted into an owned preview folder.
/// Selecting a guide retains the folder for its saved ordinary model path;
/// uncommitted folders are removed after the owner retires any model readers.</summary>
public sealed class GuideModelArchive : IDisposable
{
    public const long MaximumBytes = 32 * 1024 * 1024;
    public const int MaximumEntries = 512;
    private bool retained, disposed;
    public string SourcePath { get; }
    public string DirectoryPath { get; }
    public IReadOnlyList<GuideArchiveModel> Models { get; }

    private GuideModelArchive(string source, string directory, GuideArchiveModel[] models)
    { SourcePath = source; DirectoryPath = directory; Models = Array.AsReadOnly(models); }

    public static Task<GuideModelArchive> OpenAsync(string path, string cacheDirectory,
        CancellationToken cancellationToken = default) => Task.Run(() => Extract(path, cacheDirectory, cancellationToken), cancellationToken);

    /// <summary>Resolve a pasted alias to the pack's actual stored model path.
    /// Ownership must use the same separator, relative-path and case rules as
    /// the filesystem, before deciding whether its preview folder can retire.</summary>
    public bool TryFindModel(string path, out GuideArchiveModel model)
    {
        model = null!;
        if (disposed || string.IsNullOrWhiteSpace(path)) return false;
        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var candidate in Models)
            if (candidate.FullPath.Equals(fullPath, comparison)) { model = candidate; return true; }
        return false;
    }

    public void Retain()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        retained = true;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (!retained) DeleteOwnedDirectory(DirectoryPath);
    }

    private static GuideModelArchive Extract(string path, string cacheDirectory, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        path = Path.GetFullPath(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MaximumBytes) throw Error("Choose a model pack smaller than 32 MB.");
        using var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        if (zip.Entries.Count > MaximumEntries) throw Error("A model pack can contain at most 512 files and folders.");
        var entries = new List<(ZipArchiveEntry Entry, string Name, bool Directory)>();
        var names = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in zip.Entries)
        {
            cancel.ThrowIfCancellationRequested();
            var attributes = unchecked((uint)entry.ExternalAttributes);
            var unixType = (attributes >> 16) & 0xf000;
            if (unixType is not (0 or 0x8000 or 0x4000) || (attributes & (uint)FileAttributes.ReparsePoint) != 0)
                throw Error("Model packs cannot contain links or special files.");
            var directory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')
                || unixType == 0x4000 || (attributes & (uint)FileAttributes.Directory) != 0;
            var name = ValidateName(entry.FullName, directory);
            if (!names.TryAdd(name, directory)) throw Error("The model pack contains duplicate file or folder names.");
            if (entry.Length < 0 || entry.Length > MaximumBytes - expanded || directory && entry.Length != 0)
                throw Error("The unpacked model pack must total at most 32 MB.");
            expanded += entry.Length;
            entries.Add((entry, name, directory));
        }
        foreach (var item in entries)
        {
            var slash = item.Name.IndexOf('/');
            while (slash >= 0)
            {
                if (names.TryGetValue(item.Name[..slash], out var directory) && !directory)
                    throw Error("The model pack uses the same name for a file and a folder.");
                slash = item.Name.IndexOf('/', slash + 1);
            }
        }
        var models = entries.Where(e => !e.Directory && IsModel(e.Name)).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        if (models.Length == 0) throw Error("This pack contains no GLB, glTF or OBJ models. Download one of those versions from the provider.");
        cancel.ThrowIfCancellationRequested();
        cacheDirectory = Path.GetFullPath(cacheDirectory);
        Directory.CreateDirectory(cacheDirectory);
        if (new DirectoryInfo(cacheDirectory).LinkTarget is not null)
            throw Error("The model pack storage folder cannot be a link.");
        var owned = Path.Combine(cacheDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(owned);
        try
        {
            var buffer = new byte[64 * 1024];
            long written = 0;
            foreach (var item in entries)
            {
                cancel.ThrowIfCancellationRequested();
                var target = Inside(owned, item.Name);
                if (item.Directory) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var source = item.Entry.Open();
                using var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                long copied = 0;
                while (true)
                {
                    cancel.ThrowIfCancellationRequested();
                    var count = source.Read(buffer);
                    if (count == 0) break;
                    if (count > item.Entry.Length - copied || count > MaximumBytes - written)
                        throw Error("The unpacked model pack is larger than its declared size.");
                    destination.Write(buffer, 0, count); copied += count; written += count;
                }
                if (copied != item.Entry.Length) throw Error("The model pack contains an incomplete file.");
            }
            cancel.ThrowIfCancellationRequested();
            return new(path, owned, models.Select(m => new GuideArchiveModel(m.Name, Inside(owned, m.Name))).ToArray());
        }
        catch
        {
            DeleteOwnedDirectory(owned);
            throw;
        }
    }

    private static bool IsModel(string path) => Path.GetExtension(path).ToLowerInvariant() is ".glb" or ".gltf" or ".obj";

    private static string ValidateName(string name, bool directory)
    {
        name = name.Replace('\\', '/');
        if (directory) name = name.TrimEnd('/');
        if (name.Length is 0 or > 512 || name.StartsWith('/')) throw Error("The model pack contains an invalid file path.");
        foreach (var part in name.Split('/'))
        {
            if (part.Length is 0 or > 255 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
                || part.Any(c => char.IsControl(c) || "<>:\"|?*".Contains(c)))
                throw Error("Model pack files must use ordinary relative folder names.");
            var stem = part.Split('.')[0];
            if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase)
                || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                    || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && (stem[3] is >= '1' and <= '9' or '¹' or '²' or '³'))
                throw Error("The model pack contains a reserved file name.");
        }
        return name;
    }

    private static string Inside(string root, string relative)
    {
        var target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw Error("Model pack files must stay inside their folder.");
        return target;
    }

    private static void DeleteOwnedDirectory(string path)
    {
        // The UI waits for all model readers before disposing a preview pack.
        // Filesystem failure must not break window teardown or hide its original error.
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static InvalidDataException Error(string message) => new(message);
}
