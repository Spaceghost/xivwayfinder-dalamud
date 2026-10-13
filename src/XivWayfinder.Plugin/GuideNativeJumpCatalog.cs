using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using XivWayfinder.Core;

namespace XivWayfinder.Plugin;

internal static class GuideNativeJumpCatalog
{
    public static GuideNativeJumpSet Read(IDataManager data, string root, int variant, int modelType)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        // Reuse the current model/variant pack discovery, including its demihuman
        // movement packs, without changing normal locomotion's independent work.
        GuideAnimationPack.ReadResident(root, variant, modelType, path =>
        {
            var bytes = data.GetFile(path)?.Data;
            if (bytes is not null) names.UnionWith(GuideAnimationPack.Names(bytes));
            return bytes;
        });
        if (names.Count == 0) return default;
        ushort Find(string key)
        {
            foreach (var timeline in data.GetExcelSheet<ActionTimeline>())
            {
                if (timeline.RowId is 0 or > ushort.MaxValue || timeline.Key.ExtractText() != key) continue;
                var bytes = data.GetFile("chara/action/" + key + ".tmb")?.Data;
                if (bytes is null) return 0;
                var references = GuideJumpAnimation.TimelineAnimationNames(bytes);
                // Match this rig's body clips; normal jumps include separate
                // optional facial overlays that do not establish body support.
                return GuideJumpAnimation.NativeBodyReferencesSupported(references, names) ? (ushort)timeline.RowId : (ushort)0;
            }
            return 0;
        }
        return new(Find("normal/jump_start"), Find("normal/jump_falling"), Find("normal/jump_landing"));
    }
}
