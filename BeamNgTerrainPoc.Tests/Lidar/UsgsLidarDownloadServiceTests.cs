using System.Net;
using System.Net.Http.Headers;
using System.Text;
using BeamNgTerrainPoc.Terrain.GeoTiff;
using BeamNgTerrainPoc.Terrain.Lidar;

namespace BeamNgTerrainPoc.Tests.Lidar;

public class UsgsLidarDownloadServiceTests
{
    private static readonly GeoBoundingBox WestonBounds = new(-104.95, 43.70, -104.70, 43.95);

    [Fact]
    public void SearchUriTargetsLpcLazWithinRequestedBounds()
    {
        var uri = UsgsLidarDownloadService.BuildSearchUri(WestonBounds, 100, 0);
        var decoded = Uri.UnescapeDataString(uri.Query);

        Assert.Equal("tnmaccess.nationalmap.gov", uri.Host);
        Assert.Contains("bbox=-104.95,43.7,-104.7,43.95", decoded);
        Assert.Contains("datasets=Lidar Point Cloud (LPC)", decoded);
        Assert.Contains("prodFormats=LAS,LAZ", decoded);
        Assert.Contains("max=100", decoded);
        Assert.Contains("offset=0", decoded);
    }

    [Fact]
    public void SquareBoundsMatchRequestedGroundDimensions()
    {
        var bounds = UsgsLidarDownloadService.BuildSquareBounds(43.8, -104.85, 16_384);

        Assert.InRange(bounds.ApproximateWidthMeters, 16_300, 16_470);
        Assert.InRange(bounds.ApproximateHeightMeters, 16_300, 16_470);
        Assert.Equal(43.8, bounds.Center.Latitude, 6);
        Assert.Equal(-104.85, bounds.Center.Longitude, 6);
    }

