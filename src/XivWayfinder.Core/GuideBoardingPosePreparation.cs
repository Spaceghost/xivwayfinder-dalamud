using System.Numerics;

namespace XivWayfinder.Core;

/// <summary>
/// Measured receiving space, not a volume invented from a player root/offset.
/// The runtime producer must establish the actual same-character receiving
/// pose or cast-effect volume and occupancy. TeleportGround has a separate
/// navmesh/floor producer below, not a claim about VFX or arms. Synchronized hand
/// bones alone do not establish a receiving pose.
/// </summary>
public readonly record struct GuideBoardingReceiverVolume(GuideBoardingIdentity Identity, GuideBoardingAnchor Anchor,
    ulong PoseSequence, Vector3 Center, Quaternion Rotation, Vector3 HalfExtents,
    GuideBoardingAvatarExclusion? Avatar = null)
{
    public bool Valid(GuideBoardingFrame frame, double now) => Identity == frame.Identity && PoseSequence != 0
        && Anchor.PlayerGeneration == Identity.PlayerGeneration && Anchor.Fits(frame.Capability, now)
        && GuideBoardingGeometry.Finite(Center) && GuideBoardingGeometry.Unit(Rotation)
        && GuideBoardingGeometry.Finite(HalfExtents) && HalfExtents.X is >= .01f and <= 3
        && HalfExtents.Y is >= .01f and <= 3 && HalfExtents.Z is >= .01f and <= 3
        && (Anchor.Target != GuideBoardingTarget.TeleportGround || Avatar is { } avatar && avatar.Clear(frame, now));
    public bool Contains(Vector3 point)
    {
        var local = Vector3.Transform(point - Center, Quaternion.Conjugate(Rotation));
        return Math.Abs(local.X) <= HalfExtents.X && Math.Abs(local.Y) <= HalfExtents.Y && Math.Abs(local.Z) <= HalfExtents.Z;
    }
}

/// <summary>The callback must check actual receiver/player occupancy and scene/landing clearance for these exact world triangles.</summary>
public delegate GuideBoardingPoseProof GuideBoardingMeshClearance(GuideBoardingFrame frame, ReadOnlySpan<Vector3> worldVertices,
    ReadOnlySpan<int> indices, GuideBoardingReceiverVolume? receiver, double now);
public readonly record struct GuideBoardingPoseProof(GuideJumpEvidence Evidence, double SampledAt, ulong ReceiverPoseSequence);

/// <summary>
/// Immutable render receipt: the same evaluated vertices/normals that passed
/// preflight, not a clip/time instruction which the UI thread can advance.
/// Position denotes the canonical foot root; the model's raw bind center is
/// removed exactly once before the complete root rotation and scale.
/// </summary>
public sealed class GuideBoardingPreparedPose
{
    private readonly Vector3[] vertices, normals;
    private readonly int[] indices;
    internal GuideMesh Mesh { get; }
    public GuideMeshPoseSnapshot LocalPose { get; }
    public GuideBoardingFrame Frame { get; }
    public float Scale { get; }
    public double VerifiedAt { get; }
    public double ReceiverAt { get; }
    public ReadOnlySpan<Vector3> WorldVertices => vertices;
    public ReadOnlySpan<Vector3> WorldNormals => normals;
    internal GuideBoardingPreparedPose(GuideMesh mesh, GuideMeshPoseSnapshot local, GuideBoardingFrame frame,
        float scale, double at, double receiverAt, Vector3[] vertices, Vector3[] normals, int[] indices)
    {
        Mesh = mesh; LocalPose = local; Frame = frame; Scale = scale; VerifiedAt = at; ReceiverAt = receiverAt;
        this.vertices = vertices; this.normals = normals; this.indices = indices;
    }
    public bool CanRender(GuideMesh mesh, GuideBoardingIdentity identity, float scale, double now) => ReferenceEquals(mesh, Mesh)
        && identity == Frame.Identity && scale == Scale && GuideBoardingGeometry.Fresh(VerifiedAt, now)
        && mesh.Indices.AsSpan().SequenceEqual(indices)
        && (Frame.Phase is GuideBoardingPhase.Returning or GuideBoardingPhase.Landed || GuideBoardingGeometry.Fresh(ReceiverAt, now));
    public void CopyWorldTo(Span<Vector3> destination, Span<Vector3> normalDestination)
    {
        if (destination.Length != vertices.Length || normalDestination.Length != normals.Length)
            throw new ArgumentException("Boarding render buffers must match the prepared mesh exactly.");
        vertices.CopyTo(destination); normals.CopyTo(normalDestination);
    }
}

