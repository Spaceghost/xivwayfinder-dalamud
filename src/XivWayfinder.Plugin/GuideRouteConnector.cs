using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using XivWayfinder.Core;
using XivWayfinder.Shared;

namespace XivWayfinder.Plugin;

/// <summary>A companion-only query client. Never calls vnavmesh Path.* or SimpleMove.* movement IPC.</summary>
internal sealed class GuideRouteConnector : IDisposable
{
    private readonly ICallGateSubscriber<bool> ready;
    private readonly ICallGateSubscriber<Vector3, Vector3, bool, CancellationToken, Task<List<Vector3>>> pathfind;
    private readonly GuideConnectorCoordinator coordinator;
    public string Status => coordinator.Status;

    public GuideRouteConnector(IDalamudPluginInterface pluginInterface)
    {
        ready = pluginInterface.GetIpcSubscriber<bool>(IpcContract.NavIsReady);
        pathfind = pluginInterface.GetIpcSubscriber<Vector3, Vector3, bool, CancellationToken, Task<List<Vector3>>>(IpcContract.NavPathfindCancelable);
        coordinator = new(Query);
    }

    public GuideRouteSelection Select(uint territory, Vector3[] mainRoute, float playerArc,
        CompanionIntentFrame intent, CompanionRouteMotionFrame motion, double now, bool hasRoute) =>
        coordinator.Select(territory, mainRoute, playerArc, intent, motion, now, hasRoute);

    private async Task<IReadOnlyList<Vector3>> Query(Vector3 from, Vector3 to, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        if (!ready.InvokeFunc()) return Array.Empty<Vector3>();
        // InvokeFunc is called on the framework thread before the first await.
        return await pathfind.InvokeFunc(from, to, false, cancel).ConfigureAwait(false);
    }

    public void Reset() => coordinator.Reset();
    public void Dispose() => coordinator.Dispose();
}
