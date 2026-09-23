namespace XivWayfinder.Shared;

/// <summary>
/// Dalamud IPC names: what XivWayfinder publishes for sibling plugins (Ghostty's coordinate links, XivMcp, anything
/// else) without an assembly reference, and the vnavmesh gates it reads when that plugin is installed. The full
/// contract, with argument rules and examples, is docs/IPC.md.
/// </summary>
public static class IpcContract
{
    /// <summary>
    /// (uint territoryId, float x, float z, string label) → bool. Point at world position (x, z) in that territory;
    /// 0 means the current one. False when the numbers are not finite or out of range. Replaces any earlier target
    /// set by IPC or command.
    /// </summary>
    public const string SetTarget = "XivWayfinder.v1.SetTarget";

    /// <summary>
    /// (uint territoryId, float mapX, float mapY, string label) → bool. The same, in the map coordinates the game
    /// prints ("X: 11.2 Y: 14.5"), converted with that territory's map.
    /// </summary>
    public const string SetMapTarget = "XivWayfinder.v1.SetMapTarget";

    /// <summary>() → bool. Forget the target set by IPC or command; true when there was one.</summary>
    public const string Clear = "XivWayfinder.v1.Clear";

    /// <summary>() → string. What XivWayfinder is pointing at, as JSON (see docs/IPC.md).</summary>
    public const string GetState = "XivWayfinder.v1.GetState";

    // vnavmesh (github.com/awgil/ffxiv_navmesh, vnavmesh/IPCProvider.cs). Only queries: XivWayfinder never calls the
    // Path.* or SimpleMove.* gates, which move the character.

    /// <summary>() → bool. A navmesh is loaded for this zone.</summary>
    public const string NavIsReady = "vnavmesh.Nav.IsReady";

    /// <summary>(Vector3 from, Vector3 to, bool fly, CancellationToken) → Task&lt;List&lt;Vector3&gt;&gt;: the path's corners.</summary>
    public const string NavPathfindCancelable = "vnavmesh.Nav.PathfindCancelable";

    /// <summary>(Vector3 p, float halfExtentXZ, float halfExtentY) → Vector3?: the nearest point on the mesh.</summary>
    public const string NavNearestPoint = "vnavmesh.Query.Mesh.NearestPoint";
}
