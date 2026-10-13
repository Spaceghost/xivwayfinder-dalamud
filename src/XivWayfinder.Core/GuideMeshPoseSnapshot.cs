using System.Numerics;
using XivVisualAssets.Animation;

namespace XivWayfinder.Core;

/// <summary>
/// Immutable rig-owned local pose, including morphs and in-place root offsets.
/// Captures a blend as it actually was accepted, not an approximation using its
/// destination clip. No mutable buffers or native addresses are exposed.
/// </summary>
public sealed class GuideMeshPoseSnapshot
{
    internal GuideMeshAnimation Owner { get; }
    public bool InPlace { get; }
    private readonly Vector3[] translations, scales, displacements;
    private readonly Quaternion[] rotations;
    private readonly float[] weights;
    internal ReadOnlySpan<Vector3> Translations => translations;
    internal ReadOnlySpan<Quaternion> Rotations => rotations;
    internal ReadOnlySpan<Vector3> Scales => scales;
    internal ReadOnlySpan<float> Weights => weights;
    internal ReadOnlySpan<Vector3> RootDisplacements => displacements;

    internal GuideMeshPoseSnapshot(GuideMeshAnimation owner, bool inPlace, ReadOnlySpan<Vector3> translation,
        ReadOnlySpan<Quaternion> rotation, ReadOnlySpan<Vector3> scale, ReadOnlySpan<float> weight, ReadOnlySpan<Vector3> displacement)
    {
        Owner = owner; InPlace = inPlace; translations = translation.ToArray(); rotations = rotation.ToArray();
        scales = scale.ToArray(); weights = weight.ToArray(); displacements = displacement.ToArray();
    }
}

/// <summary>Exact locally sampled pose and canonical foot root of a submitted
/// draw, for continuous transitions into boarding. Not receiver/scene evidence.</summary>
public readonly record struct GuideBoardingRenderedPose(GuideMeshPoseSnapshot LocalPose, Vector3 FootPosition,
    Quaternion Rotation, float Scale, double SubmittedAt);

public sealed partial record GuideMesh
{
    public GuideMeshPoseSnapshot BlendCapturedPoses(GuideMeshPoseSnapshot source,GuideMeshPoseSnapshot target,float blend)
    {
        ValidateSnapshot(source,-1,0,blend);ValidateSnapshot(target,-1,0,blend);
        if(source.InPlace!=target.InPlace)throw new ArgumentException("Captured root-motion contracts differ.");
        var translations=source.Translations.ToArray();var rotations=source.Rotations.ToArray();
        var scales=source.Scales.ToArray();var weights=source.Weights.ToArray();var displacement=source.RootDisplacements.ToArray();
        for(var i=0;i<translations.Length;i++)
        {
            translations[i]=Vector3.Lerp(translations[i],target.Translations[i],blend);
            rotations[i]=Quaternion.Normalize(Quaternion.Slerp(rotations[i],target.Rotations[i],blend));
            scales[i]=Vector3.Lerp(scales[i],target.Scales[i],blend);
            displacement[i]=Vector3.Lerp(displacement[i],target.RootDisplacements[i],blend);
        }
        for(var i=0;i<weights.Length;i++)weights[i]+=(target.Weights[i]-weights[i])*blend;
        return new(animation!,source.InPlace,translations,rotations,scales,weights,displacement);
    }
    public GuideMeshPoseSnapshot CapturePose(VisualAnimationFrame frame, bool loop = true, bool inPlace = false)
    {
        if (animation is null) throw new InvalidOperationException("A rig is required for local-pose capture.");
        ValidateSnapshotTarget(frame.FromClip, frame.FromTime, frame.Blend);
        ValidateSnapshotTarget(frame.ToClip, frame.ToTime, frame.Blend);
        return animation.Sample(frame.FromClip, SnapshotTime(frame.FromClip, frame.FromTime, loop), [], inPlace,
            frame.ToClip, SnapshotTime(frame.ToClip, frame.ToTime, loop), frame.Blend, capture: true)!;
    }

    public void SampleCapturedPose(GuideMeshPoseSnapshot captured, Span<Vector3> vertices, Span<Vector3> normals = default) =>
        SampleFromCapture(captured, -1, 0, 0, vertices, loop: false, normals);

    public void SampleFromCapture(GuideMeshPoseSnapshot captured, int targetClip, float time, float blend,
        Span<Vector3> vertices, bool loop = true, Span<Vector3> normals = default)
    {
        ValidateSnapshot(captured, targetClip, time, blend);
        if (vertices.Length < Vertices.Length || !normals.IsEmpty && normals.Length < Vertices.Length)
            throw new ArgumentException("Pose buffer is too small.");
        animation!.Sample(targetClip, SnapshotTime(targetClip, time, loop), vertices, captured.InPlace, normals: normals,
            source: captured, targetBlend: blend);
    }

    public GuideMeshPoseSnapshot CaptureFromSnapshot(GuideMeshPoseSnapshot captured, int targetClip, float time,
        float blend, bool loop = true)
    {
        ValidateSnapshot(captured, targetClip, time, blend);
        return animation!.Sample(targetClip, SnapshotTime(targetClip, time, loop), [], captured.InPlace,
            capture: true, source: captured, targetBlend: blend)!;
    }

    private void ValidateSnapshot(GuideMeshPoseSnapshot captured, int clip, float time, float blend)
    {
        ArgumentNullException.ThrowIfNull(captured);
        if (animation is null || !ReferenceEquals(captured.Owner, animation))
            throw new ArgumentException("Pose capture belongs to another asset/rig.", nameof(captured));
        ValidateSnapshotTarget(clip, time, blend);
    }
    private void ValidateSnapshotTarget(int clip, float time, float blend)
    {
        if (clip < -1 || clip >= Animations.Count || !float.IsFinite(time) || !float.IsFinite(blend) || blend is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(clip));
    }
    private float SnapshotTime(int clip, float time, bool loop)
    {
        var duration = clip < 0 ? 0 : Animations[clip].DurationSeconds;
        return loop && duration > 0 ? ((time % duration) + duration) % duration : Math.Clamp(time, 0, duration);
    }
}
