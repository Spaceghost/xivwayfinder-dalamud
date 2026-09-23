namespace XivWayfinder.Core;

/// <summary>
/// Conversions between world positions and the two map spaces the game uses, for a map with the <c>Map</c> sheet's
/// <c>SizeFactor</c> (100 for most field maps, 200 for cities and dungeons) and <c>OffsetX</c>/<c>OffsetY</c>:
/// <list type="bullet">
/// <item>map coordinates, what the game prints ("X: 11.2 Y: 14.5"), 1 to about 42 on a size-100 map, and</item>
/// <item>map texture pixels, 0..2048, what the <c>MapMarker</c> sheet stores (aetherytes among them).</item>
/// </list>
/// The map-coordinate formula is Dalamud's <c>MapUtil.ConvertWorldCoordXZToMapCoord</c>; the world Y axis maps to
/// neither (the map's Y is the world's Z).
/// </summary>
public static class MapCoords
{
    /// <summary>The largest map coordinate a player can type; a size-100 map ends at about 42.</summary>
    public const float MaxMapCoord = 44f;

    public static float WorldToMap(float world, ushort sizeFactor, short offset) =>
        0.02f * (world + offset) + 2048f / Scale(sizeFactor) + 1f;

    public static float MapToWorld(float map, ushort sizeFactor, short offset) =>
        (map - 1f - 2048f / Scale(sizeFactor)) / 0.02f - offset;

    /// <summary>A <c>MapMarker</c> row's pixel X or Y to world X or Z.</summary>
    public static float PixelToWorld(float pixel, ushort sizeFactor, short offset) =>
        (pixel - 1024f) / (Scale(sizeFactor) / 100f) - offset;

    /// <summary>Whether a typed map coordinate can be on any map at all.</summary>
    public static bool IsPlausibleMapCoord(float value) => float.IsFinite(value) && value > 0f && value < MaxMapCoord;

    /// <summary>Whether a world coordinate is inside any zone the game has (they stay well within ±3000).</summary>
    public static bool IsPlausibleWorldCoord(float value) => float.IsFinite(value) && MathF.Abs(value) <= 5000f;

    private static float Scale(ushort sizeFactor) => sizeFactor == 0 ? 100f : sizeFactor;
}
