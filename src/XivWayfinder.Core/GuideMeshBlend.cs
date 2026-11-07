using System.Numerics;
using XivVisualAssets.Animation;

namespace XivWayfinder.Core;

public sealed partial record GuideMesh
{
    /// <summary>Blends authored local bone/node poses before hierarchy transforms,
    /// morph deformation and skinning. Clip clocks are seconds; -1 selects the
    /// bind pose. Both clips retain their own in-place root-motion reference.
    /// Optional normal output uses the resulting blended transforms and morph weights.</summary>
    public void SampleBlendedPose(VisualAnimationFrame frame, Span<Vector3> destination,
        bool loop = true, bool inPlace = false, Span<Vector3> normals = default)
    {
        if (destination.Length < Vertices.Length) throw new ArgumentException("Pose buffer is too small.", nameof(destination));
        if (!normals.IsEmpty && normals.Length < Vertices.Length) throw new ArgumentException("Normal buffer is too small.", nameof(normals));
        Validate(frame.FromClip, frame.FromTime); Validate(frame.ToClip, frame.ToTime);
        if (!float.IsFinite(frame.Blend) || frame.Blend < 0 || frame.Blend > 1)
            throw new ArgumentOutOfRangeException(nameof(frame));
        if (frame.Blend == 0) { SamplePose(frame.FromClip, frame.FromTime, destination, loop, inPlace, normals); return; }
        if (frame.Blend == 1) { SamplePose(frame.ToClip, frame.ToTime, destination, loop, inPlace, normals); return; }
        if (animation is null || frame.FromClip == -1 && frame.ToClip == -1) { Vertices.CopyTo(destination); CopyNormals(normals); return; }
        animation.Sample(frame.FromClip, Time(frame.FromClip, frame.FromTime), destination, inPlace,
            frame.ToClip, Time(frame.ToClip, frame.ToTime), frame.Blend, normals);

        void Validate(int clip, float time)
        {
            if (clip < -1 || clip >= Animations.Count || !float.IsFinite(time))
                throw new ArgumentOutOfRangeException(nameof(frame));
        }
        float Time(int clip, float time)
        {
            var duration = clip < 0 ? 0 : Animations[clip].DurationSeconds;
            return loop && duration > 0 ? ((time % duration) + duration) % duration : Math.Clamp(time, 0, duration);
        }
    }
}
