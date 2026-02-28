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
    private HttpClient? _motoApiClient;

    public int? MotoPort => _app?.GetEndpoint("moto").Port;

    public async Task InitializeAsync()
    {
#pragma warning disable CA1849
        // ReSharper disable once MethodHasAsyncOverload
        _builder = DistributedApplicationTestingBuilder.Create();
#pragma warning restore CA1849

        _builder.AddMotoServer();

        // Disable aspire host logs as they will populate a random test output
        _builder.Services.Add(ServiceDescriptor.Singleton<ILoggerFactory>(NullLoggerFactory.Instance));
        _builder.Services.Add(ServiceDescriptor.Singleton(typeof(ILogger<>), typeof(NullLogger<>)));

        _app = await _builder.BuildAsync();

        await _app.StartAsync();

        await _app.ResourceNotifications.WaitForResourceHealthyAsync("moto");
    }

    /// <summary>
    /// Resets all Moto Server state between tests. Unlike LocalStack, Moto does not
    /// isolate resources by AWS credentials so tests must reset state explicitly.
    /// </summary>
    public async Task ResetMotoStateAsync()
    {
        if (_motoApiClient is null)
        {
            _motoApiClient = new HttpClient
            {
                BaseAddress = new Uri($"http://localhost:{MotoPort}/")
            };
        }

#pragma warning disable CA2234
        using var response = await _motoApiClient.PostAsync("moto-api/reset", null);
#pragma warning restore CA2234
        response.EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync()
    {
        _motoApiClient?.Dispose();
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
        if (_builder is not null)
        {
            await _builder.DisposeAsync();
        }
    }
}
