using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using XivWayfinder.Core;
using XivWayfinder.Shared;

namespace XivWayfinder.Plugin;

/// <summary>Query-only rendezvous routing. Never calls player-movement IPC.</summary>
internal sealed class GuideApproachRouter : IDisposable
{
    private readonly ICallGateSubscriber<bool> ready;
    private readonly ICallGateSubscriber<Vector3, Vector3, bool, CancellationToken, Task<List<Vector3>>> pathfind;
    private readonly CompanionApproachCoordinator coordinator;
    public string Status => coordinator.Status;
    public bool QueryInFlight => coordinator.QueryInFlight;

    public GuideApproachRouter(IDalamudPluginInterface pluginInterface)
    {
        ready = pluginInterface.GetIpcSubscriber<bool>(IpcContract.NavIsReady);
        pathfind = pluginInterface.GetIpcSubscriber<Vector3, Vector3, bool, CancellationToken, Task<List<Vector3>>>(IpcContract.NavPathfindCancelable);
        coordinator = new(Query);
    }

    public CompanionApproachSelection Select(uint territory, Vector3 rendezvous, Vector3 player,
        CompanionRouteMotionFrame accepted, double now, bool enabled) =>
        coordinator.Select(territory, rendezvous, player, accepted, now, enabled);

    public CompanionApproachSelection SelectCorral(uint territory, Vector3 rendezvous, Vector3 player,
        CompanionRouteMotionFrame accepted, double now, bool enabled) =>
        coordinator.SelectCorral(territory, rendezvous, player, accepted, now, enabled);

    private async Task<IReadOnlyList<Vector3>> Query(Vector3 from, Vector3 to, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        if (!ready.InvokeFunc()) return Array.Empty<Vector3>();
        // Existing documented walkable path query, invoked on the framework
        // thread before its first await. No speculative floor/snapping API.
        return await pathfind.InvokeFunc(from, to, false, cancel).ConfigureAwait(false);
    }

    public void Reset() => coordinator.Reset();
    public void Dispose() => coordinator.Dispose();
}