public static class GuideBoardingPosePreparation
{
    /// <summary>
    /// Consumes already authorized authored/profile pose state; does not invent
    /// a capability, receive anchor, body occupancy proof or ground support.
    /// Convex receiver containment checks every transformed vertex, therefore
    /// also the full triangle surface, not merely the root or upright AABB.
    /// </summary>
    public static bool TryPrepare(GuideMesh mesh, GuideMeshPoseSnapshot localPose, GuideBoardingFrame frame,
        float scale, GuideBoardingReceiverVolume? receiver, GuideBoardingMeshClearance? clearance,
        double now, out GuideBoardingPreparedPose? prepared)
    {
        prepared = null;
        if (mesh is null || localPose is null || clearance is null || !frame.Identity.Valid || !frame.Capability.Valid
            || !GuideBoardingGeometry.Finite(frame.Position) || !GuideBoardingGeometry.Unit(frame.Rotation)
            || !float.IsFinite(scale) || scale is <= 0 or > 4 || !double.IsFinite(now) || now < 0
            || mesh.Vertices.Length is < 1 or > 200000 || mesh.Indices.Length is < 3 or > 60000 || mesh.Indices.Length % 3 != 0
            || frame.Trajectory is not { Valid: true } || !float.IsFinite(frame.TrajectoryTime)
            || frame.TrajectoryTime < 0 || frame.TrajectoryTime > frame.Trajectory.Duration
            || !float.IsFinite(frame.ClipTime) || frame.ClipTime < 0
            || frame.Phase is not (GuideBoardingPhase.Boarding or GuideBoardingPhase.Holding or GuideBoardingPhase.Returning or GuideBoardingPhase.Landed)) return false;
        var returning = frame.Phase is GuideBoardingPhase.Returning or GuideBoardingPhase.Landed;
        var requiredClip = returning ? frame.Capability.ReturningClip
            : frame.Phase == GuideBoardingPhase.Holding ? frame.Capability.HoldingClip : frame.Capability.BoardingClip;
        if (frame.Clip != requiredClip) return false;
        if (frame.Phase != GuideBoardingPhase.Holding)
        {
            var root = frame.Trajectory.Sample(frame.TrajectoryTime);
            if (Vector3.Distance(frame.Position, root.Position) > .0001f || !GuideBoardingGeometry.SameRotation(frame.Rotation, root.Rotation)) return false;
        }
        if (!returning && (receiver is not { } volume || !volume.Valid(frame, now))) return false;
        // The proof and draw receipt must own exactly the same topology, even
        // if a callback or another consumer changes the mutable asset arrays.
        var indices = (int[])mesh.Indices.Clone();
        foreach (var index in indices) if (index < 0 || index >= mesh.Vertices.Length) return false;
        var vertices = new Vector3[mesh.Vertices.Length]; var normals = new Vector3[vertices.Length];
        try { mesh.SampleCapturedPose(localPose, vertices, normals); }
        catch (Exception) { return false; }
        var minimum = float.PositiveInfinity;
        foreach (var vertex in vertices)
        { if (!GuideBoardingGeometry.Finite(vertex)) return false; minimum = Math.Min(minimum, vertex.Y); }
        var insideReceiver = frame.Phase == GuideBoardingPhase.Holding
            || frame.Phase == GuideBoardingPhase.Boarding && frame.Trajectory is { Valid: true } path && frame.TrajectoryTime >= path.Duration;
        for (var i = 0; i < vertices.Length; i++)
        {
            var fromFoot = (vertices[i] - Vector3.UnitY * minimum) * scale;
            var offset = Vector3.Transform(fromFoot, frame.Rotation);
            // The authorized body envelope is WORLD-upright. Pitch/roll can
            // enlarge it; measured upright asset bounds cannot authorize that.
            if (!GuideBoardingGeometry.Finite(offset) || offset.Y < -.0001f || offset.Y > frame.Capability.Height
                || new Vector2(offset.X, offset.Z).Length() > frame.Capability.Radius) return false;
            vertices[i] = frame.Position + offset;
            var normal = normals[i]; var length = normal.LengthSquared();
            if (!float.IsFinite(length)) return false;
            normals[i] = length == 0 ? Vector3.Zero : Vector3.Normalize(Vector3.Transform(normal, frame.Rotation));
            if (insideReceiver && (receiver is not { } pocket || !pocket.Contains(vertices[i]))) return false;
        }
        GuideBoardingPoseProof proof;
        try { proof = clearance(frame, vertices, indices, receiver, now); }
        catch (Exception) { return false; }
        if (proof.Evidence != GuideJumpEvidence.Clear || !GuideBoardingGeometry.Fresh(proof.SampledAt, now)
            || !returning && proof.ReceiverPoseSequence != receiver!.Value.PoseSequence
            || !mesh.Indices.AsSpan().SequenceEqual(indices)) return false;
        var receiverAt = receiver?.Anchor.SampledAt ?? double.NaN;
        if (receiver?.Avatar is { } avatar) receiverAt = Math.Min(receiverAt, avatar.SampledAt);
        prepared = new(mesh, localPose, frame, scale, proof.SampledAt, receiverAt, vertices, normals, indices);
        return true;
    }
}

/// <summary>Preflight acceptance makes a receipt drawable, not displayed. Only
/// an exact GPU-submission acknowledgment may advance the visual controller.
/// All methods must share the renderer lock with queue/draw and lifecycle reset.</summary>
public sealed class GuideBoardingRenderCache
{
    private GuideBoardingPreparedPose? proposal;
    public GuideBoardingPreparedPose? Accepted { get; private set; }
    public GuideBoardingPreparedPose? Submitted { get; private set; }
    public void Propose(GuideBoardingPreparedPose prepared) => proposal = prepared ?? throw new ArgumentNullException(nameof(prepared));
    public bool Resolve(GuideBoardingPreparedPose receipt, bool accepted)
    {
        if (!ReferenceEquals(proposal, receipt)) return false;
        if (accepted) Accepted = receipt;
        proposal = null; return true;
    }
    public bool MarkSubmitted(GuideBoardingPreparedPose receipt)
    {
        if (!ReferenceEquals(Accepted, receipt)) return false;
        Submitted = receipt; return true;
    }
    /// <summary>Atomically abandon only a never-submitted receipt. A concurrent
    /// native submission wins; the caller must acknowledge it before recovery.</summary>
    public bool CancelUnsubmitted(GuideBoardingPreparedPose receipt)
    {
        if (!ReferenceEquals(Accepted, receipt) || ReferenceEquals(Submitted, receipt)) return false;
        proposal = null; Accepted = Submitted; return true;
    }
    public void Reset() { proposal = null; Accepted = Submitted = null; }
}
