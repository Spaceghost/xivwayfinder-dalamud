using System.Numerics;
using XivWayfinder.Core;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
namespace XivWayfinder.Plugin;
/// <summary>Use the actual terrain hit, never the navmesh's approximate height, for ground marks.</summary>
internal sealed class TrailGround
{
    private readonly TrailSurfaceSampler sampler;
    private int traced;
    public TrailGround(Action<string>? trace = null)
    {
        sampler = new TrailSurfaceSampler((origin, direction, distance) =>
        {
            var found = BGCollisionModule.RaycastMaterialFilter(origin, direction, out var hit, distance);
            if (direction.Y == -1 && traced++ < 3) trace?.Invoke($"Ground trail: origin={origin}, hit={found}, floor={hit.Point}, normal={hit.Normal}");
            return found ? new TrailSurfaceHit(hit.Point, hit.Normal, hit.V1, hit.V2, hit.V3) : null;
        });
    }
    internal static TrailSurfaceHit? Raycast(Vector3 origin, Vector3 direction, float distance)
        => BGCollisionModule.RaycastMaterialFilter(origin, direction, out var hit, distance)
            ? new(hit.Point, hit.Normal, hit.V1, hit.V2, hit.V3) : null;
    public void Begin(uint territory, bool adaptiveSegments = false) => sampler.Begin(territory, adaptiveSegments);
    public bool TryPlace(Vector3 point, double now, out Vector3 at) => sampler.TryPlace(point, now, out at);
    public bool TryPlace(Vector3 point, double now, out Vector3 at, out Vector3 normal) => sampler.TryPlace(point, now, out at, out normal);
    public bool TryPlaceOverhead(Vector3 point, Vector3 player, float height, double now, out Vector3 at) => sampler.TryPlaceOverhead(point, player, height, now, out at);
    public bool CanConnect(Vector3 a, Vector3 b, double now) => sampler.CanConnect(a, b, now);
}
