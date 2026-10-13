using System.Numerics;

namespace XivWayfinder.Core;

public readonly record struct GuidePreviewAttempt(int Candidate,Vector2 Offset,Vector3 Position,string Reason);

/// <summary>Finds a nearby visible preview spot without moving the player. Collision
/// samples veto missing support and intervening surfaces; this is not a mesh sweep.</summary>
public sealed class GuidePreviewPlacement
{
    public const int MaximumQueries = 40;
    // Conservative local root separation; the player skeleton is not measured.
    public const float PlayerFootprint = .35f;
    private static readonly Vector2[] Candidates = [new(2.5f,.7f),new(-2.5f,.7f),new(1.6f,.7f),new(-1.6f,.7f),
        new(.9f,2),new(-.9f,2),new(0,2.5f),new(0,1.5f),new(.8f,.7f),new(-.8f,.7f),
        new(.7f,1),new(-.7f,1),new(0,.7f),new(0,1),
        new(0,-1.5f),new(.9f,-1.5f),new(-.9f,-1.5f),new(0,-2.5f),
        new(1.6f,-1.5f),new(-1.6f,-1.5f),new(2.5f,-.7f),new(-2.5f,-.7f)];
    private Vector3 playerAt, cameraAt, anchor, right, toward;
    private MinionPose? accepted;
    private int candidate, direction = 1;
    private float phase, lastRadius, lastHeight;
    private float lastMinimum;
    private bool lastGrounded;
    private bool exhaustedSearch;
    private double previous = double.NaN, checkedAt, retryAt;
    private long lastVisibilityRevision;
    public int Queries { get; private set; }
    public int Relocation { get; private set; }
    public static int CandidateCount=>Candidates.Length;
    public int ExhaustedCycles { get; private set; }
    public GuidePreviewAttempt? LastAttempt { get; private set; }
    public string LastRejection { get; private set; } = "";
    public string Status { get; private set; } = "Finding a visible space for the preview…";

    public void Reset()
    {
        accepted = null; candidate = 0; phase = 0; direction = 1;
        previous = double.NaN; retryAt = 0;
        exhaustedSearch=false;
    }

