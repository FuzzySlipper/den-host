using System.Net;
using DenHost.Clients;
using DenHost.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DenHost.Tests;

public sealed class CoreClientTests
{
    [Fact]
    public async Task RegisterAdapterBindingAsync_PreservesBaseUrlPathPrefix()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent("""
            {
              "adapter_instance_id": "den-host-local-01",
              "adapter_kind": "host",
              "host": "den-k8plus",
              "managed_roles": [],
              "managed_capabilities": [],
              "last_seen": "2026-06-08T12:00:00Z",
              "status": "active"
            }
            """)
        });
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://core.example/den-core-api/")
        };
        var client = new CoreClient(
            http,
            Options.Create(new CoreOptions
            {
                BindingPath = "/api/direct-delivery/bindings"
            }),
            NullLogger<CoreClient>.Instance);

        await client.RegisterAdapterBindingAsync(
            new AdapterBindingRequest(
                "host",
                "den-host-local-01",
                "den-k8plus",
                Array.Empty<string>(),
                Array.Empty<string>(),
                "den-host"),
            CancellationToken.None);

        Assert.Equal("http://core.example/den-core-api/api/direct-delivery/bindings/den-host-local-01", handler.RequestUri?.ToString());
    }

    [Fact]
    public async Task GetHealthAsync_PreservesBaseUrlPathPrefix()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK));
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://core.example/den-core-api/")
        };
        var client = new CoreClient(
            http,
            Options.Create(new CoreOptions
            {
                HealthPath = "/health"
            }),
            NullLogger<CoreClient>.Instance);

        var result = await client.GetHealthAsync(CancellationToken.None);

        Assert.True(result.Reachable);
        Assert.Equal("http://core.example/den-core-api/health", handler.RequestUri?.ToString());
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public RecordingHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(_response);
        }
    }

    private static StringContent JsonContent(string json) => new(json, System.Text.Encoding.UTF8, "application/json");
}
