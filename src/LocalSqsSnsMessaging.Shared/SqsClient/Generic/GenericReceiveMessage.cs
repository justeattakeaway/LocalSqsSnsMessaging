#if !ASPNETCORE && !AWS_SDK_V3
using SdkMessageAttributeValue = Amazon.SQS.Model.MessageAttributeValue;

namespace LocalSqsSnsMessaging.Generic;

/// <summary>
/// A typed SQS message whose body has been deserialized to <typeparamref name="T"/>
/// directly from the response stream, avoiding the intermediate string allocation
/// that <c>Amazon.SQS.Model.Message.Body</c> would require.
/// </summary>
public sealed class Message<T>
{
    public string? MessageId { get; set; }
    public string? ReceiptHandle { get; set; }
    public string? MD5OfBody { get; set; }

    /// <summary>
    /// The deserialized body. Reference types may be <c>null</c> when the wire
    /// payload was JSON null or when deserialization produced a default value.
    /// </summary>
    public T? Body { get; set; }

#pragma warning disable CA2227
    public Dictionary<string, string>? Attributes { get; set; }
#pragma warning restore CA2227
    public string? MD5OfMessageAttributes { get; set; }
#pragma warning disable CA2227
    public Dictionary<string, SdkMessageAttributeValue>? MessageAttributes { get; set; }
#pragma warning restore CA2227
}

/// <summary>
/// Typed counterpart of <c>Amazon.SQS.Model.ReceiveMessageResponse</c>. Returned by
/// <see cref="TypedAmazonSQSClient.ReceiveMessageAsync{T}(Amazon.SQS.Model.ReceiveMessageRequest, System.Threading.CancellationToken)"/>.
/// </summary>
public sealed class ReceiveMessageResponse<T> : Amazon.Runtime.AmazonWebServiceResponse
{
#pragma warning disable CA1002, CA2227
    public List<Message<T>> Messages { get; set; } = [];
#pragma warning restore CA1002, CA2227
}
#endif
