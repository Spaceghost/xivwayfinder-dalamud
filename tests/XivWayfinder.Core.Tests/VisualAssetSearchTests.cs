using System.Net;
using System.Text;
using XivVisualAssets;

namespace XivWayfinder.Core.Tests;

public class VisualAssetSearchTests
{
    private const string Poly = """
        {"chair":{"name":"Arm Chair","type":2,"tags":["wood","furniture"],"authors":{"Artist":"All"},
          "polycount":5626,"thumbnail_url":"https://cdn.polyhaven.com/asset_img/thumbs/chair.png"},
         "chair2":{"name":"Second Chair","type":2,"tags":["wood"]},
         "floor":{"name":"Wood floor","type":1},"sky":{"name":"Sky","type":0},
         "../../bad":{"name":"Bad","type":2},"unknown":{"name":"Unknown","type":99}}
        """;
    private const string Ambient = """
        {"totalResults":3,"nextPageHttp":"https://127.0.0.1/secret","assets":[
          {"id":"3DApple001","title":"Apple","type":"3d-model","url":"https://evil.test/asset",
           "thumbnails":{"256-PNG":"https://acg-media.struffelproductions.com/apple.png"}},
          {"id":"Wood1","title":"Wood","type":"material"}]}
        """;
    private const string Sketch = """
        {"cursors":{"next":"2"},"next":"https://127.0.0.1/secret","results":[
          {"uid":"fox1","name":"Fox","viewerUrl":"https://evil.test","isDownloadable":true,
           "animationCount":2,"faceCount":1234,"license":{"label":"CC Attribution-NonCommercial"},
           "user":{"displayName":"Artist"},"thumbnails":{"images":[
             {"width":1920,"height":1080,"url":"https://media.sketchfab.com/large.jpeg"},
             {"width":256,"height":144,"url":"https://media.sketchfab.com/small.jpeg"},
             {"width":512,"height":512,"url":"https://evil.test/image"}]}},
          {"uid":"static1","name":"Static Fox","animationCount":0,"isDownloadable":false}]}
        """;

