using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;
using XivWayfinder.Core;

namespace XivWayfinder.Plugin;

public sealed record ImportedGuideProfile(string Path, float Size, float Turn, bool Grounded,
    string Animation = "", bool InPlace = true, bool Animate = true, bool Leads = true);

/// <summary>A temporary world preview; configuration changes only on Use this guide.</summary>
internal sealed class LocalModelLibrary(Configuration config, Action save, ImportedGuide renderer, string archiveDirectory)
    : Window("Your model collection###WayfinderLocalModels"), IDisposable
{
    private readonly FileDialogManager files = new();
    private string pathDraft = "", search = "", notice = "";
    private bool disposed;
    private Task<GuideModelArchive>? openingPack;
    private CancellationTokenSource? packCancellation;
    private GuideModelArchive? pack;
    private string packSource = "";
    public string PreviewPath { get; private set; } = "";
    public string PreviewPlacementStatus { get; set; } = "";
    private readonly PreviewWindowBounds previewWindow = new();
    public GuidePreviewScreenRect? PreviewScreenBounds => IsOpen ? previewWindow.Read() : null;
    public float PreviewScale = 1.5f, PreviewTurn, PreviewPace;
    public bool PreviewGrounded = true, PreviewAnimate = true, PreviewInPlace = true, PreviewLeads = true;
    public string PreviewAnimation = "";
    public bool Previewing => IsOpen && PreviewPath.Length > 0;
    public Action? OpenExternalAssets { get; set; }

    public void Open()
    {
        if (disposed) return;
        previewWindow.Clear();
        IsOpen = true; Size = new Vector2(570, 680); SizeCondition = ImGuiCond.FirstUseEver;
        if (PreviewPath.Length == 0 && config.GuideModelPath.Length > 0)
            Preview(new(config.GuideModelPath, config.MinionSize, config.MinionTurn, config.GuideGrounded,
                config.GuideAnimation, config.GuideInPlace, config.GuideAnimate, config.MinionLeads));
    }

    public void PreviewFile(string path)
    {
        path = path.Trim().Trim('"').Trim();
        if (path.Length == 0) { notice = "Choose a model file to preview."; return; }
        Open();
        if (Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ReleasePack();
            PreviewPath = "";
            _ = renderer.ClearAndWaitForLoadsAsync();
            packSource = path; pathDraft = path; notice = "";
            packCancellation = new();
            openingPack = GuideModelArchive.OpenAsync(path, archiveDirectory, packCancellation.Token);
            _ = openingPack.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            return;
        }
        var remembered = config.ImportedModels.FirstOrDefault(p => p.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        Preview(remembered ?? new(path, 1.5f, 0, true));
    }

    private void Preview(ImportedGuideProfile profile)
    {
        if (pack is { } currentPack && currentPack.TryFindModel(profile.Path, out var member))
            profile = profile with { Path = member.FullPath };
        else ReleasePack();
        pathDraft = PreviewPath = profile.Path;
        PreviewScale = profile.Size; PreviewTurn = profile.Turn; PreviewGrounded = profile.Grounded;
        PreviewAnimation = profile.Animation; PreviewInPlace = profile.InPlace;
        PreviewAnimate = profile.Animate; PreviewLeads = profile.Leads; PreviewPace = 0; notice = "";
        renderer.Reload();
    }

    public void DrawFiles() { if (!disposed) files.Draw(); }

    public override void OnClose()
    {
        previewWindow.Clear();
        ReleasePack();
        PreviewPath = "";
        files.Reset();
        _ = renderer.ClearAndWaitForLoadsAsync();
    }

    private void ReleasePack()
    {
        packCancellation?.Cancel(); packCancellation?.Dispose(); packCancellation = null;
        if (openingPack is { } pending)
            _ = pending.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); else _ = t.Exception; }, TaskScheduler.Default);
        openingPack = null; packSource = "";
        if (pack is not { } previous) return;
        pack = null;
        if (previous.TryFindModel(PreviewPath, out _))
        {
            PreviewPath = "";
            // An older candidate from this same pack can still have sidecars
            // open even after a newer preview superseded it. Retire every reader.
            var readers = renderer.ClearAndWaitForLoadsAsync();
            _ = readers.ContinueWith(_ => previous.Dispose(), TaskScheduler.Default);
        }
        else previous.Dispose();
    }

    private void DrawPack()
    {
        if (openingPack is { IsCompleted: true } complete)
        {
            openingPack = null;
            packCancellation?.Dispose(); packCancellation = null;
            if (complete.IsCompletedSuccessfully) pack = complete.Result;
            else notice = complete.IsCanceled ? "Opening the model pack was canceled."
                : "Could not open model pack: " + complete.Exception?.GetBaseException().Message;
        }
        if (openingPack is not null)
        {
            ImGui.TextWrapped("Opening downloaded model pack: " + Path.GetFileName(packSource));
            if (ImGui.Button("Cancel opening pack")) { ReleasePack(); notice = "Opening the model pack was canceled."; }
        }
        if (pack is not { } current) return;
        ImGui.Separator();
        ImGui.TextWrapped(Path.GetFileName(current.SourcePath));
        ImGui.TextWrapped("Choose a model in this pack to see it beside you. Keep this window open to try another.");
        if (ImGui.BeginChild("##packModels", new Vector2(0, Math.Clamp(current.Models.Count * 24 + 12, 60, 160)), true))
            foreach (var model in current.Models)
                if (ImGui.Selectable(model.RelativePath, PreviewPath == model.FullPath) && PreviewPath != model.FullPath)
                    Preview(new(model.FullPath, 1.5f, 0, true));
        ImGui.EndChild();
        if (ImGui.Button("Close pack")) ReleasePack();
    }

    public override void Draw()
    {
        previewWindow.Capture();
        ImGui.TextColored(new Vector4(.42f, .83f, .79f, 1), "TRY A COMPANION IN YOUR WORLD");
        ImGui.TextWrapped("Preview a model beside you, try its animations, then choose Use this guide. Closing this window restores your chosen guide.");
        if (ImGui.Button("Browse model files…"))
            files.OpenFileDialog("Preview a guide model or downloaded pack", "Models and packs{.glb,.gltf,.obj,.zip}", (bool picked, string path) =>
            { if (picked && !disposed) PreviewFile(path); });
        ImGui.SameLine();
        if (ImGui.Button("Find models online…")) OpenExternalAssets?.Invoke();
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##localModelPath", "Paste a local GLB, glTF, OBJ or downloaded ZIP pack…", ref pathDraft, 2048,
            ImGuiInputTextFlags.EnterReturnsTrue)) PreviewFile(pathDraft);
        if (ImGui.Button("Preview file")) PreviewFile(pathDraft);
        ImGui.TextWrapped("Open a downloaded ZIP pack here, or keep glTF buffers and OBJ materials beside their model. Models can contain up to 20,000 triangles; unpacked packs can contain up to 32 MB of files.");
        DrawPack();
        if (config.ImportedModels.Count > 0)
        {
            ImGui.Separator();
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##savedModelSearch", "Search your saved models…", ref search, 160);
            if (ImGui.BeginChild("##savedImportedModels", new Vector2(0, 105), true))
                foreach (var profile in config.ImportedModels.Where(p => p.Path.Contains(search, StringComparison.OrdinalIgnoreCase)))
                {
                    if (ImGui.Selectable($"{Path.GetFileName(profile.Path)}##{profile.Path}", PreviewPath == profile.Path)) Preview(profile);
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(profile.Path);
                }
            ImGui.EndChild();
        }
        if (!Previewing)
        {
            if (notice.Length > 0) ImGui.TextWrapped(notice);
            return;
        }
        ImGui.Separator();
        ImGui.TextUnformatted(Path.GetFileName(PreviewPath));
        ImGui.TextWrapped(renderer.Status);
        if (PreviewPlacementStatus.Length > 0) ImGui.TextWrapped(PreviewPlacementStatus);
        ImGui.SliderFloat("Size", ref PreviewScale, GuidePreviewFit.MinimumScale, 4, "%.3f", ImGuiSliderFlags.Logarithmic);
        ImGui.SliderFloat("Facing correction", ref PreviewTurn, -180, 180, "%.0f°");
        ImGui.Checkbox("Walk on the ground", ref PreviewGrounded);
        ImGui.Checkbox("Animate", ref PreviewAnimate);
        ImGui.SameLine(); ImGui.Checkbox("Keep animation centered", ref PreviewInPlace);
        var candidateLoaded = renderer.LoadedPath == PreviewPath;
        ImGui.BeginDisabled(!candidateLoaded);
        if (ImGui.BeginCombo("Animation", PreviewAnimation.Length == 0 ? "Automatic (idle / walk / run)" : PreviewAnimation))
        {
            if (ImGui.Selectable("Automatic (idle / walk / run)", PreviewAnimation.Length == 0)) PreviewAnimation = "";
            foreach (var clip in candidateLoaded ? renderer.AnimationNames : [])
                if (ImGui.Selectable(clip, clip == PreviewAnimation)) PreviewAnimation = clip;
            ImGui.EndCombo();
        }
        ImGui.EndDisabled();
        ImGui.TextUnformatted("Movement preview");
        if (ImGui.RadioButton("Rest", PreviewPace == 0)) PreviewPace = 0;
        ImGui.SameLine(); if (ImGui.RadioButton(PreviewGrounded ? "Walk" : "Float", PreviewPace == 1.5f)) PreviewPace = 1.5f;
        ImGui.SameLine(); if (ImGui.RadioButton(PreviewGrounded ? "Run" : "Glide", PreviewPace == 4.5f)) PreviewPace = 4.5f;
        if (ImGui.RadioButton("Lead ahead", PreviewLeads)) PreviewLeads = true;
        ImGui.SameLine(); if (ImGui.RadioButton("Travel beside me", !PreviewLeads)) PreviewLeads = false;
        var ready = renderer.Available && candidateLoaded;
        ImGui.BeginDisabled(!ready);
        if (ImGui.Button("Use this guide", new Vector2(180, 34)))
        {
            if (pack is { } selectedPack && selectedPack.TryFindModel(PreviewPath, out _)) selectedPack.Retain();
            config.GuideModelPath = PreviewPath; config.GuideGrounded = PreviewGrounded;
            config.GuideAnimation = PreviewAnimation; config.GuideAnimate = PreviewAnimate; config.GuideInPlace = PreviewInPlace;
            config.MinionSize = PreviewScale; config.MinionTurn = PreviewTurn; config.MinionTilt = false;
            config.Style = PointerStyle.Minion; config.MinionWithBead = false;
            if (!PreviewLeads && config.MinionLeads) config.StandBeside();
            else config.MinionLeads = PreviewLeads;
            var profile = new ImportedGuideProfile(PreviewPath, PreviewScale, PreviewTurn, PreviewGrounded,
                PreviewAnimation, PreviewInPlace, PreviewAnimate, PreviewLeads);
            config.ImportedModels.RemoveAll(p => p.Path.Equals(PreviewPath, StringComparison.OrdinalIgnoreCase));
            config.ImportedModels.Insert(0, profile); config.Clamp(); save();
            notice = "Chosen. Keep exploring, or close this window to continue your journey.";
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Stop preview", new Vector2(150, 34))) OnClose();
        if (notice.Length > 0) ImGui.TextWrapped(notice);
        if (!config.Enabled) ImGui.TextWrapped("Turn on Show the pointer in Wayfinder settings to preview in the world.");
    }

    public void Dispose() { if (disposed) return; disposed = true; OnClose(); }
}
