using InMemoryAws.Http;

namespace InMemoryAws.Tests.LocalAwsMessaging;

[InheritsTests]
public sealed class WireProtocolRefusalTestsLocalAwsMessaging : WireProtocolRefusalTests, IDisposable
{
    private readonly InMemoryAwsCloud _cloud = new();
    private readonly Dictionary<string, HttpClient> _clients = new(StringComparer.Ordinal);

    public WireProtocolRefusalTestsLocalAwsMessaging()
    {
        AccountId = _cloud.CurrentAccountId;
    }

    protected override HttpClient ClientFor(string service)
    {
        if (!_clients.TryGetValue(service, out var client))
        {
            var serviceType = service switch
            {
                "sqs" => AwsServiceType.Sqs,
                "sns" => AwsServiceType.Sns,
                "events" => AwsServiceType.EventBridge,
                _ => throw new ArgumentOutOfRangeException(nameof(service), service, null),
            };
#pragma warning disable CA2000 // The HttpClient owns the handler.
            client = new HttpClient(new InMemoryAwsHttpMessageHandler(_cloud, serviceType))
#pragma warning restore CA2000
            {
                BaseAddress = new Uri($"https://{service}.{_cloud.CurrentRegion}.amazonaws.com/"),
            };
            _clients[service] = client;
        }

        return client;
    }

    public void Dispose()
    {
        foreach (var client in _clients.Values)
        {
            client.Dispose();
        }
    }
}
