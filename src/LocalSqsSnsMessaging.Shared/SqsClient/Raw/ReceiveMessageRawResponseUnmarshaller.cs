#if !ASPNETCORE && !AWS_SDK_V3
using Amazon.Runtime;
using Amazon.Runtime.Internal.Transform;
using Amazon.Runtime.Internal.Util;
using Amazon.SQS.Model.Internal.MarshallTransformations;

namespace LocalSqsSnsMessaging;

/// <summary>
/// JSON response unmarshaller for the raw ReceiveMessage operation. Mirrors
/// <c>Amazon.SQS.Model.Internal.MarshallTransformations.ReceiveMessageResponseUnmarshaller</c>
/// but produces <see cref="ReceiveMessageRawResponse"/> by routing each
/// message through <see cref="RawMessageUnmarshaller"/>.
/// </summary>
internal sealed class ReceiveMessageRawResponseUnmarshaller : JsonResponseUnmarshaller
{
    public static ReceiveMessageRawResponseUnmarshaller Instance { get; } = new();

    public override Amazon.Runtime.AmazonWebServiceResponse Unmarshall(JsonUnmarshallerContext context)
    {
        var response = new ReceiveMessageRawResponse();
        var reader = new StreamingUtf8JsonReader(context.Stream);
        context.Read(ref reader);

        var depth = context.CurrentDepth;
        while (context.ReadAtDepth(depth, ref reader))
        {
            if (context.TestExpression("Messages", depth))
            {
                var listUnmarshaller = new JsonListUnmarshaller<RawMessage, RawMessageUnmarshaller>(
                    RawMessageUnmarshaller.Instance);
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
        // Delegate exception parsing to the SDK-supplied unmarshaller so service-specific
        // error types (QueueDoesNotExist, etc.) surface unchanged.
        return ReceiveMessageResponseUnmarshaller.Instance.UnmarshallException(context, innerException, statusCode);
    }
}
#endif
