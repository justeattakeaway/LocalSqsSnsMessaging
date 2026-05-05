#if !ASPNETCORE && !AWS_SDK_V3
using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Amazon.Runtime.Internal.Transform;
using Amazon.Runtime.Internal.Util;
using Amazon.SQS.Model.Internal.MarshallTransformations;

namespace LocalSqsSnsMessaging.Generic;

/// <summary>
/// Reads a single SQS <c>Message</c> JSON object into a <see cref="Message{T}"/>,
/// deserializing the <c>Body</c> field directly from the underlying UTF-8 byte
/// stream into <typeparamref name="T"/>. The intermediate string allocation that
/// the AWS SDK's stock <c>MessageUnmarshaller</c> performs is avoided.
/// </summary>
internal sealed class GenericMessageUnmarshaller<T>
    : IJsonUnmarshaller<Message<T>, JsonUnmarshallerContext>
{
    /// <summary>
    /// Default reflection-based singleton (uses <see cref="JsonSerializerDefaults.Web"/>).
    /// Not trim/AOT-safe; callers concerned with trimming should pass a
    /// <see cref="JsonTypeInfo{T}"/> from a source-generated context instead.
    /// </summary>
    public static GenericMessageUnmarshaller<T> Instance { get; } = new(s_defaultOptions, typeInfo: null);

    private static readonly JsonSerializerOptions s_defaultOptions = new(JsonSerializerDefaults.Web);

    private readonly JsonSerializerOptions? _options;
    private readonly JsonTypeInfo<T>? _typeInfo;

    private GenericMessageUnmarshaller(JsonSerializerOptions? options, JsonTypeInfo<T>? typeInfo)
    {
        _options = options;
        _typeInfo = typeInfo;
    }

    public static GenericMessageUnmarshaller<T> ForOptions(JsonSerializerOptions options)
        => new(options, typeInfo: null);

    public static GenericMessageUnmarshaller<T> ForTypeInfo(JsonTypeInfo<T> typeInfo)
        => new(options: null, typeInfo);

    public Message<T> Unmarshall(JsonUnmarshallerContext context, ref StreamingUtf8JsonReader reader)
    {
        var message = new Message<T>();

        // Mirrors the shape of MessageUnmarshaller.Unmarshall: peek for a JSON null
        // open the object at the current depth, then walk the field set.
        if (!context.IsEmptyResponse)
        {
            context.Read(ref reader);
            var token = context.CurrentTokenType;
            if (token == JsonTokenType.Null)
            {
                return null!;
            }
        }

        var depth = context.CurrentDepth;
        while (context.ReadAtDepth(depth, ref reader))
        {
            if (context.TestExpression("Body", depth))
            {
                message.Body = ReadBody(context, ref reader);
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

    private T? ReadBody(JsonUnmarshallerContext context, ref StreamingUtf8JsonReader reader)
    {
        // Advance to the value token for the current "Body" property.
        context.Read(ref reader);

        // Reader is a property returning by value; CopyString and the value-span
        // accessors don't mutate position, so a local snapshot is sufficient.
        var jsonReader = reader.Reader;
        if (jsonReader.TokenType == JsonTokenType.Null)
        {
            return default;
        }

        if (jsonReader.TokenType != JsonTokenType.String)
        {
            // Body should always be a JSON string in the SQS protocol; if anything
            // else shows up, fall back to deserializing the raw token directly.
            return DeserializeFromReader(ref jsonReader);
        }

        // SQS bodies are JSON strings whose content is itself JSON. We need the
        // unescaped UTF-8 bytes (the user's payload) and feed those to the
        // deserializer without ever materializing a System.String for the body.
        if (!jsonReader.ValueIsEscaped)
        {
            if (jsonReader.HasValueSequence)
            {
                var seq = jsonReader.ValueSequence;
                if (seq.IsSingleSegment)
                {
                    return DeserializeFromUtf8(seq.First.Span);
                }
                var len = checked((int)seq.Length);
                var rented = ArrayPool<byte>.Shared.Rent(len);
                try
                {
                    seq.CopyTo(rented);
                    return DeserializeFromUtf8(new ReadOnlySpan<byte>(rented, 0, len));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }
            }
            return DeserializeFromUtf8(jsonReader.ValueSpan);
        }

        // Escaped path: copy the unescaped value into a pooled buffer and parse.
        var maxLen = jsonReader.HasValueSequence
            ? checked((int)jsonReader.ValueSequence.Length)
            : jsonReader.ValueSpan.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(maxLen);
        try
        {
            var written = jsonReader.CopyString(buffer);
            return DeserializeFromUtf8(new ReadOnlySpan<byte>(buffer, 0, written));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private T? DeserializeFromUtf8(ReadOnlySpan<byte> utf8Json)
        => _typeInfo is not null
            ? JsonSerializer.Deserialize(utf8Json, _typeInfo)
            : JsonSerializer.Deserialize<T>(utf8Json, _options);

    private T? DeserializeFromReader(ref Utf8JsonReader reader)
        => _typeInfo is not null
            ? JsonSerializer.Deserialize(ref reader, _typeInfo)
            : JsonSerializer.Deserialize<T>(ref reader, _options);
}
#endif
