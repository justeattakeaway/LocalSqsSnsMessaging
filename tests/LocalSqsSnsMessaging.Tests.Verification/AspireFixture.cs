using Aspire.Hosting.Testing;

namespace LocalSqsSnsMessaging.Tests.Verification;

public sealed class AspireFixture : IAsyncLifetime
{
    private DistributedApplication? _app;
    
    public int? LocalStackPort => _app?.GetEndpoint("localstack").Port;

    public async ValueTask InitializeAsync()
    {
        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.LocalSqsSnsMessaging_Tests_AppHost>();
        
        _app = await appHost.BuildAsync();
        await _app.StartAsync();
    }
    
    public async ValueTask DisposeAsync()
    {
        if (_app != null)
        {
            await _app.DisposeAsync();
        }
    }
}