    [Fact]
    public async Task SearchRetriesGatewayTimeoutAndUsesConservativePages()
    {
        var handler = new SequenceHandler(
            () => new HttpResponseMessage(HttpStatusCode.GatewayTimeout),
            () => JsonResponse(SingleProductSearchJson));
        using var client = new HttpClient(handler);
        using var service = new UsgsLidarDownloadService(client, (_, _) => Task.CompletedTask);
        var progress = new CollectingProgress<string>();

        var result = await service.SearchAsync(WestonBounds, progress);

        var product = Assert.Single(result.Products);
        Assert.Equal("inside", product.SourceId);
        Assert.Equal(2, handler.RequestCount);
        Assert.All(handler.RequestUris, uri =>
        {
            var decoded = Uri.UnescapeDataString(uri.Query);
            Assert.Contains("prodFormats=LAS,LAZ", decoded);
            Assert.Contains("max=100", decoded);
        });
        Assert.Contains(progress.Values, message => message.Contains("504", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SearchRetriesHttp200CatalogErrorPayload()
    {
        var handler = new SequenceHandler(
            () => JsonResponse("""{"error":"ScienceBase inventory is busy"}"""),
            () => JsonResponse(SingleProductSearchJson));
        using var client = new HttpClient(handler);
        using var service = new UsgsLidarDownloadService(client, (_, _) => Task.CompletedTask);

        var result = await service.SearchAsync(WestonBounds);

        Assert.Single(result.Products);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task SearchDoesNotRetryNonTransientClientError()
    {
        var handler = new SequenceHandler(
            () => new HttpResponseMessage(HttpStatusCode.BadRequest));
        using var client = new HttpClient(handler);
        using var service = new UsgsLidarDownloadService(client, (_, _) => Task.CompletedTask);

        await Assert.ThrowsAsync<HttpRequestException>(() => service.SearchAsync(WestonBounds));

        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public void SearchResponseKeepsIntersectingHttpsLazProducts()
    {
        const string json = """
                            {
                              "total": 3,
                              "items": [
                                {
                                  "sourceId": "inside",
                                  "title": "USGS LAZ inside",
                                  "sizeInBytes": 300,
                                  "downloadLazURL": "https://example.gov/inside.laz",
                                  "publicationDate": "2025-01-02",
                                  "boundingBox": { "minX": -104.90, "minY": 43.75, "maxX": -104.80, "maxY": 43.85 }
                                },
                                {
                                  "sourceId": "outside",
                                  "title": "outside",
                                  "downloadLazURL": "https://example.gov/outside.laz",
                                  "boundingBox": { "minX": -100, "minY": 40, "maxX": -99, "maxY": 41 }
                                },
                                {
                                  "sourceId": "insecure",
                                  "title": "insecure",
                                  "downloadLazURL": "http://example.gov/insecure.laz",
                                  "boundingBox": { "minX": -104.90, "minY": 43.75, "maxX": -104.80, "maxY": 43.85 }
                                }
                              ]
                            }
                            """;

        var page = UsgsLidarDownloadService.ParseSearchResponse(json, WestonBounds);

        Assert.Equal(3, page.TotalAvailable);
        Assert.Equal(3, page.RawItemCount);
        var product = Assert.Single(page.Products);
        Assert.Equal("inside", product.SourceId);
        Assert.Equal(300, product.SizeInBytes);
    }

    [Fact]
    public async Task CompletedLazFileIsReusedFromPersistentCache()
    {
        var payload = new byte[300];
        Encoding.ASCII.GetBytes("LASF").CopyTo(payload, 0);
        var handler = new CountingHandler(payload);
        using var client = new HttpClient(handler);
        using var service = new UsgsLidarDownloadService(client);
        var cache = Path.Combine(Path.GetTempPath(), "BeamNgUsgsTest", Guid.NewGuid().ToString("N"));
        var product = new UsgsLidarProduct(
            "test",
            "test tile",
            new Uri("https://example.gov/tile.laz"),
            payload.Length,
            WestonBounds,
            null);

        try
        {
            var first = await service.DownloadAsync([product], cache);
            var second = await service.DownloadAsync([product], cache);

            Assert.Equal(1, handler.RequestCount);
            Assert.Equal(1, first.DownloadedFiles);
            Assert.Equal(1, second.ReusedFiles);
            Assert.Equal(first.FilePaths, second.FilePaths);
        }
        finally
        {
            if (Directory.Exists(cache)) Directory.Delete(cache, true);
        }
    }

    [Fact]
    public async Task IncompleteDownloadAutomaticallyResumesOnNextAttempt()
    {
        var payload = new byte[300];
        Encoding.ASCII.GetBytes("LASF").CopyTo(payload, 0);
        var handler = new ResumableDownloadHandler(payload, 150);
        using var client = new HttpClient(handler);
        using var service = new UsgsLidarDownloadService(client, (_, _) => Task.CompletedTask);
        var cache = Path.Combine(Path.GetTempPath(), "BeamNgUsgsTest", Guid.NewGuid().ToString("N"));
        var product = new UsgsLidarProduct(
            "resume-test",
            "resume test tile",
            new Uri("https://example.gov/resume.laz"),
            payload.Length,
            WestonBounds,
            null);

        try
        {
            var result = await service.DownloadAsync([product], cache);

            Assert.Equal(1, result.DownloadedFiles);
            Assert.Equal(2, handler.RequestCount);
            Assert.Equal(150, handler.SecondRequestRangeStart);
            Assert.Equal(payload, await File.ReadAllBytesAsync(Assert.Single(result.FilePaths)));
            Assert.Empty(Directory.GetFiles(cache, "*.partial"));
        }
        finally
        {
            if (Directory.Exists(cache)) Directory.Delete(cache, true);
        }
    }

    private sealed class CountingHandler(byte[] payload) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            });
        }
    }

    private const string SingleProductSearchJson = """
        {
          "total": 1,
          "items": [
            {
              "sourceId": "inside",
              "title": "USGS LAZ inside",
              "sizeInBytes": 300,
              "downloadLazURL": "https://example.gov/inside.laz",
              "publicationDate": "2025-01-02",
              "boundingBox": { "minX": -104.90, "minY": 43.75, "maxX": -104.80, "maxY": 43.85 }
            }
          ]
        }
        """;

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class SequenceHandler(params Func<HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private int _index;
        public int RequestCount { get; private set; }
        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            RequestUris.Add(request.RequestUri!);
            var response = responses[Math.Min(_index, responses.Length - 1)]();
            _index++;
            return Task.FromResult(response);
        }
    }

    private sealed class ResumableDownloadHandler(byte[] payload, int firstResponseLength) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public long? SecondRequestRangeStart { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            if (RequestCount == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(payload[..firstResponseLength])
                });
            }

            SecondRequestRangeStart = request.Headers.Range?.Ranges.Single().From;
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(payload[firstResponseLength..])
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                firstResponseLength,
                payload.Length - 1,
                payload.Length);
            return Task.FromResult(response);
        }
    }

    private sealed class CollectingProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];
        public void Report(T value) => Values.Add(value);
    }
}
