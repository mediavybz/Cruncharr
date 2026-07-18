using System.Net;
using Cruncharr.API.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cruncharr.Core.Tests;

public class SecurityRegressionTests
{
    [Fact]
    public async Task ImageProxy_DoesNotFollowRedirectToUntrustedHost()
    {
        var handler = new RedirectHandler();
        using var client = new HttpClient(handler);
        var controller = new ImagesController(
            NullLogger<ImagesController>.Instance,
            new StubHttpClientFactory(client));

        var result = await controller.Get(
            "https://www.crunchyroll.com/catalog/image.jpg",
            TestContext.Current.CancellationToken);

        var response = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, response.StatusCode);
        Assert.Equal(1, handler.RequestCount);
    }

    private sealed class RedirectHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("http://127.0.0.1/private") }
            });
        }
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