    private sealed class Handler(params string[] payloads) : HttpMessageHandler
    {
        private readonly Queue<string> responses = new(payloads);
        public readonly List<Uri> Requests = [];
        public readonly List<string> UserAgents = [];
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Reply;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            UserAgents.Add(request.Headers.UserAgent.ToString());
            Assert.Null(request.Headers.Authorization);
            Assert.Equal(HttpMethod.Get, request.Method);
            return Reply?.Invoke(request, cancellationToken) ?? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(responses.Dequeue(), Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public void ConstructionIsOfflineAndAdvertisesHonestProviderKinds()
    {
        var handler = new Handler();
        using var search = new VisualAssetSearch(handler);
        Assert.Empty(handler.Requests);
        Assert.Equal(3, search.Providers.Count);
        Assert.Contains(VisualAssetKind.Animation, search.Providers.Single(p => p.Id == "sketchfab").SupportedKinds);
        Assert.DoesNotContain(VisualAssetKind.Animation, search.Providers.Single(p => p.Id == "polyhaven").SupportedKinds);
        Assert.Equal("Powered by Poly Haven", search.Providers.Single(p => p.Id == "polyhaven").Credit);
    }

    [Fact]
    public async Task PolyHavenLocallySearchesTagsAndCachesAcrossQueriesAndPages()
    {
        var handler = new Handler(Poly);
        using var search = new VisualAssetSearch(handler);
        var first = await search.SearchAsync("polyhaven", new("wood", PageSize: 1));
        Assert.Equal("chair", Assert.Single(first.Items).Id);
        Assert.Equal("1", first.NextPageToken);
        var second = await search.SearchAsync("polyhaven", new("wood", PageSize: 1, PageToken: first.NextPageToken));
        Assert.Equal("chair2", Assert.Single(second.Items).Id);
        Assert.Null(second.NextPageToken);
        Assert.Single((await search.SearchAsync("polyhaven", new("wood furniture"))).Items);
        Assert.Single(handler.Requests);
        Assert.Contains("t=models", handler.Requests[0].Query);
        Assert.Equal("XivVisualAssets/0.1", handler.UserAgents[0]);
        var asset = first.Items[0];
        Assert.Equal("Artist", asset.Author);
        Assert.Equal(5626, asset.FaceCount);
        Assert.Equal("CC0 1.0", asset.License.Name);
        Assert.Equal("polyhaven.com", asset.Source.Host);
        Assert.Equal(VisualAssetDownloadAccess.ProviderPage, asset.DownloadAccess);
    }

    [Fact]
    public async Task PolyHavenKindChangesRefreshTheSingleCachedCatalogue()
    {
        var handler = new Handler(Poly, Poly, Poly);
        using var search = new VisualAssetSearch(handler);
        var materials = await search.SearchAsync("polyhaven", new("", VisualAssetKind.Material));
        Assert.Equal("floor", Assert.Single(materials.Items).Id);
        Assert.Equal("floor", Assert.Single((await search.SearchAsync("polyhaven", new("", VisualAssetKind.Texture))).Items).Id);
        Assert.Equal("sky", Assert.Single((await search.SearchAsync("polyhaven", new("", VisualAssetKind.Environment))).Items).Id);
        Assert.Equal(4, (await search.SearchAsync("polyhaven", new("", VisualAssetKind.All))).Items.Count);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task AmbientUsesDocumentedV3AndRechecksTypesWithoutFollowingNextUrl()
    {
        var handler = new Handler(Ambient, """{"assets":[],"totalResults":3}""");
        using var search = new VisualAssetSearch(handler);
        var page = await search.SearchAsync("ambientcg", new("apple & fox", PageSize: 2));
        var asset = Assert.Single(page.Items);
        Assert.Equal("3DApple001", asset.Id);
        Assert.Equal("ambientcg.com", asset.Source.Host);
        Assert.Equal("CC0 1.0", asset.License.Name);
        Assert.NotNull(asset.Thumbnail);
        Assert.Equal("2", page.NextPageToken); // Advance over the raw page, including rejected type rows.
        await search.SearchAsync("ambientcg", new("apple & fox", PageSize: 2, PageToken: page.NextPageToken));
        Assert.All(handler.Requests, uri => Assert.Equal("ambientcg.com", uri.Host));
        Assert.Equal("/api/v3/assets", handler.Requests[0].AbsolutePath);
        Assert.Contains("q=apple%20%26%20fox", handler.Requests[0].Query);
        Assert.Contains("type=3d-model", handler.Requests[0].Query);
        Assert.Contains("offset=2", handler.Requests[1].Query);
    }

    [Theory]
    [InlineData(VisualAssetKind.Material, "material", VisualAssetKind.Material)]
    [InlineData(VisualAssetKind.Material, "substance", VisualAssetKind.Material)]
    [InlineData(VisualAssetKind.Texture, "plain-image", VisualAssetKind.Texture)]
    [InlineData(VisualAssetKind.Texture, "decal", VisualAssetKind.Texture)]
    [InlineData(VisualAssetKind.Environment, "hdri", VisualAssetKind.Environment)]
    public async Task AmbientSupportsReusableVisualKinds(VisualAssetKind requested, string remote, VisualAssetKind actual)
    {
        var json = "{\"assets\":[{\"id\":\"Asset1\",\"title\":\"Asset\",\"type\":\"" + remote + "\"}],\"totalResults\":1}";
        using var search = new VisualAssetSearch(new Handler(json));
        Assert.Equal(actual, Assert.Single((await search.SearchAsync("ambientcg", new("", requested))).Items).Kind);
    }

    [Fact]
    public async Task SketchfabAnimationSearchKeepsLicenseAndAuthenticationHonest()
    {
        var handler = new Handler(Sketch);
        using var search = new VisualAssetSearch(handler);
        var page = await search.SearchAsync("sketchfab", new("fox", VisualAssetKind.Animation));
        var asset = Assert.Single(page.Items);
        Assert.Equal(VisualAssetKind.Model, asset.Kind); // Embedded clips, not a standalone motion pack.
        Assert.Equal(2, asset.AnimationCount);
        Assert.Equal(1234, asset.FaceCount);
        Assert.Equal("CC Attribution-NonCommercial", asset.License.Name);
        Assert.Equal(VisualAssetDownloadAccess.ProviderSignIn, asset.DownloadAccess);
        Assert.Equal("https://sketchfab.com/3d-models/fox1", asset.Source.AbsoluteUri);
        Assert.Equal("https://media.sketchfab.com/small.jpeg", asset.Thumbnail!.AbsoluteUri);
        Assert.Contains("animated=true", handler.Requests[0].Query);
        Assert.Contains("downloadable=true", handler.Requests[0].Query);
        Assert.Contains("not retargetable", page.Notice);
        Assert.Equal("2", page.NextPageToken);
    }

    [Fact]
    public async Task SketchfabMissingLicenseAndUnavailableDownloadsDoNotGetInventedPermissions()
    {
        using var search = new VisualAssetSearch(new Handler(Sketch));
        var asset = (await search.SearchAsync("sketchfab", new("fox"))).Items[1];
        Assert.Contains("not supplied", asset.License.Name);
        Assert.Null(asset.License.Details);
        Assert.Equal(VisualAssetDownloadAccess.Unavailable, asset.DownloadAccess);
    }

    [Fact]
    public async Task SketchfabParsesActualArchiveMetadataWithoutGuessingFormatsOrRigging()
    {
        const string json = """
            {"results":[{"uid":"fox","name":"Fox","animationCount":2,"isDownloadable":true,
            "archives":{"glb":{"type":"glb","size":6800000,"faceCount":234240,"vertexCount":127084,"textureMaxResolution":0},
                "gltf":{"type":"gltf","size":4100000},"source":{"type":"source"},"invented":{"type":"glb"}}}]}
            """;
        using var search = new VisualAssetSearch(new Handler(json));
        var asset = Assert.Single((await search.SearchAsync("sketchfab", new("fox"))).Items);
        Assert.Equal(3, asset.Archives.Count);
        var glb = Assert.Single(asset.Archives, a => a.Format == "glb");
        Assert.Equal(6_800_000, glb.SizeBytes);
        Assert.Equal(234_240, glb.FaceCount);
        Assert.Equal(127_084, glb.VertexCount);
        Assert.Equal(0, glb.TextureMaxResolution);
        Assert.Null(asset.Rigged);
        // The separate GLTF archive has unknown geometry, not the GLB's
        // reported over-budget mesh. The default importer now supports both.
        var assessment = VisualAssetCompatibility.Assess(asset);
        Assert.Equal(VisualAssetFit.MetadataCandidate, assessment.Fit);
        Assert.Contains("GLTF advertised", assessment.Badges);
        Assert.Contains(assessment.Reasons, reason => reason.StartsWith("Geometry size is unknown"));
        Assert.Equal(VisualAssetFit.NeedsOptimization, VisualAssetCompatibility.Assess(asset,
            new VisualAssetImportProfile { Formats = ["glb", "obj"] }).Fit);
    }

    [Fact]
    public async Task SketchfabCursorCannotBecomeAnEndpointOrExtraQueryParameter()
    {
        var handler = new Handler(Sketch);
        using var search = new VisualAssetSearch(handler);
        await search.SearchAsync("sketchfab", new("fox", PageToken: "https://127.0.0.1/?secret=1&count=999"));
        Assert.Equal("api.sketchfab.com", handler.Requests[0].Host);
        Assert.Contains("cursor=https%3A%2F%2F127.0.0.1%2F%3Fsecret%3D1%26count%3D999", handler.Requests[0].Query);
    }

    [Fact]
    public async Task RepeatedSketchfabCursorStopsPagination()
    {
        using var search = new VisualAssetSearch(new Handler(Sketch));
        Assert.Null((await search.SearchAsync("sketchfab", new("fox", PageToken: "2"))).NextPageToken);
    }

    [Theory]
    [InlineData("polyhaven", VisualAssetKind.Animation)]
    [InlineData("ambientcg", VisualAssetKind.Animation)]
    [InlineData("sketchfab", VisualAssetKind.Material)]
    public async Task UnsupportedKindsReturnAnExplanationWithoutNetwork(string provider, VisualAssetKind kind)
    {
        var handler = new Handler();
        using var search = new VisualAssetSearch(handler);
        var page = await search.SearchAsync(provider, new("", kind));
        Assert.Empty(page.Items);
        Assert.NotNull(page.Notice);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(51)]
    [InlineData(int.MaxValue)]
    public async Task InvalidPageSizesMakeNoRequests(int count)
    {
        var handler = new Handler();
        using var search = new VisualAssetSearch(handler);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => search.SearchAsync("ambientcg", new("", PageSize: count)));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("10001")]
    [InlineData("2147483648")]
    [InlineData("http://localhost")]
    public async Task AmbientRejectsUnboundedOffsets(string token)
    {
        using var search = new VisualAssetSearch(new Handler());
        await Assert.ThrowsAsync<ArgumentException>(() => search.SearchAsync("ambientcg", new("", PageToken: token)));
    }

    [Fact]
    public async Task QueryAndCursorLimitsApplyBeforeNetwork()
    {
        using var search = new VisualAssetSearch(new Handler());
        await Assert.ThrowsAsync<ArgumentException>(() => search.SearchAsync("sketchfab", new(new string('x', 161))));
        await Assert.ThrowsAsync<ArgumentException>(() => search.SearchAsync("sketchfab", new("", PageToken: new string('x', 257))));
        await Assert.ThrowsAsync<ArgumentException>(() => search.SearchAsync("sketchfab", new("", PageToken: "bad\nnext")));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => search.SearchAsync("sketchfab", new("", (VisualAssetKind)999)));
        await Assert.ThrowsAsync<ArgumentException>(() => search.SearchAsync("untrusted", new("")));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"assets\":null}")]
    public async Task InvalidMetadataFailsVisiblyInsteadOfPretendingTheSearchIsEmpty(string json)
    {
        using var search = new VisualAssetSearch(new Handler(json));
        await Assert.ThrowsAsync<InvalidDataException>(() => search.SearchAsync("ambientcg", new("")));
    }

