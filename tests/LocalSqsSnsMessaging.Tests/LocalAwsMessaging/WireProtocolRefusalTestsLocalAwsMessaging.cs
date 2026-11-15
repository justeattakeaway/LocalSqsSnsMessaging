using LocalSqsSnsMessaging.Http;

namespace LocalSqsSnsMessaging.Tests.LocalAwsMessaging;

[InheritsTests]
public sealed class WireProtocolRefusalTestsLocalAwsMessaging : WireProtocolRefusalTests, IDisposable
{
    private readonly InMemoryAwsBus _bus = new();
    private readonly Dictionary<string, HttpClient> _clients = new(StringComparer.Ordinal);

    public WireProtocolRefusalTestsLocalAwsMessaging()
    {
        AccountId = _bus.CurrentAccountId;
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
            client = new HttpClient(new InMemoryAwsHttpMessageHandler(_bus, serviceType))
#pragma warning restore CA2000
            {
                BaseAddress = new Uri($"https://{service}.{_bus.CurrentRegion}.amazonaws.com/"),
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
