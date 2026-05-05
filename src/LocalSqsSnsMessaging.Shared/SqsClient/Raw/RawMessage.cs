#if !ASPNETCORE && !AWS_SDK_V3
using SdkMessageAttributeValue = Amazon.SQS.Model.MessageAttributeValue;

namespace LocalSqsSnsMessaging;

/// <summary>
/// A SQS message whose body is exposed as the unescaped UTF-8 bytes from the
/// SQS response. Lets callers feed the bytes directly to any deserializer
/// (System.Text.Json, MessagePack, Protobuf, etc.) without paying for the
/// <see cref="string"/> allocation that <c>Amazon.SQS.Model.Message.Body</c>
/// would require.
/// </summary>
public sealed class RawMessage
{
    public string? MessageId { get; set; }
    public string? ReceiptHandle { get; set; }
    public string? MD5OfBody { get; set; }

    /// <summary>
    /// Unescaped UTF-8 bytes of the SQS message body. The default (empty)
    /// value indicates a missing or null body in the response.
    /// </summary>
    public ReadOnlyMemory<byte> Body { get; set; }

#pragma warning disable CA2227
    public Dictionary<string, string>? Attributes { get; set; }
#pragma warning restore CA2227
    public string? MD5OfMessageAttributes { get; set; }
#pragma warning disable CA2227
    public Dictionary<string, SdkMessageAttributeValue>? MessageAttributes { get; set; }
#pragma warning restore CA2227
}

/// <summary>
/// Response for <see cref="RawAmazonSQSClient.ReceiveMessageRawAsync"/>.
/// </summary>
public sealed class ReceiveMessageRawResponse : Amazon.Runtime.AmazonWebServiceResponse
{
#pragma warning disable CA1002, CA2227
    public List<RawMessage> Messages { get; set; } = [];
#pragma warning restore CA1002, CA2227
}
#endif
