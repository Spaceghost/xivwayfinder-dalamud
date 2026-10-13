using System.Buffers;
using System.Numerics;

namespace XivWayfinder.Core;

public sealed record GuideAnimationClip(string Name, float DurationSeconds);
public sealed record GuideMeshImage(string MimeType, byte[] Data, int Width = 0, int Height = 0);
public sealed record GuideMeshMaterial(Vector4 BaseColorFactor, int BaseColorImage = -1,
    bool DoubleSided = false, string AlphaMode = "OPAQUE", float AlphaCutoff = .5f,
    int WrapU = 10497, int WrapV = 10497, bool Unlit = false);

public sealed partial record GuideMesh
{
    public string SourceSha256 { get; internal init; } = "";
    internal GuideMeshAnimation? animation;
    public IReadOnlyList<GuideAnimationClip> Animations { get; internal init; } = Array.Empty<GuideAnimationClip>();
    public Vector2[] TextureCoordinates { get; internal init; } = [];
    public Vector4[] VertexColors { get; internal init; } = [];
    /// <summary>Bind-pose unit normals. Zero selects geometric face shading.</summary>
    public Vector3[] Normals { get; internal init; } = [];
    public int[] FaceMaterials { get; internal init; } = [];
    public IReadOnlyList<GuideMeshMaterial> Materials { get; internal init; } = Array.Empty<GuideMeshMaterial>();
    public IReadOnlyList<GuideMeshImage> Images { get; internal init; } = Array.Empty<GuideMeshImage>();

    /// <summary>Samples normalized model-space vertices. -1 selects the bind pose. Time is in seconds.
    /// Normalization remains fixed across frames, preserving the author's skeletal and node movement.
    /// Destination may be reused each frame. Sampling does not mutate this model or the bind pose.
    /// In-place sampling removes horizontal translation accumulated since the clip's first pose on
    /// each rendered model's common root chain. Limb motion, authored offsets and vertical motion remain.
    /// Optional normal output follows morphing and skinning; zero selects the current geometric face normal.</summary>
    public void SamplePose(int clipIndex, float timeSeconds, Span<Vector3> destination, bool loop = true, bool inPlace = false,
        Span<Vector3> normals = default)
    {
        if (destination.Length < Vertices.Length) throw new ArgumentException("Pose buffer is too small.", nameof(destination));
        if (!normals.IsEmpty && normals.Length < Vertices.Length) throw new ArgumentException("Normal buffer is too small.", nameof(normals));
        if (clipIndex < -1 || clipIndex >= Animations.Count) throw new ArgumentOutOfRangeException(nameof(clipIndex));
        if (!float.IsFinite(timeSeconds)) throw new ArgumentOutOfRangeException(nameof(timeSeconds));
        if (clipIndex == -1 || animation is null) { Vertices.CopyTo(destination); CopyNormals(normals); return; }
        var duration = Animations[clipIndex].DurationSeconds;
        var time = loop && duration > 0 ? ((timeSeconds % duration) + duration) % duration : Math.Clamp(timeSeconds, 0, duration);
        animation.Sample(clipIndex, time, destination, inPlace, normals: normals);
    }

    private void CopyNormals(Span<Vector3> destination)
    {
        if (destination.IsEmpty) return;
        destination[..Vertices.Length].Clear();
        Normals.AsSpan(0, Math.Min(Normals.Length, Vertices.Length)).CopyTo(destination);
    }
}

internal sealed record GuideMeshNode(int Parent, Matrix4x4? Matrix, Vector3 Translation, Quaternion Rotation,
    Vector3 Scale, float[] Weights);
internal sealed record GuideMeshSkin(int[] Joints, Matrix4x4[] InverseBindMatrices);
internal sealed record GuideMeshBinding(int First, int Node, Vector3[] Positions, GuideMeshSkin? Skin,
    ushort[][] Joints, Vector4[][] Weights, Vector3[][] MorphTargets, Vector3[] Normals, Vector3[][] MorphNormals);
internal enum GuideAnimationPath { Translation, Rotation, Scale, Weights }
internal enum GuideInterpolation { Linear, Step, CubicSpline }

