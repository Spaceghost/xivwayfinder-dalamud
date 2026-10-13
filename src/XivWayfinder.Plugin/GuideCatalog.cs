using Dalamud.Game;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
namespace XivWayfinder.Plugin;
internal sealed record GuideModel(string Name, uint Id, string Category, bool Grounded = true, float DefaultScale = 1.5f, uint NpcId = 0, byte ModelType = 0, uint BNpcId = 0)
{
    public string Kind => ModelType switch { 1 => "Human", 2 => "Demihuman", 3 => "Creature", 4 => "Weapon", _ => $"Native type {ModelType}" };
    public string Detail => NpcId!=0 ? $"{Category} · {Kind} · NPC appearance {NpcId} · model {Id}"
        : BNpcId!=0 ? $"{Category} · {Kind} · Battle NPC appearance {BNpcId} · model {Id}"
        : $"{Category} · {Kind} · model {Id}";
    public bool Matches(string[] terms) => terms.All(term => Name.Contains(term,StringComparison.OrdinalIgnoreCase)
        || Detail.Contains(term,StringComparison.OrdinalIgnoreCase) || NpcId!=0 && NpcId.ToString()==term
        || BNpcId!=0 && BNpcId.ToString()==term);
}
internal sealed class GuideCatalog
{
    private sealed record Result(GuideModel[] Models, string[] Issues);
    private readonly IDataManager data;
    private Task<Result> loading;
    private Result previous = new([], []);
    public GuideCatalog(IDataManager data)
    {
        this.data = data;
        loading = Start();
    }

    private Task<Result> Start()
    {
        var task = Task.Run(() =>
        {
            var items = new List<GuideModel>();
            var issues = new List<string>();
            // A missing optional sheet must not hide every other working
            // collection. Keep already-read rows if one source fails partway.
            void Read(string source, System.Action action)
            {
                try { action(); }
                catch (Exception ex) { issues.Add($"{source}: {ex.GetBaseException().Message}"); }
            }
            Lumina.Excel.ExcelSheet<ModelSkeleton>? skeletons = null;
            Read("Model sizes", () => skeletons=data.GetExcelSheet<ModelSkeleton>());
            GuideModel Describe(string name, ModelChara model,string category,uint npcId=0,uint bnpcId=0)
            {
                var skeleton=model.Type==3 ? skeletons?.GetRowOrDefault(model.Model) : null;
                var namedFloater=new[]{"moogle","cursor","airship","wisp","wind-up sun","wind-up moon","orb","levitat","flying","cloud","butterfly"}.Any(word=>name.Contains(word,StringComparison.OrdinalIgnoreCase));
                // In game data the familiar walking minions use 3, while floating companions
                // (moogle, bat, cursor, sylph, bomb, butterfly) use 8. Retain named exceptions.
                var grounded=!namedFloater && !(skeleton?.FloatHeight>=5);
                var scale=category=="Minions" ? 1.5f : skeleton is {Height: > .1f} s ? Math.Clamp(.9f/s.Height,.1f,4f) : category=="Mounts" ? .5f : 1f;
                return new(name,model.RowId,category,grounded,scale,npcId,model.Type,bnpcId);
            }
            Read("Minions", () => { foreach (var c in data.GetExcelSheet<Companion>(ClientLanguage.English))
                if (c.Model.RowId != 0 && c.Model.ValueNullable is {} model && c.Singular.ExtractText() is { Length: > 0 } name) items.Add(Describe(name,model,"Minions"));
            });
            Read("Mounts", () => { foreach (var m in data.GetExcelSheet<Mount>(ClientLanguage.English))
                if (m.ModelChara.RowId != 0 && m.ModelChara.ValueNullable is {} model && m.Singular.ExtractText() is { Length: > 0 } name) items.Add(Describe(name,model,"Mounts"));
            });
            Read("NPC appearances", () => {
            var names = data.GetExcelSheet<ENpcResident>(ClientLanguage.English);
            foreach (var npc in data.GetExcelSheet<ENpcBase>())
                if (npc.ModelChara.ValueNullable is {} npcModel && names.GetRowOrDefault(npc.RowId) is { } resident && resident.Singular.ExtractText() is { Length: > 0 } name)
                {
                    if(npcModel.Type==1 && npc.Race.RowId!=0) items.Add(Describe(name,npcModel,"Characters",npc.RowId));
                    // Demihumans can use NPC equipment to select their body
                    // parts. Dropping this identity loses the authored outfit
                    // and leaves copied player equipment on the wrong model.
                    else if(npcModel.Model>0) items.Add(Describe(name,npcModel,"Creatures",npc.RowId));
                }
            });
            // Battle-NPC names and models are separate sheets. Hunts provide an
            // explicit, reliable association; do not join unrelated row IDs.
            Read("Hunt creatures", () => { foreach (var hunt in data.GetExcelSheet<NotoriousMonster>())
                if (hunt.BNpcBase.ValueNullable is {} battle && battle.ModelChara.ValueNullable is {} huntModel
                    // Human hunts can use ModelChara zero: their authored
                    // customization determines the body, just like event NPCs.
                    && (huntModel.Type==1 ? battle.BNpcCustomize.ValueNullable is { Race.RowId: > 0 } : huntModel.Model!=0)
                    && hunt.BNpcName.ValueNullable is {} huntName
                    && huntName.Singular.ExtractText() is { Length: > 0 } name)
                    items.Add(Describe(name,huntModel,huntModel.Type==1 ? "Characters" : "Creatures",bnpcId:battle.RowId));
            });
            Read("Model IDs", () => { foreach (var m in data.GetExcelSheet<ModelChara>())
                if (m.RowId != 0 && m.Model != 0) items.Add(Describe($"Model {m.RowId} · type {m.Type} / body {m.Model}",m,"Model IDs"));
            });
            return new Result(items.DistinctBy(m => (m.Name,m.Id,m.Category,m.NpcId,m.BNpcId)).OrderBy(m=>m.Name,StringComparer.OrdinalIgnoreCase).ToArray(), issues.ToArray());
        });
        _ = task.ContinueWith(t=>{_ = t.Exception;},TaskContinuationOptions.OnlyOnFaulted);
        return task;
    }

    private Result Current => loading.IsCompletedSuccessfully ? loading.Result : previous;
    public IReadOnlyList<GuideModel> Models => Current.Models;
    public IReadOnlyList<string> Issues => loading.IsFaulted
        ? [$"Model library: {loading.Exception?.GetBaseException().Message}"] : Current.Issues;
    public bool IsLoading => !loading.IsCompleted;
    public bool CanRetry => loading.IsCompleted && (Issues.Count > 0 || Models.Count == 0);
    public void Retry()
    {
        if (!loading.IsCompleted) return;
        previous = Current;
        loading = Start();
    }
    public string Status => IsLoading ? "Reading the model library…" : Models.Count == 0
        ? "No models loaded. Retry reading the game data."
        : $"{Models.Count:N0} models from your game" + (Issues.Count > 0 ? " · Some collections could not load." : "");
}
