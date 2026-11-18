using System.Collections.Concurrent;

namespace InMemoryAws.Server;

internal sealed class CloudRegistry
{
    private readonly ConcurrentDictionary<string, InMemoryAwsCloud> _clouds = new();
    private readonly string _region;
    private readonly Uri _serviceUrl;

    public string DefaultAccountId { get; }

    public CloudRegistry(string defaultAccountId, string region, Uri serviceUrl)
    {
        DefaultAccountId = defaultAccountId;
        _region = region;
        _serviceUrl = serviceUrl;

        _clouds[defaultAccountId] = CreateCloud(defaultAccountId);
    }

    public InMemoryAwsCloud GetOrCreate(string accountId)
    {
        return _clouds.GetOrAdd(accountId, CreateCloud);
    }

    public InMemoryAwsCloud DefaultCloud => _clouds[DefaultAccountId];

    public List<string> AccountIds => _clouds.Keys.Order().ToList();

    private InMemoryAwsCloud CreateCloud(string accountId)
    {
        return new InMemoryAwsCloud
        {
            CurrentRegion = _region,
            CurrentAccountId = accountId,
            ServiceUrl = _serviceUrl,
            UsageTrackingEnabled = true
        };
    }
}
