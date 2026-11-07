using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using XivWayfinder.Core;
namespace XivWayfinder.Plugin;
internal sealed class ModelLibrary(Configuration config, Action save, GuideCatalog catalog, Func<string> status) : Window("Guide library###WayfinderModelLibrary")
{
    public Action? OpenExternalAssets { get; set; }
    public Action? OpenLocalModels { get; set; }
    public Func<GuideModel, bool>? CanUsePreview { get; set; }
    public string PreviewPlacementStatus { get; set; } = "";
    public bool PreviewPlaced { get; set; }
    private readonly PreviewWindowBounds previewWindow = new();
    public GuidePreviewScreenRect? PreviewScreenBounds => IsOpen ? previewWindow.Read() : null;
    private string search = "";
    private string category = "All";
    private int page;
    private IReadOnlyList<GuideModel>? searchedCatalog;
    private string searchedText="",searchedCategory="";
    private GuideModel[] matches=[];
    public GuideModel? Preview { get; private set; }
    public float PreviewScale = 1.5f;
    public float PreviewTurn;
    public bool PreviewGrounded;
    public float PreviewPace;
    private bool previewLeads = true;
    private bool fitPending;
    public bool NeedsPreviewFit => IsOpen && Preview is not null && fitPending;
    private static readonly Vector4 Accent = new(.42f,.83f,.79f,1);
    public void Open() { previewWindow.Clear(); IsOpen = true; Size = new Vector2(580,760); SizeCondition = ImGuiCond.FirstUseEver; }
    public void Open(string? query)
    {
        if (query is not null) { search = query.Trim(); page = 0; }
        Open();
    }
    public void StopPreview() { previewWindow.Clear(); Preview = null; PreviewPace = 0; fitPending = false; }
    public override void OnClose() => previewWindow.Clear();
    public void FitPreview(Vector3 worldSize)
    {
        if (!IsOpen || Preview is null || !fitPending
            || !GuidePreviewFit.TryFit(PreviewScale, worldSize, out var fitted)) return;
        PreviewScale = fitted;
        fitPending = false;
    }
    private (Dictionary<uint, GuideModelProfile> Profiles, uint Key) ProfileFor(GuideModel model) =>
        model.NpcId != 0 ? (config.NpcProfiles, model.NpcId)
        : model.BNpcId != 0 ? (config.BNpcProfiles, model.BNpcId) : (config.ModelProfiles, model.Id);

    private bool IsChosen(GuideModel model) => config.GuideModelPath.Length == 0 && config.GuideModelChara == model.Id
        && config.GuideNpcId == model.NpcId && config.GuideBNpcId == model.BNpcId;

