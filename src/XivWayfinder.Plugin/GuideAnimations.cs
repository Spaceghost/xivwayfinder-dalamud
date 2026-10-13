using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using XivWayfinder.Core;
using System.Runtime.CompilerServices;

namespace XivWayfinder.Plugin;

internal static class GuideAnimations
{
    private static readonly ConditionalWeakTable<IDataManager, Lazy<GuideAnimationLoadTable?>> Tables = new();

    private static GuideAnimationLoadTable? Table(IDataManager data) => Tables.GetValue(data, manager =>
        new(() => GuideAnimationLoadTable.ReadOptional(() => manager.GetFile(GuideAnimationLoadTable.ResourcePath)?.Data))).Value;

    public static GuideAnimationSet ReadHuman(IDataManager data,string root,int variant=1) =>
        GuideAnimationPack.ReadResident(root, variant, 1, path => data.GetFile(path)?.Data, Table(data));
    public static GuideAnimationSet Read(IDataManager data, ModelChara model)
    {
        var (folder,prefix)=model.Type switch { 1 => ("human","c"), 2 => ("demihuman","d"), 3 => ("monster","m"), _ => ("","") };
        if(folder.Length==0) return default;
        var root=$"chara/{folder}/{prefix}{model.Model:D4}";
        return GuideAnimationPack.ReadResident(root, model.AnimationVariant, model.Type, path => data.GetFile(path)?.Data, Table(data));
    }
}
