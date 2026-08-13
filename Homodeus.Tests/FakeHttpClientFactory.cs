using System.Net.Http;

namespace Homodeus.Tests;

/// <summary>
/// Minimal test double for IHttpClientFactory that always hands back an
/// HttpClient wired to a caller-supplied responder, so tests can stub out
/// the Anthropic API without a real network call.
/// </summary>
internal sealed class FakeHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> responder) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(new FakeHttpMessageHandler(responder));

    private sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
