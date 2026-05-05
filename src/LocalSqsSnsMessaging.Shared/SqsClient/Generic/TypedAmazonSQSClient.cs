#if !ASPNETCORE && !AWS_SDK_V3
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Amazon.SQS;
using Amazon.SQS.Model;
using Amazon.SQS.Model.Internal.MarshallTransformations;

namespace LocalSqsSnsMessaging.Generic;

/// <summary>
/// Per-T cache for unmarshallers keyed by their associated metadata source.
/// <see cref="ConditionalWeakTable{TKey, TValue}"/> means the cache entry is
/// collected when the key (the user's <see cref="JsonTypeInfo{T}"/> or
/// <see cref="JsonSerializerOptions"/>) becomes unreachable, so the cache
/// imposes no lifetime extension on user-supplied objects.
/// </summary>
internal static class UnmarshallerCache<T>
{
    public static readonly ConditionalWeakTable<JsonTypeInfo<T>, GenericReceiveMessageResponseUnmarshaller<T>> ByTypeInfo = new();
    public static readonly ConditionalWeakTable<JsonSerializerOptions, GenericReceiveMessageResponseUnmarshaller<T>> ByOptions = new();
}

/// <summary>
/// An <see cref="AmazonSQSClient"/> subclass that adds generic
/// <c>ReceiveMessageAsync&lt;T&gt;</c> overloads. The body of each returned
/// message is deserialized straight from the response stream into
/// <typeparamref name="T"/> via a custom unmarshaller, avoiding the
/// <see cref="string"/> allocation that the standard
/// <see cref="AmazonSQSClient.ReceiveMessageAsync(ReceiveMessageRequest, CancellationToken)"/>
/// path performs for <c>Message.Body</c>.
/// </summary>
public sealed class TypedAmazonSQSClient : AmazonSQSClient
{
    private const string TrimWarning =
        "JSON deserialization may require types whose members cannot be statically analyzed. " +
        "Use the JsonTypeInfo<T> or JsonSerializerContext overload for trim/AOT-safe code.";

    public TypedAmazonSQSClient(AWSCredentials credentials, AmazonSQSConfig config)
        : base(credentials, config)
    {
    }

    /// <summary>
    /// Receives messages and deserializes each body into <typeparamref name="T"/>
    /// using reflection-based JSON metadata. Not trim/AOT-safe; prefer the
    /// <see cref="ReceiveMessageAsync{T}(ReceiveMessageRequest, JsonTypeInfo{T}, CancellationToken)"/>
    /// overload when running under trimming.
    /// </summary>
    [RequiresUnreferencedCode(TrimWarning)]
    [RequiresDynamicCode(TrimWarning)]
    public Task<ReceiveMessageResponse<T>> ReceiveMessageAsync<T>(
        ReceiveMessageRequest request,
        CancellationToken cancellationToken = default)
        => InvokeReceive(request, GenericReceiveMessageResponseUnmarshaller<T>.Instance, cancellationToken);

    /// <summary>
    /// Receives messages and deserializes each body into <typeparamref name="T"/>
    /// using the supplied <see cref="JsonSerializerOptions"/>. Subject to the
    /// same trim/AOT caveats as the parameterless overload.
    /// </summary>
    [RequiresUnreferencedCode(TrimWarning)]
    [RequiresDynamicCode(TrimWarning)]
    public Task<ReceiveMessageResponse<T>> ReceiveMessageAsync<T>(
        ReceiveMessageRequest request,
        JsonSerializerOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var unmarshaller = UnmarshallerCache<T>.ByOptions.GetValue(options, static o =>
            new GenericReceiveMessageResponseUnmarshaller<T>(GenericMessageUnmarshaller<T>.ForOptions(o)));
        return InvokeReceive(request, unmarshaller, cancellationToken);
    }

    /// <summary>
    /// Receives messages and deserializes each body into <typeparamref name="T"/>
    /// using a <see cref="JsonTypeInfo{T}"/>, typically obtained from a
    /// source-generated <see cref="JsonSerializerContext"/>. This overload is
    /// trim and AOT-safe.
    /// </summary>
    public Task<ReceiveMessageResponse<T>> ReceiveMessageAsync<T>(
        ReceiveMessageRequest request,
        JsonTypeInfo<T> jsonTypeInfo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);
        var unmarshaller = UnmarshallerCache<T>.ByTypeInfo.GetValue(jsonTypeInfo, static ti =>
            new GenericReceiveMessageResponseUnmarshaller<T>(GenericMessageUnmarshaller<T>.ForTypeInfo(ti)));
        return InvokeReceive(request, unmarshaller, cancellationToken);
    }

    /// <summary>
    /// Receives messages and deserializes each body into <typeparamref name="T"/>
    /// using a <see cref="JsonSerializerContext"/>. The context must contain
    /// metadata for <typeparamref name="T"/>; otherwise an
    /// <see cref="InvalidOperationException"/> is thrown. Trim and AOT-safe.
    /// </summary>
    public Task<ReceiveMessageResponse<T>> ReceiveMessageAsync<T>(
        ReceiveMessageRequest request,
        JsonSerializerContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var typeInfo = context.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>
            ?? throw new InvalidOperationException(
                $"JsonSerializerContext '{context.GetType().FullName}' does not contain metadata for type '{typeof(T).FullName}'.");
        return ReceiveMessageAsync<T>(request, typeInfo, cancellationToken);
    }

    private Task<ReceiveMessageResponse<T>> InvokeReceive<T>(
        ReceiveMessageRequest request,
        GenericReceiveMessageResponseUnmarshaller<T> unmarshaller,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var options = new InvokeOptions
        {
            RequestMarshaller = ReceiveMessageRequestMarshaller.Instance,
            ResponseUnmarshaller = unmarshaller,
        };

        return InvokeAsync<ReceiveMessageResponse<T>>(request, options, cancellationToken);
    }
}
#endif