internal sealed record GuideMeshChannel(int Node, GuideAnimationPath Path, GuideInterpolation Interpolation,
    float[] Times, float[] Values, int Width)
{
    public void Sample(float time, Span<float> value)
    {
        var frame = Array.BinarySearch(Times, time);
        if (frame < 0) frame = ~frame - 1;
        frame = Math.Clamp(frame, 0, Times.Length - 1);
        var cubic = Interpolation == GuideInterpolation.CubicSpline;
        var stride = Width * (cubic ? 3 : 1);
        var first = frame * stride + (cubic ? Width : 0);
        if (frame == Times.Length - 1 || time <= Times[0] || Interpolation == GuideInterpolation.Step)
        {
            Values.AsSpan(first, Width).CopyTo(value);
            return;
        }
        var delta = Times[frame + 1] - Times[frame];
        var t = Math.Clamp((time - Times[frame]) / delta, 0, 1);
        var next = first + stride;
        if (!cubic && Path == GuideAnimationPath.Rotation)
        {
            var q = Quaternion.Slerp(new(Values[first], Values[first + 1], Values[first + 2], Values[first + 3]),
                new(Values[next], Values[next + 1], Values[next + 2], Values[next + 3]), t);
            value[0] = q.X; value[1] = q.Y; value[2] = q.Z; value[3] = q.W;
            return;
        }
        var t2 = t * t; var t3 = t2 * t;
        for (var i = 0; i < Width; i++)
            value[i] = cubic
                ? (2 * t3 - 3 * t2 + 1) * Values[first + i] + delta * (t3 - 2 * t2 + t) * Values[first + Width + i]
                  + (-2 * t3 + 3 * t2) * Values[next + i] + delta * (t3 - t2) * Values[next - Width + i]
                : Values[first + i] + t * (Values[next + i] - Values[first + i]);
    }
}

