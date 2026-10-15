using System.Net;
using Cruncharr.API.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cruncharr.Core.Tests;

// Catalog posters were proxied as full-quality PNGs (~400 KB each, ~20 MB per Browse screen).
// They are now fetched as WebP from Crunchyroll's resizing CDN, with the original as fallback.
[Collection("ConfigurationEnvironment")]
public class ImageProxyTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("cruncharr-images-");
    private readonly string? _previous = Environment.GetEnvironmentVariable("CRUNCHYROLL_CONFIG_PATH");
    private readonly List<string> _requested = [];

    public ImageProxyTests() =>
        Environment.SetEnvironmentVariable("CRUNCHYROLL_CONFIG_PATH", Path.Combine(_root.FullName, "cruncharr.yaml"));

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CRUNCHYROLL_CONFIG_PATH", _previous);
        try { _root.Delete(recursive: true); } catch { }
    }

    [Fact]
    public async Task CatalogThumbnail_IsFetchedAsCompressedWebP()
    {
        var url = Thumbnail();
        var result = await Controller(cdnWorks: true).Get(url, CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("image/webp", file.ContentType);
        var fetched = Assert.Single(_requested);
        Assert.StartsWith("https://imgsrv.crunchyroll.com/cdn-cgi/image/fit=contain,format=webp,quality=85,width=480/catalog/crunchyroll/", fetched);
    }

    [Fact]
    public async Task CdnFailure_FallsBackToTheOriginalImage()
    {
        var url = Thumbnail();
        var result = await Controller(cdnWorks: false).Get(url, CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("image/png", file.ContentType);
        Assert.Equal(2, _requested.Count);
        Assert.Equal(url, _requested[1]);
    }

    [Fact]
    public async Task CdnNetworkFailure_FallsBackToTheOriginalImage()
    {
        var url = Thumbnail();
        var result = await Controller(cdnWorks: false, cdnThrows: true).Get(url, TestContext.Current.CancellationToken);

        Assert.Equal("image/png", Assert.IsType<FileContentResult>(result).ContentType);
        Assert.Equal(2, _requested.Count);
        Assert.Equal(url, _requested[1]);
    }

    [Fact]
    public async Task CdnFailure_ReusesTheCachedOriginal()
    {
        var url = Thumbnail();
        await Controller(cdnWorks: false).Get(url, TestContext.Current.CancellationToken);
        _requested.Clear();

        var result = await Controller(cdnWorks: false).Get(url, TestContext.Current.CancellationToken);

        Assert.Equal("image/png", Assert.IsType<PhysicalFileResult>(result).ContentType);
        Assert.Contains("/cdn-cgi/image/", Assert.Single(_requested));
    }

    [Fact]
    public async Task OtherImages_AreFetchedUnchanged()
    {
        var url = $"https://static.crunchyroll.com/assets/avatar/170x170/{Guid.NewGuid():N}.png";
        var result = await Controller(cdnWorks: true).Get(url, CancellationToken.None);

        Assert.IsType<FileContentResult>(result);
        Assert.Equal(url, Assert.Single(_requested));
    }

    private static string Thumbnail() =>
        $"https://www.crunchyroll.com/imgsrv/display/thumbnail/480x720/catalog/crunchyroll/{Guid.NewGuid():N}.png";

    private ImagesController Controller(bool cdnWorks, bool cdnThrows = false) =>
        new(NullLogger<ImagesController>.Instance, new TestHttpClientFactory(new HttpClient(new Handler(request =>
        {
            var target = request.RequestUri!.AbsoluteUri;
            _requested.Add(target);
            if (target.Contains("/cdn-cgi/image/"))
            {
                if (cdnThrows) throw new HttpRequestException("CDN unavailable");
                // Like the real CDN: WebP only when the request accepts it, otherwise JPEG.
                if (!cdnWorks) return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("<html/>") };
                return Image(request.Headers.Accept.Any(a => a.MediaType == "image/webp") ? "image/webp" : "image/jpeg");
            }
            return Image("image/png");
        }))))
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

    private static HttpResponseMessage Image(string type)
    {
        var content = new ByteArrayContent([1, 2, 3, 4]);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
