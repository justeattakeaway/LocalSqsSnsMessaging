#if !ASPNETCORE && !AWS_SDK_V3
using System.Text.Json;
using Amazon.Runtime;
using Amazon.Runtime.Internal.Transform;
using Amazon.Runtime.Internal.Util;
using Amazon.SQS.Model.Internal.MarshallTransformations;

namespace LocalSqsSnsMessaging.Generic;

/// <summary>
/// JSON response unmarshaller for the typed ReceiveMessage operation. Mirrors
/// <c>Amazon.SQS.Model.Internal.MarshallTransformations.ReceiveMessageResponseUnmarshaller</c>
/// but produces <see cref="ReceiveMessageResponse{T}"/> by routing each message
/// through <see cref="GenericMessageUnmarshaller{T}"/>.
/// </summary>
internal sealed class GenericReceiveMessageResponseUnmarshaller<T> : JsonResponseUnmarshaller
{
    /// <summary>Default reflection-based singleton.</summary>
    public static GenericReceiveMessageResponseUnmarshaller<T> Instance { get; }
        = new(GenericMessageUnmarshaller<T>.Instance);

    private readonly GenericMessageUnmarshaller<T> _messageUnmarshaller;

    public GenericReceiveMessageResponseUnmarshaller(GenericMessageUnmarshaller<T> messageUnmarshaller)
    {
        _messageUnmarshaller = messageUnmarshaller;
    }

    public override Amazon.Runtime.AmazonWebServiceResponse Unmarshall(JsonUnmarshallerContext context)
    {
        var response = new ReceiveMessageResponse<T>();
        var reader = new StreamingUtf8JsonReader(context.Stream);
        context.Read(ref reader);

        var depth = context.CurrentDepth;
        while (context.ReadAtDepth(depth, ref reader))
        {
            if (context.TestExpression("Messages", depth))
            {
                var listUnmarshaller = new JsonListUnmarshaller<Message<T>, GenericMessageUnmarshaller<T>>(
                    _messageUnmarshaller);
                response.Messages = listUnmarshaller.Unmarshall(context, ref reader) ?? [];
            }
        }

        return response;
    }

    public override AmazonServiceException UnmarshallException(
        JsonUnmarshallerContext context,
        Exception innerException,
        System.Net.HttpStatusCode statusCode)
    {
        // Delegate exception parsing to the SDK-supplied unmarshaller so that
        // service-specific error types (QueueDoesNotExist, etc.) are produced
        // exactly as they would be for the non-generic call.
        return ReceiveMessageResponseUnmarshaller.Instance.UnmarshallException(context, innerException, statusCode);
    }
}
#endif