    public MinionPose? Update(Vector3 player, Vector3 camera, Vector3 fallbackFacing, bool grounded,
        float radius, float height, float pace, float turnDegrees, double now,
        Func<Vector3, Vector3, float, TrailSurfaceHit?> raycast, Func<Vector3, bool> visible, float bodyMinimum = 0,
        Func<Vector3,float,float,bool>? visibleBody = null,long visibilityRevision = 0,Func<string>? visibilityFailure = null)
    {
        Queries = 0;
        LastAttempt=null;LastRejection="";
        if (!Finite(player) || !Finite(camera) || !Finite(fallbackFacing) || !double.IsFinite(now)
            || !float.IsFinite(radius) || radius is <= 0 or > 10 || !float.IsFinite(height) || height is <= 0 or > 20
            || !float.IsFinite(pace) || pace < 0 || !float.IsFinite(turnDegrees) || !float.IsFinite(bodyMinimum))
        { Reset(); Status = "Waiting for valid preview bounds and camera."; return null; }
        var dt = double.IsFinite(previous) && now >= previous ? (float)Math.Clamp(now - previous, 0, .05) : 0;
        if (double.IsFinite(previous) && (now < previous || now - previous > .5
            || Vector3.DistanceSquared(player, playerAt) > .0625f || grounded != lastGrounded)) Reset();
        previous = now;
        var changedBounds = Math.Abs(radius-lastRadius) > .01f || Math.Abs(height-lastHeight) > .01f || Math.Abs(bodyMinimum-lastMinimum) > .01f;
        lastRadius = radius; lastHeight = height; lastMinimum = bodyMinimum; lastGrounded = grounded;
        var cameraMoved = Vector3.DistanceSquared(camera, cameraAt) > .01f;
        var visibilityChanged=visibilityRevision!=lastVisibilityRevision;lastVisibilityRevision=visibilityRevision;
        var facing = camera - player; facing.Y = 0;
        if (facing.LengthSquared() < .001f) { facing = fallbackFacing; facing.Y = 0; }
        facing = facing.LengthSquared() > .001f ? Vector3.Normalize(facing) : Vector3.UnitZ;
        var cameraYaw = MathF.Atan2(facing.X, facing.Z) + turnDegrees * MathF.PI / 180;

        if (accepted is { } current && (now-checkedAt >= .25 || cameraMoved || changedBounds || visibilityChanged))
        {
            if (!TryPlace(current.Position, null, out var refreshed))
            { accepted = null; candidate = 0; Status = "Finding another visible space for the preview…"; return null; }
            accepted = current with { Position = refreshed }; checkedAt = now; cameraAt = camera;
            // A complete refresh already spent this update's query allocation.
            return accepted = accepted.Value with { Yaw = pace > 0 ? accepted.Value.Yaw : cameraYaw };
        }
        if (accepted is null)
        {
            playerAt = player;
            if (now < retryAt) return null;
            toward = facing; right = new(toward.Z,0,-toward.X);
            var offset = Candidates[candidate++];
            var requested = player + right*offset.X + toward*offset.Y + Vector3.UnitY*(grounded ? 0 : 1.7f);
            if (!TryPlace(requested, null, out var position))
            {
                LastAttempt=new(candidate-1,offset,requested,LastRejection);
                if (candidate == Candidates.Length) { candidate = 0; retryAt = now+.5; ExhaustedCycles++; exhaustedSearch=true; }
                Status = exhaustedSearch ? "No clear preview spot is visible here. Try a smaller preview or turn the camera."
                    : "Looking for clear ground and a visible space nearby…"; return null;
            }
            anchor = position; phase = 0; direction = 1; checkedAt = now; cameraAt = camera; Relocation++;
            Status = "Preview placed in a clear, visible space.";
            return accepted = new(position,cameraYaw,0);
        }
        if (pace <= 0 || dt <= 0 || now < retryAt)
            return accepted = accepted.Value with { Yaw = cameraYaw };

        // Begin at the accepted spot, with no initial orbit jump. A blocked step
        // reverses along the checked path instead of snapping across an obstacle.
        const float orbit = .6f;
        var nextPhase = phase + Math.Min(pace,6)*dt/orbit*direction;
        var next = anchor + right*(MathF.Sin(nextPhase)*orbit) + toward*((1-MathF.Cos(nextPhase))*orbit);
        if (!TryPlace(next, accepted.Value.Position, out var moved))
        { direction = -direction; retryAt = now+.15; Status = "Movement preview turns back at nearby obstacles."; return accepted; }
        phase = MathF.IEEERemainder(nextPhase,MathF.Tau);
        var tangent = (right*MathF.Cos(phase)+toward*MathF.Sin(phase))*direction;
        var yaw = MathF.Atan2(tangent.X,tangent.Z)+turnDegrees*MathF.PI/180;
        checkedAt = now; cameraAt = camera; Status = "Preview placed in a clear, visible space.";
        return accepted = new(moved,yaw,0);

        bool TryPlace(Vector3 request, Vector3? from, out Vector3 placed)
        {
            placed = default;
            var expectedFloor = request - Vector3.UnitY*(grounded ? 0 : 1.7f);
            var floor = Cast(expectedFloor+Vector3.UnitY*.75f,-Vector3.UnitY,1.5f);
            if (!Support(expectedFloor,floor,out var floorAt)) return Reject(floor is { } rejectedFloor
                ? FloorReason("floor-invalid-or-steep",expectedFloor,rejectedFloor) : "floor-missing");
            var support=floor!.Value;
            var supportNormal=TrailPlacement.SurfaceNormal(support.Normal,support.A,support.B,support.C);
            placed = floorAt + Vector3.UnitY*(grounded ? 0 : 1.7f);
            var separation=radius+PlayerFootprint;
            if (Vector2.DistanceSquared(new(placed.X,placed.Z),new(player.X,player.Z))<separation*separation)
                return Reject("player-clearance");
            var body = placed+Vector3.UnitY*bodyMinimum;
            // The imported renderer can apply its existing triangle-proven
            // ground correction. Check its possible upward extent too; this
            // enlarges clearance only and never raises the requested pose.
            var checkedHeight=height+(grounded?radius*new Vector2(supportNormal.X,supportNormal.Z).Length()/supportNormal.Y:0);
            if (body.Y < floorAt.Y-.02f) return Reject("body-below-floor");
            if (!visible(body+Vector3.UnitY*.08f) || !visible(body+Vector3.UnitY*checkedHeight)) return Reject("center-offscreen");
            if (visibleBody is not null && !visibleBody(body,radius,checkedHeight)) return Reject(visibilityFailure?.Invoke()??"body-viewport-or-selector");
            // Measure each edge against the confirmed center plane. Ordinary
            // ramps are continuous support; a tread on a different plane is not.
            for (var i=0;i<4;i++)
            {
                var offset = i switch { 0=>Vector3.UnitX,1=>-Vector3.UnitX,2=>Vector3.UnitZ,_=>-Vector3.UnitZ };
                var supportAt = floorAt+offset*radius;
                supportAt.Y=PlaneY(supportAt,floorAt,supportNormal);
                var edgeHit=Cast(supportAt+Vector3.UnitY*.25f,-Vector3.UnitY,.5f);
                if (!Support(supportAt,edgeHit,out var edge) || Math.Abs(edge.Y-supportAt.Y) > .02f
                    || Vector3.Dot(supportNormal,TrailPlacement.SurfaceNormal(edgeHit!.Value.Normal,edgeHit.Value.A,edgeHit.Value.B,edgeHit.Value.C))<.995f)
                    return Reject(edgeHit is { } rejectedEdge ? FloorReason("floor-footprint",supportAt,rejectedEdge) : "floor-footprint-missing");
            }
            for (var level=0;level<3;level++)
            {
                var y = .08f + Math.Max(.01f,checkedHeight-.08f)*level/2;
                for (var spoke=0;spoke<8;spoke++)
                {
                    var angle = spoke*MathF.PI/4;
                    var rayFrom=body+Vector3.UnitY*y;
                    var rayTo=rayFrom+new Vector3(MathF.Cos(angle),0,MathF.Sin(angle))*(radius+.08f);
                    if (!ClearAboveSupport(rayFrom,rayTo)) return Reject("body-wall");
                }
            }
            if (Cast(body+Vector3.UnitY*.04f,Vector3.UnitY,checkedHeight+.08f) is not null) return Reject("ceiling");
            if (!Clear(camera,body+Vector3.UnitY*(height*.35f)) || !Clear(camera,body+Vector3.UnitY*(height*.8f))) return Reject("camera-sightline");
            if (!Clear(player+Vector3.UnitY*.3f,floorAt+Vector3.UnitY*.3f)
                || !Clear(player+Vector3.UnitY*1.2f,floorAt+Vector3.UnitY*1.2f)) return Reject("player-to-preview-wall");
            if (from is { } last)
            {
                var delta = placed-last; delta.Y = 0;
                var side = delta.LengthSquared()>.000001f ? Vector3.Normalize(new Vector3(delta.Z,0,-delta.X))*radius : Vector3.UnitX*radius;
                for (var level=0;level<2;level++)
                    for (var edge=-1;edge<=1;edge++)
                    {
                        var offset = side*edge+Vector3.UnitY*(bodyMinimum+(level==0 ? .08f : Math.Max(.08f,checkedHeight*.7f)));
                        if (!ClearAboveSupport(last+offset,placed+offset)) return Reject("movement-wall");
                    }
            }
            return true;

            bool ClearAboveSupport(Vector3 from,Vector3 to)
            {
                // A low horizontal spoke can enter the ramp before reaching a
                // real wall. Keep its endpoints above the same measured plane
                // instead of ignoring that first floor hit and hiding the wall.
                from.Y=Math.Max(from.Y,PlaneY(from,floorAt,supportNormal)+.08f);
                to.Y=Math.Max(to.Y,PlaneY(to,floorAt,supportNormal)+.08f);
                return Clear(from,to);
            }
        }
        bool Reject(string reason){LastRejection=reason;return false;}
        TrailSurfaceHit? Cast(Vector3 origin,Vector3 direction,float distance)
        {
            // All successful branches use at most 34 resting / 40 moving rays.
            // Invalid adapter data is treated as an obstruction, never free air.
            if (Queries >= MaximumQueries) return new(new(float.NaN),default,default,default,default);
            Queries++;
            var result=raycast(origin,direction,distance);
            if (result is not { } hit) return null;
            var delta=hit.Point-origin;var along=Vector3.Dot(delta,direction);
            if (!Finite(hit.Point) || along < -.002f || along > distance+.002f
                || (delta-direction*along).LengthSquared()>.0004f)
                return new(new(float.NaN),default,default,default,default);
            return hit;
        }
        bool Clear(Vector3 from,Vector3 to)
        {
            var delta=to-from;var distance=delta.Length();
            return distance<.001f || Cast(from,delta/distance,distance) is null;
        }
    }

    private static bool Support(Vector3 expected,TrailSurfaceHit? hit,out Vector3 at)
    {
        at=default;
        if (hit is not { } floor || !Finite(floor.Point)
            || Math.Abs(floor.Point.X-expected.X)>.01f || Math.Abs(floor.Point.Z-expected.Z)>.01f
            || Math.Abs(floor.Point.Y-expected.Y)>.35f
            || TrailPlacement.SurfaceNormal(floor.Normal,floor.A,floor.B,floor.C).Y<.8f) return false;
        at=floor.Point;return true;
    }
    private static bool Finite(Vector3 value)=>TrailPlacement.IsFinite(value);
    private static float PlaneY(Vector3 at,Vector3 point,Vector3 normal)=>point.Y
        -(normal.X*(at.X-point.X)+normal.Z*(at.Z-point.Z))/normal.Y;
    private static string FloorReason(string reason,Vector3 expected,TrailSurfaceHit hit)=>FormattableString.Invariant(
        $"{reason} hit={hit.Point} normal={TrailPlacement.SurfaceNormal(hit.Normal,hit.A,hit.B,hit.C)} deltaY={hit.Point.Y-expected.Y:F6}");
}
