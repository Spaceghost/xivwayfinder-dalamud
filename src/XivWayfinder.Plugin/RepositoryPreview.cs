using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using XivVisualAssets;

namespace XivWayfinder.Plugin;

/// <summary>One selected repository image; replaced requests cannot publish or retain GPU resources.</summary>
internal sealed class RepositoryPreview(ITextureProvider textures) : IDisposable
{
    private readonly VisualAssetThumbnailLoader loader = new();
    private CancellationTokenSource? cancellation;
    private Task<IDalamudTextureWrap>? loading;
    private IDalamudTextureWrap? image;
    private bool disposed;
    public VisualAsset? Selection { get; private set; }
    public string Status { get; private set; } = "";
    public IDalamudTextureWrap? Image => image;

    public void Select(VisualAsset candidate)
    {
        if (disposed || candidate == Selection && (loading is not null || image is not null)) return;
        Clear(); Selection = candidate;
        if (candidate.Thumbnail is not { } source) { Status = "This repository did not supply a preview image."; return; }
        cancellation = new();
        Status = "Loading repository image…";
        loading = Load(source, cancellation.Token);
    }

    private async Task<IDalamudTextureWrap> Load(Uri source, CancellationToken cancel)
    {
        var thumbnail = await loader.LoadAsync(source, cancel).ConfigureAwait(false);
        var texture = await textures.CreateFromImageAsync(thumbnail.Data, "Wayfinder repository preview", cancel).ConfigureAwait(false);
        if (cancel.IsCancellationRequested) { texture.Dispose(); cancel.ThrowIfCancellationRequested(); }
        return texture;
    }

    public void Collect()
    {
        if (disposed || loading is not { IsCompleted: true } complete) return;
        loading = null;
        if (complete.IsCompletedSuccessfully) { image = complete.Result; Status = "Repository image · preview the downloaded file to see it in your world."; }
        else { _ = complete.Exception; Status = "The repository image could not be loaded. You can still open its source page."; }
    }

    public void Clear()
    {
        cancellation?.Cancel(); cancellation?.Dispose(); cancellation = null;
        image?.Dispose(); image = null;
        if (loading is { } retiring)
            _ = retiring.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); else _ = t.Exception; },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        loading = null; Selection = null; Status = "";
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; Clear(); loader.Dispose();
    }
}
