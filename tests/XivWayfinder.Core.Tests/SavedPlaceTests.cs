using XivWayfinder.Core;

namespace XivWayfinder.Core.Tests;

public sealed class SavedPlaceTests
{
    [Fact]
    public void StoresFindsReplacesAndForgetsByFoldedName()
    {
        var places = new List<SavedPlace>();
        Assert.True(SavedPlaces.Store(places, new SavedPlace("  Café meetup  ", 132, 1, 2, 3), out var replaced, out var error));
        Assert.False(replaced);
        Assert.Empty(error);
        Assert.Equal(0, SavedPlaces.Find(places, "CAFE MEETUP!"));

        Assert.True(SavedPlaces.Store(places, new SavedPlace("cafe meetup", 148, 4, 5, 6), out replaced, out error));
        Assert.True(replaced);
        Assert.Single(places);
        Assert.Equal(148u, places[0].TerritoryId);
        Assert.True(SavedPlaces.Forget(places, "café meetup"));
        Assert.Empty(places);
    }

    [Fact]
    public void RejectsInvalidAndSanitizesPersistedPlaces()
    {
        var places = new List<SavedPlace>();
        Assert.False(SavedPlaces.Store(places, new SavedPlace("", 132, 1, 2, 3), out _, out _));
        Assert.False(SavedPlaces.Store(places, new SavedPlace("bad", 0, 1, 2, 3), out _, out _));
        Assert.False(SavedPlaces.Store(places, new SavedPlace("bad", 132, float.NaN, 2, 3), out _, out _));

        var cleaned = SavedPlaces.Sanitize([
            new SavedPlace("first", 132, 1, 2, 3),
            new SavedPlace("FIRST!", 148, 4, 5, 6),
            new SavedPlace("bad", 0, 1, 2, 3),
        ]);
        Assert.Single(cleaned);
        Assert.Equal(148u, cleaned[0].TerritoryId);
    }
}
