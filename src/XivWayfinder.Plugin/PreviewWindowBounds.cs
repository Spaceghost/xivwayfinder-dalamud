using System.Diagnostics;
using Dalamud.Bindings.ImGui;
using XivWayfinder.Core;

namespace XivWayfinder.Plugin;

/// <summary>Only Capture reads ImGui, on Draw. Framework readers receive one
/// complete immutable snapshot, never a partly updated position and size.</summary>
internal sealed class PreviewWindowBounds
{
    private sealed record Snapshot(GuidePreviewScreenRect Bounds,long At);
    private Snapshot? snapshot;
    public void Clear()=>Volatile.Write(ref snapshot,null);
    public void Capture()
    {
        var position=ImGui.GetWindowPos();var size=ImGui.GetWindowSize();
        var bounds=new GuidePreviewScreenRect(position,position+size);
        Volatile.Write(ref snapshot,bounds.Valid?new(bounds,Stopwatch.GetTimestamp()):null);
    }
    public GuidePreviewScreenRect? Read()
    {
        var current=Volatile.Read(ref snapshot);
        return current is not null && Stopwatch.GetElapsedTime(current.At).TotalSeconds<=2?current.Bounds:null;
    }
}
