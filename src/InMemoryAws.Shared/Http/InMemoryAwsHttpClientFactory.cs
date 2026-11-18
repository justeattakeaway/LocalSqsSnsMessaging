#if !ASPNETCORE
using Amazon.DynamoDBv2;
using Amazon.EventBridge;
using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SQS;

namespace InMemoryAws.Http;

/// <summary>
/// An <see cref="Amazon.Runtime.HttpClientFactory"/> that routes AWS SQS, SNS, EventBridge and DynamoDB traffic to an
/// in-memory cloud, while delegating every other AWS service to a fallback delegate.
/// </summary>
/// <remarks>
/// This factory is intended for integration tests where the application under test registers a
/// custom <see cref="Amazon.Runtime.HttpClientFactory"/> in DI for all AWS SDK clients. Replacing
/// that registration with an <see cref="InMemoryAwsHttpClientFactory"/> intercepts only the
/// services backed by <see cref="InMemoryAwsCloud"/> and lets the rest reach their real endpoints.
/// When no fallback is supplied, requests for unhandled AWS services throw
/// <see cref="NotSupportedException"/>.
/// </remarks>
public class InMemoryAwsHttpClientFactory : HttpClientFactory, IDisposable
{
    private readonly Func<IClientConfig, HttpClient>? _fallback;
    private readonly InMemoryAwsHttpMessageHandler _sqsHandler;
    private readonly InMemoryAwsHttpMessageHandler _snsHandler;
    private readonly InMemoryAwsHttpMessageHandler _eventBridgeHandler;
    private readonly InMemoryAwsHttpMessageHandler _dynamoDbHandler;
    private readonly InMemoryAwsHttpMessageHandler _dynamoDbStreamsHandler;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryAwsHttpClientFactory"/> class.
    /// </summary>
    /// <param name="cloud">The in-memory AWS cloud that will handle SQS and SNS traffic.</param>
    /// <param name="fallback">
    /// Optional delegate invoked for AWS clients not backed by <paramref name="cloud"/>. Typically a
    /// one-liner that defers to an <c>IHttpClientFactory</c>, e.g.
    /// <c>cfg =&gt; httpClientFactory.CreateClient(cfg.GetType().Name)</c>. When <see langword="null"/>,
    /// requests for unhandled services throw <see cref="NotSupportedException"/>.
    /// </param>
    public InMemoryAwsHttpClientFactory(InMemoryAwsCloud cloud, Func<IClientConfig, HttpClient>? fallback = null)
    {
        ArgumentNullException.ThrowIfNull(cloud);
        _fallback = fallback;
        _sqsHandler = new InMemoryAwsHttpMessageHandler(cloud, AwsServiceType.Sqs);
        _snsHandler = new InMemoryAwsHttpMessageHandler(cloud, AwsServiceType.Sns);
        _eventBridgeHandler = new InMemoryAwsHttpMessageHandler(cloud, AwsServiceType.EventBridge);
        _dynamoDbHandler = new InMemoryAwsHttpMessageHandler(cloud, AwsServiceType.DynamoDb);
        _dynamoDbStreamsHandler = new InMemoryAwsHttpMessageHandler(cloud, AwsServiceType.DynamoDbStreams);
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
            AmazonEventBridgeConfig => _eventBridgeHandler,
            AmazonDynamoDBConfig => _dynamoDbHandler,
            // By name: in AWS SDK v4 DynamoDB Streams is its own package, which this library doesn't reference.
            _ when string.Equals(clientConfig.GetType().FullName, "Amazon.DynamoDBStreams.AmazonDynamoDBStreamsConfig", StringComparison.Ordinal)
                || string.Equals(clientConfig.GetType().FullName, "Amazon.DynamoDBv2.AmazonDynamoDBStreamsConfig", StringComparison.Ordinal) => _dynamoDbStreamsHandler,
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
            _eventBridgeHandler.Dispose();
            _dynamoDbHandler.Dispose();
            _dynamoDbStreamsHandler.Dispose();
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
