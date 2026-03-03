using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry;
using OpenTelemetry.Trace;
using TUnit.Core.Interfaces;

namespace LocalSqsSnsMessaging.Tests.Verification;

public sealed class AspireFixture : IAsyncInitializer, IAsyncDisposable
{
    private DistributedApplication? _app;
    private IDistributedApplicationTestingBuilder? _builder;
    private TracerProvider? _tracerProvider;

    public int? LocalStackPort => _app?.GetEndpoint("localstack").Port;

    public async Task InitializeAsync()
    {
        _tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource("LocalSqsSnsMessaging")
            .AddAWSInstrumentation()
            .Build();

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
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
        if (_builder is not null)
        {
            await _builder.DisposeAsync();
        }
        _tracerProvider?.Dispose();
    }
}
