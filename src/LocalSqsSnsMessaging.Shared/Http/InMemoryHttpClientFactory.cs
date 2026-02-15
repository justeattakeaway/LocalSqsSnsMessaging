#if !ASPNETCORE
using Amazon.Runtime;

namespace LocalSqsSnsMessaging.Http;

/// <summary>
/// HTTP client factory that creates HttpClient instances configured with the in-memory message handler.
/// </summary>
internal sealed class InMemoryHttpClientFactory : HttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public InMemoryHttpClientFactory(HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handler = handler;
    }

    public override HttpClient CreateHttpClient(IClientConfig clientConfig)
    {
        var httpClient = new HttpClient(_handler, disposeHandler: false)
        {
            Timeout = clientConfig.Timeout ?? TimeSpan.FromSeconds(100)
        };

        return httpClient;
    }

    public override string GetConfigUniqueString(IClientConfig clientConfig)
    {
        // Return a unique identifier for this configuration
        return "InMemory";
    }

    public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig)
    {
        // We manage our own handler lifecycle
        return false;
    }

    public override bool UseSDKHttpClientCaching(IClientConfig clientConfig)
    {
        // We don't need the SDK to cache HTTP clients
        return false;
    }
}
#endif
