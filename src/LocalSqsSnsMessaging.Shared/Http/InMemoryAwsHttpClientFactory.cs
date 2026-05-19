#if !ASPNETCORE
using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SQS;

namespace LocalSqsSnsMessaging.Http;

/// <summary>
/// An <see cref="Amazon.Runtime.HttpClientFactory"/> that routes AWS SQS and SNS traffic to an
/// in-memory bus, while delegating every other AWS service to a fallback delegate.
/// </summary>
/// <remarks>
/// This factory is intended for integration tests where the application under test registers a
/// custom <see cref="Amazon.Runtime.HttpClientFactory"/> in DI for all AWS SDK clients. Replacing
/// that registration with an <see cref="InMemoryAwsHttpClientFactory"/> intercepts only the
/// services backed by <see cref="InMemoryAwsBus"/> and lets the rest reach their real endpoints.
/// When no fallback is supplied, requests for unhandled AWS services throw
/// <see cref="NotSupportedException"/>.
/// </remarks>
public class InMemoryAwsHttpClientFactory : HttpClientFactory, IDisposable
{
    private readonly Func<IClientConfig, HttpClient>? _fallback;
    private readonly InMemoryAwsHttpMessageHandler _sqsHandler;
    private readonly InMemoryAwsHttpMessageHandler _snsHandler;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryAwsHttpClientFactory"/> class.
    /// </summary>
    /// <param name="bus">The in-memory AWS bus that will handle SQS and SNS traffic.</param>
    /// <param name="fallback">
    /// Optional delegate invoked for AWS clients not backed by <paramref name="bus"/>. Typically a
    /// one-liner that defers to an <c>IHttpClientFactory</c>, e.g.
    /// <c>cfg =&gt; httpClientFactory.CreateClient(cfg.GetType().Name)</c>. When <see langword="null"/>,
    /// requests for unhandled services throw <see cref="NotSupportedException"/>.
    /// </param>
    public InMemoryAwsHttpClientFactory(InMemoryAwsBus bus, Func<IClientConfig, HttpClient>? fallback = null)
    {
        ArgumentNullException.ThrowIfNull(bus);
        _fallback = fallback;
        _sqsHandler = new InMemoryAwsHttpMessageHandler(bus, AwsServiceType.Sqs);
        _snsHandler = new InMemoryAwsHttpMessageHandler(bus, AwsServiceType.Sns);
    }

    /// <inheritdoc />
    public override HttpClient CreateHttpClient(IClientConfig clientConfig)
    {
        ArgumentNullException.ThrowIfNull(clientConfig);
        ThrowIfDisposed();

        var handler = GetHandler(clientConfig);
        if (handler is not null)
        {
            return new HttpClient(handler, disposeHandler: false)
            {
                Timeout = clientConfig.Timeout ?? TimeSpan.FromSeconds(100)
            };
        }

        if (_fallback is not null)
        {
            return _fallback(clientConfig);
        }

        throw new NotSupportedException(
            $"No fallback was provided to handle AWS client config '{clientConfig.GetType().Name}'. " +
            "Pass a delegate to the constructor to provide an HttpClient for non-SQS/SNS traffic.");
    }

    /// <inheritdoc />
    public override string GetConfigUniqueString(IClientConfig clientConfig)
    {
        ArgumentNullException.ThrowIfNull(clientConfig);
        ThrowIfDisposed();
        return GetHandler(clientConfig) is not null
            ? "InMemory"
            : base.GetConfigUniqueString(clientConfig);
    }

    /// <inheritdoc />
    public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig)
    {
        ArgumentNullException.ThrowIfNull(clientConfig);
        ThrowIfDisposed();
        // Our handlers are owned by this factory and must outlive any HttpClient that wraps them.
        // Defer to the SDK's default for everything else so the fallback's clients are disposed
        // and cached as the SDK normally would.
        return GetHandler(clientConfig) is null && base.DisposeHttpClientsAfterUse(clientConfig);
    }

    /// <inheritdoc />
    public override bool UseSDKHttpClientCaching(IClientConfig clientConfig)
    {
        ArgumentNullException.ThrowIfNull(clientConfig);
        ThrowIfDisposed();
        return GetHandler(clientConfig) is null && base.UseSDKHttpClientCaching(clientConfig);
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    private InMemoryAwsHttpMessageHandler? GetHandler(IClientConfig clientConfig) =>
        clientConfig switch
        {
            AmazonSQSConfig => _sqsHandler,
            AmazonSimpleNotificationServiceConfig => _snsHandler,
            _ => null
        };

    /// <summary>
    /// Releases the in-memory handlers owned by this factory.
    /// </summary>
    /// <param name="disposing">True when called from <see cref="Dispose()"/>.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing)
        {
            _sqsHandler.Dispose();
            _snsHandler.Dispose();
        }
        _disposed = true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}
#endif
