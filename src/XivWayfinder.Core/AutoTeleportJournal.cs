using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;

namespace XivWayfinder.Core;

/// <summary>
/// Small replace-only journal. Flush the reservation before returning to the cast
/// caller; a failed write never authorizes a cast. A stale second plugin instance
/// cannot overwrite newer history. No game or configuration services are used.
/// </summary>
public sealed class AutoTeleportJournal(string path)
{
    private string? expectedFingerprint;
    private bool loaded;

    /// <summary>Stable character scope without putting a raw content ID into paths or journal data.</summary>
    public static string CharacterFileName(ulong contentId)
    {
        if (contentId == 0) throw new ArgumentOutOfRangeException(nameof(contentId));
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(contentId.ToString(CultureInfo.InvariantCulture)));
        return "automatic-teleport-" + Convert.ToHexString(digest) + ".json";
    }

    public AutoTeleportState? Read()
    {
        var current = ReadCurrent();
        expectedFingerprint = current.Fingerprint;
        loaded = true;
        if (current.Bytes is null) return null;
        return current.Oversized ? new() { Version = 0 } : AutoTeleportState.Decode(Encoding.UTF8.GetString(current.Bytes));
    }

    public void Write(AutoTeleportState state)
    {
        if (!loaded) throw new IOException("Teleport history must be read successfully before it can be replaced.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state);
        if (bytes.Length > AutoTeleportState.MaximumJsonBytes) throw new InvalidDataException("Teleport history exceeds its size limit.");
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        // This is a scoped journal lock, not the user's complete configuration.
        // FileShare.None also rejects competing instances in the same process.
        using var journalLock = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (ReadCurrent().Fingerprint != expectedFingerprint)
            throw new IOException("Teleport history changed in another instance; reload before retrying.");
        var temporary = Path.Combine(directory, ".automatic-teleport-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            // Same-directory rename: readers see the complete old or new file,
            // never a partially serialized reservation. No state is cast first.
            File.Move(temporary, path, overwrite: true);
            var written = ReadCurrent();
            if (written.Oversized || written.Bytes is null || !written.Bytes.AsSpan().SequenceEqual(bytes))
                throw new IOException("The saved teleport reservation could not be verified.");
            expectedFingerprint = written.Fingerprint;
        }
        finally
        {
            // Only our uniquely-created temporary file is ever removed.
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private (byte[]? Bytes, string? Fingerprint, bool Oversized) ReadCurrent()
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var length = stream.Length;
            var bytes = new byte[(int)Math.Min(length, AutoTeleportState.MaximumJsonBytes + 1L)];
            stream.ReadExactly(bytes);
            // Oversized/corrupt files remain recoverable by explicit Retry, but
            // neither allocation nor hashing can grow with their total length.
            var fingerprint = $"{length}:{File.GetLastWriteTimeUtc(path).Ticks}:{Convert.ToHexString(SHA256.HashData(bytes))}";
            return (bytes, fingerprint, length > AutoTeleportState.MaximumJsonBytes);
        }
        catch (FileNotFoundException) { return (null, null, false); }
        catch (DirectoryNotFoundException) { return (null, null, false); }
    }
}
