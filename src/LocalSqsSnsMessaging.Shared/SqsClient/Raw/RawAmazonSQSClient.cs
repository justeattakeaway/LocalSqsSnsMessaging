#if !ASPNETCORE && !AWS_SDK_V3
using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Amazon.SQS;
using Amazon.SQS.Model;
using Amazon.SQS.Model.Internal.MarshallTransformations;

namespace LocalSqsSnsMessaging;

/// <summary>
/// An <see cref="AmazonSQSClient"/> subclass that adds
/// <see cref="ReceiveMessageRawAsync"/>: a receive operation that returns the
/// SQS message body as <see cref="ReadOnlyMemory{Byte}"/> UTF-8 bytes instead
/// of a <see cref="string"/>. Callers handle deserialization themselves with
/// the serializer of their choice.
/// </summary>
public sealed class RawAmazonSQSClient : AmazonSQSClient
{
    public RawAmazonSQSClient(AWSCredentials credentials, AmazonSQSConfig config)
        : base(credentials, config)
    {
    }

    /// <summary>
    /// Receives messages from the queue and returns each body as the
    /// unescaped UTF-8 bytes from the SQS response, ready to be passed to a
    /// deserializer such as
    /// <c>JsonSerializer.Deserialize&lt;T&gt;(ReadOnlySpan&lt;byte&gt;, JsonTypeInfo&lt;T&gt;)</c>.
    /// </summary>
    public Task<ReceiveMessageRawResponse> ReceiveMessageRawAsync(
        ReceiveMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var options = new InvokeOptions
        {
            RequestMarshaller = ReceiveMessageRequestMarshaller.Instance,
            ResponseUnmarshaller = ReceiveMessageRawResponseUnmarshaller.Instance,
        };

        return InvokeAsync<ReceiveMessageRawResponse>(request, options, cancellationToken);
    }
}
#endif
