#if !ASPNETCORE && !AWS_SDK_V3
using System.Buffers;
using System.Text.Json;
using Amazon.Runtime.Internal.Transform;
using Amazon.Runtime.Internal.Util;
using Amazon.SQS.Model.Internal.MarshallTransformations;

namespace LocalSqsSnsMessaging;

/// <summary>
/// Reads a single SQS <c>Message</c> JSON object into a <see cref="RawMessage"/>.
/// The <c>Body</c> field is captured as the unescaped UTF-8 bytes of the
/// original SQS body (which is itself a JSON string in the AWS response), so
/// callers can feed those bytes directly to a deserializer of their choice
/// without an intermediate <see cref="string"/> allocation.
/// </summary>
internal sealed class RawMessageUnmarshaller
    : IJsonUnmarshaller<RawMessage, JsonUnmarshallerContext>
{
    public static RawMessageUnmarshaller Instance { get; } = new();

    public RawMessage Unmarshall(JsonUnmarshallerContext context, ref StreamingUtf8JsonReader reader)
    {
        var message = new RawMessage();

        if (!context.IsEmptyResponse)
        {
            context.Read(ref reader);
            if (context.CurrentTokenType == JsonTokenType.Null)
            {
                return null!;
            }
        }

        var depth = context.CurrentDepth;
        while (context.ReadAtDepth(depth, ref reader))
        {
            if (context.TestExpression("Body", depth))
            {
                message.Body = ReadBodyBytes(context, ref reader);
                continue;
            }
            if (context.TestExpression("MessageId", depth))
            {
                message.MessageId = StringUnmarshaller.Instance.Unmarshall(context, ref reader);
                continue;
            }
            if (context.TestExpression("ReceiptHandle", depth))
            {
                message.ReceiptHandle = StringUnmarshaller.Instance.Unmarshall(context, ref reader);
                continue;
            }
            if (context.TestExpression("MD5OfBody", depth))
            {
                message.MD5OfBody = StringUnmarshaller.Instance.Unmarshall(context, ref reader);
                continue;
            }
            if (context.TestExpression("MD5OfMessageAttributes", depth))
            {
                message.MD5OfMessageAttributes = StringUnmarshaller.Instance.Unmarshall(context, ref reader);
                continue;
            }
            if (context.TestExpression("Attributes", depth))
            {
                var dict = new JsonDictionaryUnmarshaller<string, string, StringUnmarshaller, StringUnmarshaller>(
                    StringUnmarshaller.Instance, StringUnmarshaller.Instance);
                message.Attributes = dict.Unmarshall(context, ref reader);
                continue;
            }
            if (context.TestExpression("MessageAttributes", depth))
            {
                var dict = new JsonDictionaryUnmarshaller<string, Amazon.SQS.Model.MessageAttributeValue, StringUnmarshaller, MessageAttributeValueUnmarshaller>(
                    StringUnmarshaller.Instance, MessageAttributeValueUnmarshaller.Instance);
                message.MessageAttributes = dict.Unmarshall(context, ref reader);
                continue;
            }
        }

        return message;
    }

    private static ReadOnlyMemory<byte> ReadBodyBytes(JsonUnmarshallerContext context, ref StreamingUtf8JsonReader reader)
    {
        // Advance to the value token for the current "Body" property.
        context.Read(ref reader);

        // Snapshot the inner reader; CopyString and the value-span accessors
        // don't mutate position so a copy is fine.
        var jsonReader = reader.Reader;
        if (jsonReader.TokenType == JsonTokenType.Null)
        {
            return default;
        }

        if (jsonReader.TokenType != JsonTokenType.String)
        {
            // SQS bodies are always JSON strings; defensively return empty for anything else.
            return default;
        }

        if (!jsonReader.ValueIsEscaped)
        {
            if (jsonReader.HasValueSequence)
            {
                return jsonReader.ValueSequence.ToArray();
            }
            return jsonReader.ValueSpan.ToArray();
        }

        // Escaped path: copy the unescaped bytes through a pooled buffer, then
        // shrink to a right-sized array for the caller to keep.
        var maxLen = jsonReader.HasValueSequence
            ? checked((int)jsonReader.ValueSequence.Length)
            : jsonReader.ValueSpan.Length;
        var rented = ArrayPool<byte>.Shared.Rent(maxLen);
        try
        {
            var written = jsonReader.CopyString(rented);
            var result = new byte[written];
            Buffer.BlockCopy(rented, 0, result, 0, written);
            return result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
#endif
