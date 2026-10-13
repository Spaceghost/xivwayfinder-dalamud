using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using XivVisualAssets;

namespace XivWayfinder.Plugin;

/// <summary>Explicit, metadata-only searches; results are published only on the UI thread.</summary>
internal sealed class VisualAssetLibrary : Window, IDisposable
{
    private sealed record Pending(string ProviderId, Task<VisualAssetSearchPage> Work);
    private readonly VisualAssetSearch service = new();
    private readonly RepositoryPreview preview;
    private readonly List<Pending> pending = [];
    private readonly Dictionary<string, VisualAssetSearchPage> pages = new();
    private readonly Dictionary<string, string> errors = new();
    private readonly Dictionary<VisualAsset, VisualAssetCompatibilityAssessment> compatibility = new();
    private IReadOnlyList<VisualAsset> rankedResults = [];
    private bool rankingDirty = true;
    private bool rankForGuide = true;
    private CancellationTokenSource cancellation = new();
    private string query = "";
    private string activeQuery = "";
    private string providerId = "";
    private VisualAssetKind kind = VisualAssetKind.Model;
    private VisualAssetKind activeKind;
    private bool searched;
    private bool disposed;
    public Action? OpenLocalModels { get; set; }

    public VisualAssetLibrary(ITextureProvider textures) : base("Find a guide model###WayfinderVisualAssets")
    {
        preview = new(textures);
        Size = new Vector2(680, 760);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new(640, 480), MaximumSize = new(float.MaxValue) };
    }

    public void Open() { if (!disposed) IsOpen = true; }
    public void Open(string search)
    {
        if (disposed) return;
        query = search.Length <= VisualAssetSearchRequest.MaxQueryLength ? search : search[..VisualAssetSearchRequest.MaxQueryLength];
        Open(); Search();
    }
    public override void OnClose() => preview.Clear();

    public override void Draw()
    {
        if (disposed) return;
        Collect();
        preview.Collect();
        if (rankingDirty)
        {
            rankedResults = VisualAssetCompatibility.Rank(pages.Values.SelectMany(p => p.Items));
            compatibility.Clear();
            foreach (var item in rankedResults) compatibility[item] = VisualAssetCompatibility.Assess(item);
            rankingDirty = false;
        }
        ImGui.TextColored(new Vector4(.42f, .83f, .79f, 1), "FIND YOUR NEXT COMPANION");
        var searchWidth = ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize("Search").X
            - ImGui.GetStyle().FramePadding.X * 2 - ImGui.GetStyle().ItemSpacing.X;
        ImGui.SetNextItemWidth(searchWidth);
        var submit = ImGui.InputTextWithHint("##assetQuery", "Fox, carpet, stone…", ref query,
            VisualAssetSearchRequest.MaxQueryLength, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        submit |= ImGui.Button("Search");
        var halfWidth = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * .5f;
        ImGui.SetNextItemWidth(halfWidth);
        if (ImGui.BeginCombo("##repository", service.Providers.FirstOrDefault(p => p.Id == providerId)?.Name ?? "All sources"))
        {
            if (ImGui.Selectable("All sources", providerId.Length == 0)) providerId = "";
            foreach (var provider in service.Providers)
                if (ImGui.Selectable(provider.Name, providerId == provider.Id)) providerId = provider.Id;
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(halfWidth);
        if (ImGui.BeginCombo("##assetKind", Label(kind)))
        {
            foreach (var candidate in Enum.GetValues<VisualAssetKind>())
                if (ImGui.Selectable(Label(candidate), kind == candidate)) kind = candidate;
            ImGui.EndCombo();
        }
        if (submit) Search();
        ImGui.Checkbox("Best matches first", ref rankForGuide);
        ImGui.SameLine();
        if (ImGui.Button("Preview a local file…")) OpenLocalModels?.Invoke();
        if (pending.Count > 0)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Cancel")) Stop();
        }
        if (ImGui.CollapsingHeader("Search help"))
        {
            ImGui.TextWrapped("Click a result to see its image. Open its source page to download it, then choose Preview a local file to see it in your world.");
            ImGui.TextWrapped("For walking companions, look for GLB or glTF models with Idle, Walk and Run clips. Keep the unpacked model folder together and try Automatic animation.");
            ImGui.TextWrapped("Animated models include their own clips. Those clips cannot automatically be transferred to your FFXIV character.");
            ImGui.TextWrapped("Best matches first compares only the pages loaded from your selected sources. More results loads the next page for that source.");
            ImGui.TextWrapped(VisualAssetCompatibilityAssessment.VerificationNotice);
        }
        ImGui.Separator();
        var body = ImGui.GetContentRegionAvail();
        var height = Math.Max(240, body.Y);
        var resultsWidth = (body.X - ImGui.GetStyle().ItemSpacing.X) * .54f;
        if (ImGui.BeginChild("##externalAssetResults", new Vector2(resultsWidth, height), false))
        {
            if (!searched) ImGui.TextWrapped("Search for a creature, character or object to begin.");
            else
            {
                ImGui.TextDisabled($"{rankedResults.Count:N0} results" + (pending.Count > 0 ? " · searching…" : ""));
                DrawSources();
                if (rankedResults.Count == 0 && pending.Count == 0)
                    ImGui.TextWrapped("No results on these pages. Try a different name or another source.");
            }
            if (rankForGuide)
            {
                foreach (var item in rankedResults) DrawAsset(item);
            }
            else
            {
                foreach (var provider in service.Providers)
                {
                    if (!pages.TryGetValue(provider.Id, out var page) || page.Items.Count == 0) continue;
                    ImGui.Spacing();
                    ImGui.TextDisabled(provider.Name);
                    foreach (var item in page.Items) DrawAsset(item);
                }
            }
        }
        ImGui.EndChild();
        ImGui.SameLine();
        if (ImGui.BeginChild("##assetImagePreview", new Vector2(0, height), true)) DrawPreview();
        ImGui.EndChild();
    }

    private void DrawSources()
    {
        var heading = errors.Count > 0 ? $"Sources & more results · {errors.Count} issue(s)" : "Sources & more results";
        if (!ImGui.TreeNode(heading + "###assetSources")) return;
        foreach (var provider in service.Providers)
        {
            var busy = pending.Any(p => p.ProviderId == provider.Id);
            if (!pages.TryGetValue(provider.Id, out var page) && !errors.ContainsKey(provider.Id) && !busy) continue;
            ImGui.PushID(provider.Id);
            ImGui.TextWrapped(provider.Credit);
            if (busy) { ImGui.SameLine(); ImGui.TextDisabled("Searching…"); }
            else if (page?.NextPageToken is { } next)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("More results")) Queue(provider.Id, next);
            }
            else if (page?.Items.Count == 0) { ImGui.SameLine(); ImGui.TextDisabled("No matches"); }
            if (errors.TryGetValue(provider.Id, out var error)) ImGui.TextWrapped(error);
            if (page?.Notice is { Length: > 0 } notice && ImGui.TreeNode("Source notes"))
            { ImGui.TextWrapped(notice); ImGui.TreePop(); }
            ImGui.PopID();
        }
        ImGui.TreePop();
    }

    private static string Label(VisualAssetKind value) => value switch
    {
        VisualAssetKind.All => "All visual assets",
        VisualAssetKind.Model => "3D models",
        VisualAssetKind.Material => "Materials",
        VisualAssetKind.Texture => "Textures",
        VisualAssetKind.Animation => "Animated models",
        VisualAssetKind.Environment => "Environments / HDRIs",
        _ => value.ToString(),
    };

    private void DrawAsset(VisualAsset item)
    {
        ImGui.PushID(item.ProviderId);
        ImGui.PushID(item.Id);
        ImGui.Spacing();
        if (ImGui.Selectable(item.Title, preview.Selection == item)) preview.Select(item);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(item.Title);
        ImGui.TextWrapped($"{item.Author} · {item.License.Name}");
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        ImGui.TextWrapped(AssetFacts(item));
        ImGui.PopStyleColor();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.PopID();
        ImGui.PopID();
    }

    private static string AssetFacts(VisualAsset item)
    {
        var formats = item.Archives.Select(a => a.Format.ToUpperInvariant()).Distinct().Take(3).ToArray();
        var facts = new List<string> { formats.Length > 0 ? string.Join(" / ", formats) : "Format not listed" };
        if (item.AnimationCount is > 0) facts.Add(item.AnimationCount == 1 ? "1 animation" : $"{item.AnimationCount} animations");
        else if (item.AnimationCount == 0) facts.Add("No animation listed");
        if (item.FaceCount is { } faces) facts.Add($"{faces:N0} faces");
        if (item.DownloadAccess == VisualAssetDownloadAccess.Unavailable) facts.Add("Download unavailable");
        return string.Join(" · ", facts);
    }

    private void DrawCompatibility(VisualAsset item)
    {
        if (!ImGui.TreeNode("Compatibility details")) return;
        if (compatibility.TryGetValue(item, out var fit))
        {
            ImGui.TextWrapped($"{fit.Label} · score {fit.Score}/100");
            ImGui.TextWrapped(string.Join(" · ", fit.Badges));
            foreach (var reason in fit.Reasons) ImGui.TextWrapped(reason);
        }
        ImGui.TextWrapped(VisualAssetCompatibilityAssessment.VerificationNotice);
        ImGui.TreePop();
    }

    private void Search()
    {
        Stop();
        preview.Clear();
        pages.Clear(); errors.Clear(); searched = true; rankingDirty = true;
        activeQuery = query; activeKind = kind;
        foreach (var provider in service.Providers.Where(p => providerId.Length == 0 || p.Id == providerId))
        {
            if (provider.SupportedKinds.Contains(kind)) Queue(provider.Id, null);
            else errors[provider.Id] = $"This repository does not offer {Label(kind).ToLowerInvariant()} search.";
        }
    }

    private void Queue(string provider, string? pageToken)
    {
        errors.Remove(provider);
        try
        {
            pending.Add(new(provider, service.SearchAsync(provider,
                new(activeQuery, activeKind, PageSize: 20, PageToken: pageToken), cancellation.Token)));
        }
        catch (Exception) { errors[provider] = "Could not start this repository search."; }
    }

    private void Collect()
    {
        for (var i = pending.Count - 1; i >= 0; i--)
        {
            var request = pending[i];
            if (!request.Work.IsCompleted) continue;
            pending.RemoveAt(i);
            if (request.Work.IsCompletedSuccessfully)
            { pages[request.ProviderId] = request.Work.Result; rankingDirty = true; }
            else if (request.Work.IsFaulted)
            {
                _ = request.Work.Exception; // Observe every failure; no untrusted response body in the UI.
                errors[request.ProviderId] = "Repository search failed or timed out. Try again later; other repositories remain available.";
            }
            else if (request.Work.IsCanceled)
            {
                // Explicit cancel/supersession removed its requests in Stop().
                // A cancellation still present here includes HTTP timeouts.
                errors[request.ProviderId] = "Repository search timed out or was interrupted. Search again, or retry Next results page if available.";
            }
        }
    }

    private void Stop(bool renew = true)
    {
        cancellation.Cancel();
        foreach (var request in pending)
            _ = request.Work.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        pending.Clear(); // Superseded requests can never publish into a newer search.
        cancellation.Dispose();
        // Keep completed pages usable after Cancel: their Next buttons must
        // not inherit the canceled request token. Disposal needs no successor.
        if (renew) cancellation = new();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; Stop(renew: false); preview.Dispose(); service.Dispose();
    }

    private void DrawPreview()
    {
        if (preview.Selection is not { } selected)
        {
            ImGui.TextColored(new Vector4(.42f, .83f, .79f, 1), "PREVIEW");
            ImGui.Spacing();
            ImGui.TextWrapped("Choose a result to see it here.");
            ImGui.TextDisabled("Your guide stays as it is.");
            return;
        }
        ImGui.TextWrapped(selected.Title);
        ImGui.Spacing();
        if (preview.Image is { } image)
        {
            var width = Math.Max(1, ImGui.GetContentRegionAvail().X);
            var factor = Math.Min(width / image.Width, 260f / image.Height);
            ImGui.Image(image.Handle, new Vector2(image.Width, image.Height) * factor);
            ImGui.TextDisabled("Source image");
        }
        else ImGui.TextWrapped(preview.Status);
        ImGui.Spacing();
        ImGui.TextWrapped($"{selected.Author} · {selected.License.Name}");
        ImGui.TextWrapped(AssetFacts(selected));
        var provider = service.Providers.FirstOrDefault(p => p.Id == selected.ProviderId);
        if (provider is not null) ImGui.TextWrapped(provider.Credit);
        if (selected.DownloadAccess == VisualAssetDownloadAccess.ProviderSignIn)
            ImGui.TextWrapped("Sign in on the source website to download.");
        if (ImGui.Button("Open source page"))
        {
            try { Util.OpenLink(selected.Source.AbsoluteUri); }
            catch { errors[selected.ProviderId] = "Could not open your browser. Use Copy link instead."; }
        }
        ImGui.SameLine();
        if (ImGui.Button("Copy link")) ImGui.SetClipboardText(selected.Source.AbsoluteUri);
        ImGui.Spacing();
        DrawCompatibility(selected);
        if (pages.TryGetValue(selected.ProviderId, out var page) && page.Notice is { Length: > 0 } notice
            && ImGui.TreeNode("Source notes"))
        { ImGui.TextWrapped(notice); ImGui.TreePop(); }
        ImGui.Spacing();
        if (ImGui.SmallButton("Close preview")) preview.Clear();
    }
}
