using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using XivWayfinder.Core;

namespace XivWayfinder.Plugin;

internal readonly record struct GuideNativeGestures(ushort Wait, ushort Beckon, float BeckonSeconds, GuideSitClips Sitting = default)
{
    public GuideGestureCapabilities Capabilities => new(Wait > 0, Sitting.Valid, Beckon > 0 && BeckonSeconds > 0);
}

internal static class GuideNativeGestureCatalog
{
    public static GuideNativeGestures Read(IDataManager data, string root, int variant, int modelType)
    {
        var resident = GuideAnimationPack.ReadResident(root, variant, modelType, path => data.GetFile(path)?.Data);
        var beckon = Verified("emote/comeon");
        // Installed Emote "Sit on Ground" selects ground_start followed by
        // emote/jmn; ground_end is its authored standing return. Each phase's
        // body references must exist in this rig's own PAP, including exit.
        var enter = Verified("event_base/event_base_ground_start");
        var loop = Verified("emote/jmn", mustLoop: true);
        var exit = Verified("event_base/event_base_ground_end");
        var sitting = enter.Id > 0 && loop.Id > 0 && exit.Id > 0
            ? new GuideSitClips(enter.Id, loop.Id, exit.Id, enter.Seconds, loop.Seconds, exit.Seconds) : default;
        return new(resident.Idle, beckon.Id, beckon.Seconds, sitting);

        (ushort Id, float Seconds) Verified(string key, bool mustLoop = false)
        {
            // Same per-rig convention as VFXEditor's EmoteTabPap. Missing
            // packs are unsupported, never replaced by human animation data.
            var pap = data.GetFile($"{root.TrimEnd('/')}/animation/a{Math.Clamp(variant, 1, ushort.MaxValue):D4}/bt_common/{key}.pap")?.Data;
            var tmb = data.GetFile("chara/action/" + key + ".tmb")?.Data;
            if (pap is null || tmb is null) return default;
            var names = GuideAnimationPack.Names(pap).ToHashSet(StringComparer.Ordinal);
            if (!GuideJumpAnimation.NativeBodyReferencesSupported(GuideJumpAnimation.TimelineAnimationNames(tmb), names)) return default;
            var seconds = GuideGestureAnimation.TimelineSeconds(tmb);
            if (seconds <= 0) return default;
            foreach (var timeline in data.GetExcelSheet<ActionTimeline>())
                if (timeline.RowId is > 0 and <= ushort.MaxValue && timeline.Key.ExtractText() == key && (!mustLoop || timeline.IsLoop))
                    return ((ushort)timeline.RowId, seconds);
            return default;
        }
    }
}
