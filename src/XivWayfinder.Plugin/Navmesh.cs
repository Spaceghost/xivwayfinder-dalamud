using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;
using XivWayfinder.Core;
using XivWayfinder.Shared;

namespace XivWayfinder.Plugin;

/// <summary>
/// Asks vnavmesh, when it is installed and has a mesh for the zone, for the walkable path to the target, and says
/// which corner of it to point at. Queries only: the gates that move the character (<c>vnavmesh.Path.*</c>,
/// <c>vnavmesh.SimpleMove.*</c>) are never touched. One request at a time, at most one every two seconds, and a
/// fresh one only when the target moves or the player strays from the path.
/// </summary>
internal sealed class Navmesh(IDalamudPluginInterface pluginInterface, IPluginLog log) : IDisposable
{
    private const double MinInterval = 2.0;
    private const float OffPath = 6f;
    private const float CornerReach = 1.5f;

    private readonly ICallGateSubscriber<bool> isReady = pluginInterface.GetIpcSubscriber<bool>(IpcContract.NavIsReady);
    private readonly ICallGateSubscriber<Vector3, Vector3, bool, CancellationToken, Task<List<Vector3>>> pathfind =
        pluginInterface.GetIpcSubscriber<Vector3, Vector3, bool, CancellationToken, Task<List<Vector3>>>(IpcContract.NavPathfindCancelable);
    private readonly ICallGateSubscriber<Vector3, float, float, Vector3?> nearest =
        pluginInterface.GetIpcSubscriber<Vector3, float, float, Vector3?>(IpcContract.NavNearestPoint);

    private CancellationTokenSource? cancel;
    private Task<List<Vector3>>? inFlight;
    private Vector3[] path = [];
    private Vector3 pathGoal;
    private uint territory;
    private double lastRequest = double.NegativeInfinity;
    private double lastInstallCheck = double.NegativeInfinity;
    private bool installed;
    private bool warned;

    /// <summary>For the settings window.</summary>
    public string Status { get; private set; } = "not installed";

    /// <summary>The corners of the current path, for the trail; empty when pointing in a straight line.</summary>
    public ReadOnlySpan<Vector3> Path => path;

    /// <summary>Which corner of <see cref="Path"/> the pointer aims at, or -1.</summary>
    public int Corner { get; private set; } = -1;

    /// <summary>
    /// The point to aim at this frame: the next corner of the walkable path, or null to point straight at
    /// <paramref name="goal"/> (no vnavmesh, no mesh yet, no path found, or still waiting for one).
    /// </summary>
    public Vector3? Aim(uint zone, Vector3 player, Vector3 goal, bool goalHasHeight, double now)
    {
        if (now - lastInstallCheck > 5.0)
        {
            lastInstallCheck = now;
            installed = pluginInterface.InstalledPlugins.Any(p => p.InternalName == "vnavmesh" && p.IsLoaded);
            if (!installed)
                Status = "not installed: pointing in a straight line";
        }

        if (!installed)
        {
            Idle();
            return null;
        }

        if (zone != territory)
        {
            Idle();
            territory = zone;
        }

        if (inFlight is { IsCompleted: true } done)
        {
            path = done.IsCompletedSuccessfully && done.Result is { Count: > 1 } corners ? [.. corners] : [];
            Status = path.Length > 0 ? $"following a {path.Length}-corner path" : "no path found: pointing in a straight line";
            inFlight = null;
        }

        var away = PathFollow.DistanceFromPath(path, player);
        if (PathFollow.ShouldRequery(path.Length > 0, inFlight is not null, now - lastRequest, MinInterval, pathGoal, goal, away, OffPath))
            Request(player, goal, goalHasHeight, now);

        if (path.Length == 0 || Heading.FlatDistance(pathGoal, goal) > 1f)
        {
            Corner = -1;
            return null;
        }

        Corner = PathFollow.NextCorner(path, player, CornerReach);
        return Corner >= 0 ? path[Corner] : null;
    }

    /// <summary>Forget the path (no target, another zone, navmesh switched off).</summary>
    public void Idle()
    {
        cancel?.Cancel();
        cancel?.Dispose();
        cancel = null;
        inFlight = null;
        path = [];
        Corner = -1;
    }

    private void Request(Vector3 player, Vector3 goal, bool goalHasHeight, double now)
    {
        lastRequest = now;
        try
        {
            if (!isReady.InvokeFunc())
            {
                Status = "waiting for vnavmesh to load this zone's mesh";
                return;
            }

            var to = goal;
            if (!goalHasHeight)
            {
                // a flag or a typed coordinate has no height: take the mesh's nearest floor under it
                if (nearest.InvokeFunc(new Vector3(goal.X, player.Y, goal.Z), 4f, 250f) is { } floor)
                    to = floor;
            }

            cancel?.Cancel();
            cancel?.Dispose();
            cancel = new CancellationTokenSource();
            inFlight = pathfind.InvokeFunc(player, to, false, cancel.Token);
            pathGoal = goal;
            Status = "asking vnavmesh for a path";
        }
        catch (IpcNotReadyError)
        {
            installed = false;
            Status = "vnavmesh is not answering: pointing in a straight line";
            Idle();
        }
        catch (Exception ex)
        {
            if (!warned)
                log.Warning(ex, "XivWayfinder: vnavmesh pathfinding failed; pointing in a straight line");
            warned = true;
            Status = "vnavmesh error: pointing in a straight line";
            Idle();
        }
    }

    public void Dispose() => Idle();
}