    [Fact]
    public async Task OversizedPageIsRejectedEvenIfJsonIsValid()
    {
        using var search = new VisualAssetSearch(new Handler(Ambient));
        await Assert.ThrowsAsync<InvalidDataException>(() => search.SearchAsync("ambientcg", new("", PageSize: 1)));
    }

    [Theory]
    [InlineData("http://cdn.polyhaven.com/a.png")]
    [InlineData("https://cdn.polyhaven.com.evil.test/a.png")]
    [InlineData("https://user:secret@cdn.polyhaven.com/a.png")]
    [InlineData("https://cdn.polyhaven.com:8443/a.png")]
    [InlineData("file:///etc/passwd")]
    public async Task UnsafeThumbnailSchemesHostsPortsAndCredentialsAreRejected(string url)
    {
        using var search = new VisualAssetSearch(new Handler(Poly.Replace("https://cdn.polyhaven.com/asset_img/thumbs/chair.png", url)));
        Assert.Null((await search.SearchAsync("polyhaven", new("Arm"))).Items[0].Thumbnail);
    }

    [Theory]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task RedirectsRateLimitsAndAuthFailuresAreNotEmptySuccess(HttpStatusCode code)
    {
        var handler = new Handler
        {
            Reply = (_, _) => Task.FromResult(new HttpResponseMessage(code)
            { Content = new StringContent("{}"), Headers = { Location = new Uri("https://127.0.0.1/") } })
        };
        using var search = new VisualAssetSearch(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => search.SearchAsync("ambientcg", new("")));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CancellationReachesTheHttpRequest()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler
        {
            Reply = async (_, cancellation) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, cancellation);
            throw new InvalidOperationException("Unreachable");
        }
        };
        using var search = new VisualAssetSearch(handler);
        using var cancel = new CancellationTokenSource();
        var pending = search.SearchAsync("ambientcg", new("fox"), cancel.Token);
        await started.Task;
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task AlreadyCancelledRequestsNeverReachProvider()
    {
        var handler = new Handler();
        using var search = new VisualAssetSearch(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search.SearchAsync("polyhaven", new(""), new CancellationToken(true)));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MetadataByteLimitAppliesWithOrWithoutContentLength(bool advertiseLength)
    {
        var bytes = new byte[AssetMetadataHttp.PageByteLimit + 1];
        var handler = new Handler
        {
            Reply = (_, _) =>
        {
            HttpContent content = advertiseLength ? new ByteArrayContent(bytes) : new StreamContent(new UnseekableStream(bytes));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
        };
        using var search = new VisualAssetSearch(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => search.SearchAsync("ambientcg", new("")));
    }

    private sealed class UnseekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}