    private void Select(GuideModel model)
    {
        Preview = model; PreviewGrounded = model.Grounded; previewLeads = config.MinionLeads;
        PreviewPlaced = false; PreviewPlacementStatus = "Finding a visible space for the preview…";
        PreviewScale = model.DefaultScale; PreviewTurn = 0; PreviewPace = 0;
        fitPending = true;
        var (profiles, key) = ProfileFor(model);
        if (profiles.TryGetValue(key,out var profile) && profile is not null)
        { PreviewScale=float.IsFinite(profile.Size)?Math.Clamp(profile.Size,GuidePreviewFit.MinimumScale,4):1.5f;PreviewTurn=float.IsFinite(profile.Turn)?Math.Clamp(profile.Turn,-180,180):0;PreviewGrounded=profile.Grounded;previewLeads=profile.Leads;fitPending=false; }
        else if (IsChosen(model))
        {
            // A guide chosen by command can have saved settings without a
            // library profile. Its current size is already the user's choice.
            PreviewScale = config.MinionSize; PreviewTurn = config.MinionTurn;
            PreviewGrounded = config.GuideGrounded; fitPending = false;
        }
    }
    public override void Draw()
    {
        previewWindow.Capture();
        ImGui.TextColored(Accent,"FIND YOUR TRAVELLING COMPANION");
        ImGui.TextWrapped("Click a model to preview it beside you. Keep browsing; the window stays open. Choose Use this guide when you find the one.");
        if (ImGui.Button("Search external model repositories…")) OpenExternalAssets?.Invoke();
        if (OpenLocalModels is not null)
        {
            ImGui.SameLine();
            if (ImGui.Button("Preview local models…")) OpenLocalModels();
        }
        ImGui.Spacing();
        foreach (var (title, match) in new[]{("Pointer","wind-up cursor"),("Moogle","wind-up moogle"),("Chocobo","black chocobo chick")})
        {
            if (ImGui.Button(title,new Vector2(150,34)) && catalog.Models.FirstOrDefault(m=>m.Name.Equals(match,StringComparison.OrdinalIgnoreCase)) is {} model) Select(model);
            ImGui.SameLine();
        }
        ImGui.NewLine(); ImGui.Separator();
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##modelSearch","Search names or model IDs…",ref search,160)) page=0;
        ImGui.SetNextItemWidth(185);
        if(ImGui.BeginCombo("Collection",category))
        {
            foreach (var c in new[]{"All","Minions","Mounts","Creatures","Characters","Model IDs"})
                if (ImGui.Selectable(c,category==c)) {category=c;page=0;}
            ImGui.EndCombo();
        }
        var allModels=catalog.Models;
        if(!ReferenceEquals(allModels,searchedCatalog) || searchedText!=search || searchedCategory!=category)
        {
            searchedCatalog=allModels;searchedText=search;searchedCategory=category;
            var terms=search.Split(' ',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
            matches=allModels.Where(m=>(category=="All" || m.Category==category) && m.Matches(terms)).ToArray();
        }
        const int perPage=100;
        page=Math.Clamp(page,0,Math.Max(0,(matches.Length-1)/perPage));
        if (ImGui.BeginChild("##modelResults",new Vector2(0,Preview is null ? 220 : 140),true))
        {
            foreach(var m in matches.Skip(page*perPage).Take(perPage))
            {
                if(ImGui.Selectable($"{m.Name}##{m.Category}:{m.Id}:{m.NpcId}:{m.BNpcId}",Preview==m)) Select(m);
                if(ImGui.IsItemHovered()) ImGui.SetTooltip(m.Detail);
            }
        }
        ImGui.EndChild();
        ImGui.TextDisabled($"{matches.Length:N0} matches · {catalog.Status}");
        if (catalog.CanRetry && ImGui.Button("Retry reading model library")) catalog.Retry();
        if (catalog.Issues.Count > 0 && ImGui.TreeNode("Collections that could not load"))
        {
            foreach (var issue in catalog.Issues) ImGui.TextWrapped(issue);
            ImGui.TreePop();
        }
        if(matches.Length>perPage)
        {
            if(ImGui.Button("Previous") && page>0) page--;
            ImGui.SameLine(); ImGui.TextUnformatted($"Page {page+1} / {(matches.Length+perPage-1)/perPage}"); ImGui.SameLine();
            if(ImGui.Button("Next") && (page+1)*perPage<matches.Length) page++;
        }
        ImGui.Separator();
        if (Preview is { } selected)
        {
            ImGui.TextColored(Accent,selected.Name);
            ImGui.TextDisabled(selected.Detail);
            if (selected.ModelType==1 && selected.NpcId==0 && selected.BNpcId==0)
                ImGui.TextWrapped("This raw human model uses your character's appearance. Choose Characters for a named NPC's own appearance.");
            else if (selected.ModelType==2 && selected.NpcId==0 && selected.BNpcId==0)
            {
                ImGui.TextWrapped("Demihuman bodies can require their own equipment. Choose an authored NPC appearance if this raw model is missing parts.");
                if (ImGui.BeginCombo("Authored appearance", "Choose a character using this model…"))
                {
                    var found = false;
                    foreach (var appearance in allModels.Where(m => m.Id == selected.Id && (m.NpcId != 0 || m.BNpcId != 0)))
                    {
                        found = true;
                        var identity = appearance.NpcId != 0 ? $"NPC {appearance.NpcId}" : $"Battle NPC {appearance.BNpcId}";
                        if (ImGui.Selectable($"{appearance.Name} · {identity}")) Select(appearance);
                    }
                    if (!found) ImGui.TextDisabled("No named NPC appearance found for this model.");
                    ImGui.EndCombo();
                }
            }
            else if (selected.ModelType is not (1 or 2 or 3))
                ImGui.TextWrapped("This native model type is experimental. Check its preview before choosing it.");
            ImGui.TextWrapped(status());
            if (PreviewPlacementStatus.Length > 0) ImGui.TextWrapped(PreviewPlacementStatus);
            if (ImGui.SliderFloat("Preview size",ref PreviewScale,GuidePreviewFit.MinimumScale,4f,"%.3f",ImGuiSliderFlags.Logarithmic))
                fitPending = false;
            if (fitPending) ImGui.TextDisabled("Fitting the new model to a comfortable companion size…");
            ImGui.SliderFloat("Preview rotation",ref PreviewTurn,-180,180,"%.0f°");
            ImGui.Checkbox("Walk on the ground",ref PreviewGrounded);
            ImGui.TextUnformatted("Movement preview");
            if(ImGui.RadioButton("At rest",PreviewPace==0)) PreviewPace=0;
            ImGui.SameLine(); if(ImGui.RadioButton(PreviewGrounded ? "Walk" : "Float",PreviewPace==1.5f)) PreviewPace=1.5f;
            ImGui.SameLine(); if(ImGui.RadioButton(PreviewGrounded ? "Run" : "Glide",PreviewPace==4.5f)) PreviewPace=4.5f;
            if (ImGui.RadioButton("Lead ahead", previewLeads)) previewLeads=true;
            ImGui.SameLine(); if(ImGui.RadioButton("Travel beside me", !previewLeads)) previewLeads=false;
            var ready = !fitPending && CanUsePreview?.Invoke(selected) == true;
            ImGui.BeginDisabled(!ready);
            if(ImGui.Button("Use this guide",new Vector2(190,34)))
            {
                config.GuideModelChara=selected.Id; config.GuideNpcId=selected.NpcId; config.GuideBNpcId=selected.BNpcId; config.GuideModelPath="";config.GuideGrounded=PreviewGrounded;
                config.MinionSize=PreviewScale;config.MinionTurn=PreviewTurn;config.MinionTilt=false;
                config.MinionHeight=2;config.MinionWithBead=false;
                if (!previewLeads && config.MinionLeads) config.StandBeside();
                else config.MinionLeads=previewLeads;
                config.Style=XivWayfinder.Core.PointerStyle.Minion;
                var (profiles, key) = ProfileFor(selected);
                profiles[key]=new(PreviewScale,PreviewTurn,PreviewGrounded,previewLeads);
                save();
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            if(ImGui.Button("Stop preview",new Vector2(150,34))) StopPreview();
            if (!ready) ImGui.TextWrapped("Choose this guide after its preview appears in the world. Show the pointer must be enabled.");
            ImGui.TextDisabled(IsChosen(selected) ? "This is your chosen guide. Size, rotation and movement are remembered." : "Closing this window restores your chosen guide.");
        }
        else ImGui.TextWrapped("Select any model above to see it in the world. Your current destination stays intact.");
    }
}
