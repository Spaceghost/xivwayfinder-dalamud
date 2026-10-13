using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace XivWayfinder.Core;

public readonly record struct GuideNativeJumpSet(ushort Takeoff, ushort Falling, ushort Landing)
{
    public bool Valid => Takeoff > 0 && Falling > 0 && Landing > 0;
    // Native jump consists of separately authored phases rather than a single
    // loop pretending to be a complete jump. These bound the visual low hop;
    // phase transitions follow its collision-approved motion, not player flags.
    public const float TakeoffSeconds = 3f / 30;
    public const float FlightSeconds = .8f;
    public const float LandingSeconds = 14f / 30;
    public const float LowHopSeconds = FlightSeconds + LandingSeconds;
    public ushort Timeline(GuideJumpPhase phase) => phase switch
    {
        GuideJumpPhase.Takeoff => Takeoff,
        GuideJumpPhase.Airborne => Falling,
        _ => Landing,
    };
}

public static class GuideJumpAnimation
{
    /// <summary>Normal jump timelines also contain optional cfxf facial overlays, not body motion.</summary>
    public static bool NativeBodyReferencesSupported(IEnumerable<string> references, IReadOnlySet<string> residentNames)
    {
        // Confirmed against the installed normal/jump_* TMBs and MotionTimeline:
        // cbnm_jump_1/2/3 are body phases; cfxf_clench/angry use the facial group.
        // Every other referenced body animation must exist in this exact rig.
        var body = references.Where(name => !name.StartsWith("cfxf_", StringComparison.Ordinal)).ToArray();
        return body.Length > 0 && body.All(residentNames.Contains);
    }

    /// <summary>Checks the exact rendered pose as well as the premeasured clip envelope.</summary>
    public static bool PoseFits(ReadOnlySpan<Vector3> vertices, float scale, GuideJumpCapability capability, out float minimum)
    {
        minimum = float.PositiveInfinity;
        if (!capability.Valid || !float.IsFinite(scale) || scale <= 0 || vertices.IsEmpty) return false;
        var maximum = float.NegativeInfinity;
        foreach (var vertex in vertices)
        {
            if (!TrailPlacement.IsFinite(vertex) || new Vector2(vertex.X, vertex.Z).Length() * scale > capability.Radius) return false;
            minimum = Math.Min(minimum, vertex.Y); maximum = Math.Max(maximum, vertex.Y);
        }
        return (maximum - minimum) * scale <= capability.Height;
    }

    /// <summary>Only a complete explicitly named authored jump, not fall/loop/start/end/attack variants.</summary>
    public static int ImportedClip(IReadOnlyList<GuideAnimationClip> clips)
    {
        for (var i = 0; i < clips.Count; ++i)
        {
            var clip = clips[i];
            var normalized = new string(clip.Name.ToLowerInvariant().Where(char.IsLetter).ToArray());
            if (normalized is not ("jump" or "hop" or "standingjump" or "forwardjump" or "runningjump")) continue;
            if (float.IsFinite(clip.DurationSeconds) && clip.DurationSeconds is >= .3f and <= 1.8f) return i;
        }
        return -1;
    }

    /// <summary>Measure authored poses once during asset preparation; horizontal in-place root motion is removed.</summary>
    public static GuideJumpCapability MeasureImported(GuideMesh mesh, CancellationToken cancel = default)
    {
        var clip = ImportedClip(mesh.Animations);
        if (clip < 0 || mesh.Vertices.Length is < 1 or > 200000) return default;
        var vertices = new Vector3[mesh.Vertices.Length];
        var radius = 0f; var height = 0f;
        for (var sample = 0; sample <= 32; ++sample)
        {
            cancel.ThrowIfCancellationRequested();
            mesh.SamplePose(clip, sample / 32f * mesh.Animations[clip].DurationSeconds, vertices, loop: false, inPlace: true);
            var low = float.PositiveInfinity; var high = float.NegativeInfinity;
            foreach (var vertex in vertices)
            {
                if (!TrailPlacement.IsFinite(vertex)) return default;
                radius = Math.Max(radius, new Vector2(vertex.X, vertex.Z).Length());
                low = Math.Min(low, vertex.Y); high = Math.Max(high, vertex.Y);
            }
            height = Math.Max(height, high - low);
        }
        // A runtime frame is checked against this envelope too, so cubic
        // animation overshoot between samples cannot quietly escape clearance.
        return new(GuideJumpAnimationKind.ImportedClip, clip, mesh.Animations[clip].Name,
            mesh.Animations[clip].DurationSeconds, radius * 1.15f + .025f, height * 1.15f + .025f);
    }

    /// <summary>
    /// Bounded metadata-only C010 animation names from TMLB chunks. Layout and
    /// relative string offsets match the documented VFXEditor TmbReader/C010
    /// format; no timeline is executed. Unknown/malformed chunks fail closed.
    /// </summary>
    public static string[] TimelineAnimationNames(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12 || !bytes[..4].SequenceEqual("TMLB"u8)) return [];
        var size = BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]);
        var count = BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]);
        if (size < 12 || size > bytes.Length || count is < 1 or > 4096) return [];
        var names = new List<string>(); var at = 12;
        for (var entry = 0; entry < count; ++entry)
        {
            if (at > size - 8) return [];
            var chunk = BinaryPrimitives.ReadInt32LittleEndian(bytes[(at + 4)..]);
            if (chunk < 8 || chunk > size - at) return [];
            if (bytes.Slice(at, 4).SequenceEqual("C010"u8))
            {
                if (chunk < 40) return [];
                var offset = BinaryPrimitives.ReadInt32LittleEndian(bytes[(at + 32)..]);
                var textAt = (long)at + 8 + offset;
                if (offset <= 0 || textAt < 12 || textAt >= size) return [];
                var span = bytes.Slice((int)textAt, Math.Min(256, size - (int)textAt));
                var end = span.IndexOf((byte)0);
                if (end is < 1 or > 255) return [];
                var name = Encoding.ASCII.GetString(span[..end]);
                if (name.Any(c => c < ' ' || c > '~')) return [];
                names.Add(Path.GetFileNameWithoutExtension(name.Replace('\\', '/')));
            }
            at += chunk;
        }
        return names.Distinct(StringComparer.Ordinal).ToArray();
    }
}
