namespace InMemoryAws.Tests.Verification.ExternalVerification;

[InheritsTests]
public sealed class WireProtocolRefusalTestsVerification : WireProtocolRefusalTests, IDisposable
{
    private HttpClient? _client;

    [ClassDataSource<AspireFixture>(Shared = SharedType.PerTestSession)]
    public required AspireFixture AspireFixture { get; init; }

    [Before(Test)]
    public void BeforeEachTest()
    {
#pragma warning disable CA5394
        AccountId = ClientFactory.EffectiveAccountId(Random.Shared.NextInt64(999999999999).ToString("D12", NumberFormatInfo.InvariantInfo));
#pragma warning restore CA5394
    }

    // Floci serves every service on one port, telling them apart by the request.
    protected override HttpClient ClientFor(string service) =>
        _client ??= new HttpClient { BaseAddress = new Uri($"http://localhost:{AspireFixture.ServicePort}/") };

    public void Dispose() => _client?.Dispose();
}
