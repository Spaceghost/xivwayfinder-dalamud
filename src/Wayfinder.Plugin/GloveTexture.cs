using System.Numerics;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using Lumina.Data.Files;
using Wayfinder.Core;

namespace Wayfinder.Plugin;

/// <summary>
/// The game's own pointing-glove cursor, read from the player's installed game files at run time (never shipped
/// with the plugin): <see cref="Glove.TextureHrPath"/>, else <see cref="Glove.TexturePath"/>, cropped to the part
/// that <see cref="Glove.UldPath"/> names for it. Texture mods apply as they do in the game. When none of it can
/// be found the overlay draws <see cref="VectorGlove"/> instead.
/// </summary>
internal sealed class GloveTexture(ITextureProvider textures, IDataManager data, IPluginLog log)
{
    private bool resolved;
    private string? path;
    private int scale = 1;
    private (int U, int V, int W, int H) part = Glove.MeasuredPart;

    /// <summary>For the settings window: which picture the glove uses.</summary>
    public string Status { get; private set; } = "not loaded yet";

    public bool TryGet(out IDalamudTextureWrap? wrap, out Vector2 uvMin, out Vector2 uvMax)
    {
        wrap = null;
        uvMin = Vector2.Zero;
        uvMax = Vector2.One;
        if (!resolved)
            Resolve();
        if (path is null)
            return false;
        if (!textures.GetFromGame(path).TryGetWrap(out var w, out _) || w is null)
            return false;
        (uvMin, uvMax) = Glove.PartUv(part.U, part.V, part.W, part.H, w.Width, w.Height, scale);
        wrap = w;
        return true;
    }

    private void Resolve()
    {
        resolved = true;
        try
        {
            ReadPart();
            if (data.FileExists(Glove.TextureHrPath))
            {
                path = Glove.TextureHrPath;
                scale = 2;
            }
            else if (data.FileExists(Glove.TexturePath))
            {
                path = Glove.TexturePath;
                scale = 1;
            }

            Status = path is null
                ? "the game's glove cursor was not found: drawing Wayfinder's own glove"
                : $"the game's own glove: {path}, part {part.U},{part.V} {part.W}×{part.H}";
        }
        catch (Exception ex)
        {
            path = null;
            Status = "the game's glove cursor could not be read: drawing Wayfinder's own glove";
            log.Warning(ex, "Wayfinder: reading {Uld} failed", Glove.UldPath);
        }
    }

    /// <summary>The part the ULD gives the cursor texture; the measured one when the ULD cannot be read.</summary>
    private void ReadPart()
    {
        var uld = data.GetFile<UldFile>(Glove.UldPath);
        if (uld is null)
            return;
        uint? textureId = null;
        foreach (var asset in uld.AssetData)
        {
            var name = new string(asset.Path).TrimEnd('\0');
            if (name.StartsWith(Glove.TexturePath[..^4], StringComparison.OrdinalIgnoreCase))
            {
                textureId = asset.Id;
                break;
            }
        }

        if (textureId is null)
            return;
        foreach (var list in uld.Parts)
        {
            foreach (var p in list.Parts)
            {
                if (p.TextureId == textureId && p.W > 0 && p.H > 0)
                {
                    part = (p.U, p.V, p.W, p.H);
                    return;
                }
            }
        }
    }
}