internal sealed class GuideMeshAnimation(GuideMeshNode[] nodes, int[] order, GuideMeshBinding[] bindings,
    GuideMeshChannel[][] clips)
{
    private readonly Dictionary<int, Vector3>[] rootTranslations = FindRootTranslations(nodes, bindings, clips);
    private Vector3 center;
    private float extent = 1;
    public void SetNormalization(Vector3 value, float size) { center = value; extent = size; }

    public GuideMeshPoseSnapshot? Sample(int clipIndex, float time, Span<Vector3> destination, bool inPlace = false,
        int? blendClipIndex = null, float blendTime = 0, float blend = 0, Span<Vector3> normals = default,
        bool capture = false, GuideMeshPoseSnapshot? source = null, float targetBlend = 1)
    {
        if (!float.IsFinite(targetBlend) || targetBlend is < 0 or > 1
            || source is not null && (!ReferenceEquals(source.Owner, this) || source.InPlace != inPlace))
            throw new ArgumentException("Pose capture belongs to another rig or root-motion contract.", nameof(source));
        // Pool scratch storage: model selection/loading is infrequent; pose sampling happens each frame.
        var translations = ArrayPool<Vector3>.Shared.Rent(nodes.Length);
        var rotations = ArrayPool<Quaternion>.Shared.Rent(nodes.Length);
        var scales = ArrayPool<Vector3>.Shared.Rent(nodes.Length);
        var world = ArrayPool<Matrix4x4>.Shared.Rent(nodes.Length);
        var weightOffsets = ArrayPool<int>.Shared.Rent(nodes.Length);
        var totalWeights = nodes.Sum(n => n.Weights.Length);
        var weights = ArrayPool<float>.Shared.Rent(totalWeights);
        Vector3[]? blendedRootMotion = null;
        try
        {
            SampleLocal(clipIndex, time, translations, rotations, scales, weights, weightOffsets);
            var rootMotion = inPlace && clipIndex >= 0 ? rootTranslations[clipIndex] : null;
            if (blendClipIndex is { } secondClip)
            {
                var secondTranslations = ArrayPool<Vector3>.Shared.Rent(nodes.Length);
                var secondRotations = ArrayPool<Quaternion>.Shared.Rent(nodes.Length);
                var secondScales = ArrayPool<Vector3>.Shared.Rent(nodes.Length);
                var secondWeights = ArrayPool<float>.Shared.Rent(totalWeights);
                try
                {
                    SampleLocal(secondClip, blendTime, secondTranslations, secondRotations, secondScales, secondWeights, weightOffsets);
                    var secondRootMotion = inPlace && secondClip >= 0 ? rootTranslations[secondClip] : null;
                    if (inPlace) blendedRootMotion = ArrayPool<Vector3>.Shared.Rent(nodes.Length);
                    for (var i = 0; i < nodes.Length; i++)
                    {
                        if (blendedRootMotion is not null)
                        {
                            var firstMotion = rootMotion is not null && rootMotion.TryGetValue(i, out var firstStart)
                                ? translations[i] - firstStart : Vector3.Zero;
                            var secondMotion = secondRootMotion is not null && secondRootMotion.TryGetValue(i, out var secondStart)
                                ? secondTranslations[i] - secondStart : Vector3.Zero;
                            blendedRootMotion[i] = Vector3.Lerp(firstMotion, secondMotion, blend);
                        }
                        translations[i] = Vector3.Lerp(translations[i], secondTranslations[i], blend);
                        scales[i] = Vector3.Lerp(scales[i], secondScales[i], blend);
                        rotations[i] = Quaternion.Normalize(Quaternion.Slerp(rotations[i], secondRotations[i], blend));
                    }
                    for (var i = 0; i < totalWeights; i++) weights[i] += (secondWeights[i] - weights[i]) * blend;
                }
                finally
                {
                    ArrayPool<Vector3>.Shared.Return(secondTranslations); ArrayPool<Quaternion>.Shared.Return(secondRotations);
                    ArrayPool<Vector3>.Shared.Return(secondScales); ArrayPool<float>.Shared.Return(secondWeights);
                }
            }
            // Capture/recovery mixes LOCAL pose state, not deformed vertices.
            // Root-motion cancellation is captured independently per node so
            // it retains its correct parent-space reference through reblends.
            if (source is not null && targetBlend < 1)
            {
                if (inPlace && blendedRootMotion is null)
                {
                    blendedRootMotion = ArrayPool<Vector3>.Shared.Rent(nodes.Length);
                    for (var i = 0; i < nodes.Length; i++)
                        blendedRootMotion[i] = rootMotion is not null && rootMotion.TryGetValue(i, out var initial)
                            ? translations[i] - initial : Vector3.Zero;
                }
                for (var i = 0; i < nodes.Length; i++)
                {
                    if (targetBlend == 0)
                    {
                        translations[i] = source.Translations[i]; rotations[i] = source.Rotations[i]; scales[i] = source.Scales[i];
                        if (blendedRootMotion is not null) blendedRootMotion[i] = source.RootDisplacements[i];
                    }
                    else
                    {
                        translations[i] = Vector3.Lerp(source.Translations[i], translations[i], targetBlend);
                        rotations[i] = Quaternion.Normalize(Quaternion.Slerp(source.Rotations[i], rotations[i], targetBlend));
                        scales[i] = Vector3.Lerp(source.Scales[i], scales[i], targetBlend);
                        if (blendedRootMotion is not null)
                            blendedRootMotion[i] = Vector3.Lerp(source.RootDisplacements[i], blendedRootMotion[i], targetBlend);
                    }
                }
                for (var i = 0; i < totalWeights; i++)
                    weights[i] = targetBlend == 0 ? source.Weights[i] : source.Weights[i] + (weights[i] - source.Weights[i]) * targetBlend;
            }
            if (capture)
            {
                var displacement = new Vector3[nodes.Length];
                if (inPlace)
                    for (var i = 0; i < nodes.Length; i++)
                        displacement[i] = blendedRootMotion is not null ? blendedRootMotion[i]
                            : rootMotion is not null && rootMotion.TryGetValue(i, out var initial) ? translations[i] - initial : Vector3.Zero;
                return new(this, inPlace, translations.AsSpan(0, nodes.Length), rotations.AsSpan(0, nodes.Length),
                    scales.AsSpan(0, nodes.Length), weights.AsSpan(0, totalWeights), displacement);
            }
            foreach (var i in order)
            {
                var local = nodes[i].Matrix ?? Matrix4x4.CreateScale(scales[i]) * Matrix4x4.CreateFromQuaternion(rotations[i]) * Matrix4x4.CreateTranslation(translations[i]);
                world[i] = nodes[i].Parent < 0 ? local : local * world[nodes[i].Parent];
                var displacement = blendedRootMotion is not null ? blendedRootMotion[i]
                    : rootMotion is not null && rootMotion.TryGetValue(i, out var initial) ? translations[i] - initial : Vector3.Zero;
                if (displacement != Vector3.Zero)
                {
                    if (nodes[i].Parent >= 0) displacement = Vector3.TransformNormal(displacement, world[nodes[i].Parent]);
                    // Cancel movement in model/world XZ, not the node's local XZ: DCC exports often
                    // put a rotated axis-conversion node above a skeleton. Keep the world's vertical bounce.
                    world[i].M41 -= displacement.X;
                    world[i].M43 -= displacement.Z;
                }
            }
            foreach (var binding in bindings)
            {
                var skin = binding.Skin;
                var jointMatrices = skin is null ? null : ArrayPool<Matrix4x4>.Shared.Rent(skin.Joints.Length);
                var sampleNormals = !normals.IsEmpty && binding.Normals.Length > 0;
                var rigidNormalTransform = sampleNormals && skin is null ? new GuideNormalTransform(world[binding.Node]) : default;
                try
                {
                    if (skin is not null)
                        for (var i = 0; i < skin.Joints.Length; i++) jointMatrices![i] = skin.InverseBindMatrices[i] * world[skin.Joints[i]];
                    for (var v = 0; v < binding.Positions.Length; v++)
                    {
                        var position = binding.Positions[v];
                        var normal = sampleNormals ? binding.Normals[v] : Vector3.Zero;
                        for (var target = 0; target < binding.MorphTargets.Length; target++)
                        {
                            var weight = weights[weightOffsets[binding.Node] + target];
                            position += binding.MorphTargets[target][v] * weight;
                            if (sampleNormals && binding.MorphNormals[target].Length > 0) normal += binding.MorphNormals[target][v] * weight;
                        }
                        Vector3 transformed;
                        if (skin is null)
                        {
                            transformed = Vector3.Transform(position, world[binding.Node]);
                            if (sampleNormals) normal = rigidNormalTransform.Apply(normal);
                        }
                        else
                        {
                            transformed = Vector3.Zero;
                            var skinMatrix = default(Matrix4x4);
                            for (var set = 0; set < binding.Joints.Length; set++)
                            {
                                var joints = binding.Joints[set]; var w = binding.Weights[set][v]; var at = v * 4;
                                if (w.X > 0) transformed += Vector3.Transform(position, jointMatrices![joints[at]]) * w.X;
                                if (w.Y > 0) transformed += Vector3.Transform(position, jointMatrices![joints[at + 1]]) * w.Y;
                                if (w.Z > 0) transformed += Vector3.Transform(position, jointMatrices![joints[at + 2]]) * w.Z;
                                if (w.W > 0) transformed += Vector3.Transform(position, jointMatrices![joints[at + 3]]) * w.W;
                                if (sampleNormals)
                                {
                                    if (w.X > 0) skinMatrix += jointMatrices![joints[at]] * w.X;
                                    if (w.Y > 0) skinMatrix += jointMatrices![joints[at + 1]] * w.Y;
                                    if (w.Z > 0) skinMatrix += jointMatrices![joints[at + 2]] * w.Z;
                                    if (w.W > 0) skinMatrix += jointMatrices![joints[at + 3]] * w.W;
                                }
                            }
                            if (sampleNormals) normal = new GuideNormalTransform(skinMatrix).Apply(normal);
                        }
                        var normalized = (transformed - center) / extent;
                        if (!float.IsFinite(normalized.X) || !float.IsFinite(normalized.Y) || !float.IsFinite(normalized.Z))
                            throw new InvalidDataException("The model animation produces invalid coordinates.");
                        destination[binding.First + v] = normalized;
                        if (!normals.IsEmpty) normals[binding.First + v] = normal;
                    }
                }
                finally { if (jointMatrices is not null) ArrayPool<Matrix4x4>.Shared.Return(jointMatrices); }
            }
        }
        finally
        {
            ArrayPool<Vector3>.Shared.Return(translations); ArrayPool<Quaternion>.Shared.Return(rotations);
            ArrayPool<Vector3>.Shared.Return(scales); ArrayPool<Matrix4x4>.Shared.Return(world);
            ArrayPool<int>.Shared.Return(weightOffsets); ArrayPool<float>.Shared.Return(weights);
            if (blendedRootMotion is not null) ArrayPool<Vector3>.Shared.Return(blendedRootMotion);
        }
        return null;
    }

    private void SampleLocal(int clipIndex, float time, Vector3[] translations, Quaternion[] rotations,
        Vector3[] scales, float[] weights, int[] weightOffsets)
    {
        var offset = 0;
        for (var i = 0; i < nodes.Length; i++)
        {
            translations[i] = nodes[i].Translation; rotations[i] = nodes[i].Rotation; scales[i] = nodes[i].Scale;
            weightOffsets[i] = offset; nodes[i].Weights.CopyTo(weights, offset); offset += nodes[i].Weights.Length;
        }
        Span<float> sampled = stackalloc float[64];
        if (clipIndex < 0) return;
        foreach (var channel in clips[clipIndex])
        {
            channel.Sample(time, sampled);
            switch (channel.Path)
            {
                case GuideAnimationPath.Translation: translations[channel.Node] = new(sampled[0], sampled[1], sampled[2]); break;
                case GuideAnimationPath.Scale: scales[channel.Node] = new(sampled[0], sampled[1], sampled[2]); break;
                case GuideAnimationPath.Rotation:
                    var q = new Quaternion(sampled[0], sampled[1], sampled[2], sampled[3]);
                    rotations[channel.Node] = q.LengthSquared() > 1e-12f ? Quaternion.Normalize(q) : nodes[channel.Node].Rotation;
                    break;
                case GuideAnimationPath.Weights: sampled[..channel.Width].CopyTo(weights.AsSpan(weightOffsets[channel.Node])); break;
            }
        }
    }

    private static Dictionary<int, Vector3>[] FindRootTranslations(GuideMeshNode[] nodes,
        GuideMeshBinding[] bindings, GuideMeshChannel[][] clips)
    {
        // Geometry establishes a model's root. Bone names are neither standardized nor reliable.
        // Include only joints that actually influence vertices; unused helper joints must not
        // move the common root above the locomotion bone. Rigid attachments participate too.
        var used = new HashSet<int>();
        foreach (var binding in bindings)
        {
            if (binding.Skin is not { } skin) { used.Add(binding.Node); continue; }
            for (var set = 0; set < binding.Joints.Length; set++)
                for (var vertex = 0; vertex < binding.Positions.Length; vertex++)
                {
                    var joints = binding.Joints[set]; var weights = binding.Weights[set][vertex]; var at = vertex * 4;
                    if (weights.X > 0) used.Add(skin.Joints[joints[at]]);
                    if (weights.Y > 0) used.Add(skin.Joints[joints[at + 1]]);
                    if (weights.Z > 0) used.Add(skin.Joints[joints[at + 2]]);
                    if (weights.W > 0) used.Add(skin.Joints[joints[at + 3]]);
                }
        }
        var perTree = new Dictionary<int, int>();
        foreach (var node in used)
        {
            var top = node;
            while (nodes[top].Parent >= 0) top = nodes[top].Parent;
            perTree[top] = perTree.TryGetValue(top, out var common) ? CommonAncestor(common, node) : node;
        }
        var roots = new HashSet<int>();
        foreach (var common in perTree.Values)
            for (var at = common; at >= 0; at = nodes[at].Parent) roots.Add(at);
        var result = new Dictionary<int, Vector3>[clips.Length];
        Span<float> first = stackalloc float[4];
        for (var clip = 0; clip < clips.Length; clip++)
        {
            result[clip] = [];
            foreach (var channel in clips[clip])
                if (channel.Path == GuideAnimationPath.Translation && roots.Contains(channel.Node))
                {
                    channel.Sample(0, first);
                    result[clip][channel.Node] = new(first[0], first[1], first[2]);
                }
        }
        return result;

        int CommonAncestor(int left, int right)
        {
            var ancestors = new HashSet<int>();
            for (var at = left; at >= 0; at = nodes[at].Parent) ancestors.Add(at);
            for (var at = right; at >= 0; at = nodes[at].Parent) if (ancestors.Contains(at)) return at;
            throw new InvalidDataException("The model's render hierarchy has no common root.");
        }
    }
}
