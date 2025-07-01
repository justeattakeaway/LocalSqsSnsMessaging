using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core.Interfaces;

namespace LocalSqsSnsMessaging.Tests.Verification;

public sealed class AspireFixture : IAsyncInitializer, IAsyncDisposable
{
    private DistributedApplication? _app;
    private IDistributedApplicationTestingBuilder? _builder;

    public int? LocalStackPort => _app?.GetEndpoint("localstack").Port;

    public async Task InitializeAsync()
    {
#pragma warning disable CA1849
        // ReSharper disable once MethodHasAsyncOverload
        _builder = DistributedApplicationTestingBuilder.Create();
#pragma warning restore CA1849

        string[] services = ["sqs", "sns"];
        _builder.AddLocalStack(services);

        // Disable aspire host logs as they will populate a random test output
        _builder.Services.Add(ServiceDescriptor.Singleton<ILoggerFactory>(NullLoggerFactory.Instance));
        _builder.Services.Add(ServiceDescriptor.Singleton(typeof(ILogger<>), typeof(NullLogger<>)));

        _app = await _builder.BuildAsync();

        await _app.StartAsync();

        await _app.ResourceNotifications.WaitForResourceHealthyAsync("localstack");
    }

    public async ValueTask DisposeAsync()
    {
        if (_app != null)
        {
            await _app.DisposeAsync();
        }
        if (_builder != null)
        {
            await _builder.DisposeAsync();
        }
    }
}
