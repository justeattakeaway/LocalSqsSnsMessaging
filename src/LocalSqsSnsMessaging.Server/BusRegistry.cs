using System.Collections.Concurrent;

namespace LocalSqsSnsMessaging.Server;

internal sealed class BusRegistry
{
    private readonly ConcurrentDictionary<string, InMemoryAwsBus> _buses = new();
    private readonly string _region;
    private readonly Uri _serviceUrl;

    public string DefaultAccountId { get; }

    public BusRegistry(string defaultAccountId, string region, Uri serviceUrl)
    {
        DefaultAccountId = defaultAccountId;
        _region = region;
        _serviceUrl = serviceUrl;

        _buses[defaultAccountId] = CreateBus(defaultAccountId);
    }

    public InMemoryAwsBus GetOrCreate(string accountId)
    {
        return _buses.GetOrAdd(accountId, CreateBus);
    }

    public InMemoryAwsBus DefaultBus => _buses[DefaultAccountId];

    public List<string> AccountIds => _buses.Keys.Order().ToList();

    private InMemoryAwsBus CreateBus(string accountId)
    {
        return new InMemoryAwsBus
        {
            CurrentRegion = _region,
            CurrentAccountId = accountId,
            ServiceUrl = _serviceUrl,
            UsageTrackingEnabled = true
        };
    }
}
