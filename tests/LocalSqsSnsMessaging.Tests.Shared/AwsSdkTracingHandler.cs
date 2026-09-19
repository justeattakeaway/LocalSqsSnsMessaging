using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Amazon.Runtime;
using Amazon.Runtime.Internal;
using SnsMessageAttributeValue = Amazon.SimpleNotificationService.Model.MessageAttributeValue;
using SqsMessageSystemAttributeValue = Amazon.SQS.Model.MessageSystemAttributeValue;

namespace LocalSqsSnsMessaging.Tests;

/// <summary>Adds <see cref="AwsSdkTracingHandler"/> to each SDK client as it is constructed.</summary>
internal sealed class AwsSdkTracingCustomizer : IRuntimePipelineCustomizer
{
    public string UniqueName => "LocalSqsSnsMessaging test tracing";

    public void Customize(Type type, RuntimePipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);

        // Ahead of the marshaller, so the request object can still be added to.
        pipeline.AddHandlerBefore<Marshaller>(new AwsSdkTracingHandler());
    }
}

/// <summary>
/// Tags the SDK's span with the messaging semantic conventions, and - unless
/// <see cref="AwsSdkTracing.SuppressPropagation"/> is in scope - writes the span's context into the
/// outgoing message so a consumer can carry on the trace.
/// </summary>
/// <remarks>
/// The span itself, and the <c>rpc.*</c>, <c>aws.request_id</c>, <c>http.*</c> and exception
/// attributes on it, come from the SDK's own telemetry via <see cref="AwsSdkActivityTracerProvider"/>.
/// What is added here is the part the SDK doesn't know to add: which topic or queue the call is
/// about. The attribute names follow semantic conventions 1.40 - the versions the real
/// instrumentation reaches for last, and the only ones that name SNS destinations at all.
/// </remarks>
internal sealed class AwsSdkTracingHandler : PipelineHandler
{
    private const string Sns = "SNS";
    private const string Sqs = "SQS";

    private static readonly ConcurrentDictionary<(Type Type, string Property), PropertyInfo?> PropertyCache = new();

    /// <summary>Operations that produce or consume a message, and which of the two they do.</summary>
    private static readonly Dictionary<string, string> MessagingOperationTypes = new(StringComparer.Ordinal)
    {
        ["Publish"] = "send",
        ["PublishBatch"] = "send",
        ["SendMessage"] = "send",
        ["SendMessageBatch"] = "send",
        ["ReceiveMessage"] = "receive",
        ["DeleteMessage"] = "settle",
        ["DeleteMessageBatch"] = "settle"
    };

    public override async Task<T> InvokeAsync<T>(IExecutionContext executionContext)
    {
        ArgumentNullException.ThrowIfNull(executionContext);

        BeginRequest(executionContext.RequestContext);
        var response = await base.InvokeAsync<T>(executionContext).ConfigureAwait(false);
        EndRequest(response);
        return response;
    }

    public override void InvokeSync(IExecutionContext executionContext)
    {
        ArgumentNullException.ThrowIfNull(executionContext);

        BeginRequest(executionContext.RequestContext);
        base.InvokeSync(executionContext);
        EndRequest(executionContext.ResponseContext.Response);
    }

    private static void BeginRequest(IRequestContext context)
    {
        // The SDK's MetricsHandler has already started the span for this call.
        var activity = Activity.Current;
        if (activity is null)
        {
            return;
        }

        if (activity.IsAllDataRequested)
        {
            Describe(activity, context);
        }

        if (!AwsSdkTracing.PropagationIsSuppressed)
        {
            Propagate(context.OriginalRequest, activity);
        }
    }

    private static void EndRequest(Amazon.Runtime.AmazonWebServiceResponse? response)
    {
        var activity = Activity.Current;
        if (activity?.IsAllDataRequested != true || response is null)
        {
            return;
        }

        if (GetStringProperty(response, "MessageId") is { Length: > 0 } messageId)
        {
            activity.SetTag("messaging.message.id", messageId);
        }
    }

    private static void Describe(Activity activity, IRequestContext context)
    {
        var service = context.ClientConfig.ServiceId;
        var operation = OperationName(context.RequestName);
        var request = context.OriginalRequest;

        if (context.ClientConfig.RegionEndpoint?.SystemName is { Length: > 0 } region)
        {
            activity.SetTag("cloud.region", region);
        }

        switch (service)
        {
            case Sns when GetStringProperty(request, "TopicArn") is { Length: > 0 } topicArn:
                activity.SetTag("aws.sns.topic.arn", topicArn);
                SetMessaging(activity, "aws.sns", operation, LastSegment(topicArn, ':'));
                break;

            case Sns:
                SetMessaging(activity, "aws.sns", operation, destination: null);
                break;

            case Sqs when GetStringProperty(request, "QueueUrl") is { Length: > 0 } queueUrl:
                activity.SetTag("aws.sqs.queue.url", queueUrl);
                if (Uri.TryCreate(queueUrl, UriKind.Absolute, out var uri))
                {
                    activity.SetTag("server.address", uri.Host);
                }

                SetMessaging(activity, "aws_sqs", operation, LastSegment(queueUrl, '/'));
                break;

            case Sqs:
                SetMessaging(activity, "aws_sqs", operation, destination: null);
                break;

            default:
                break;
        }
    }

    private static void SetMessaging(Activity activity, string system, string operation, string? destination)
    {
        activity.SetTag("messaging.system", system);
        activity.SetTag("messaging.operation.name", operation);

        if (destination is not null)
        {
            activity.SetTag("messaging.destination.name", destination);
        }

        if (MessagingOperationTypes.TryGetValue(operation, out var operationType))
        {
            activity.SetTag("messaging.operation.type", operationType);
        }
    }

    private static string? LastSegment(string value, char delimiter)
    {
        var index = value.LastIndexOf(delimiter);
        return index > -1 && index < value.Length - 1 ? value[(index + 1)..] : null;
    }

    private static string OperationName(string requestName) =>
        requestName.EndsWith("Request", StringComparison.Ordinal)
            ? requestName[..^"Request".Length]
            : requestName;

    /// <summary>
    /// Reads a property by name the way the real instrumentation does, so one lookup covers every
    /// request type that happens to carry a <c>TopicArn</c> or a <c>QueueUrl</c>.
    /// </summary>
    private static string? GetStringProperty(object instance, string propertyName)
    {
        var property = PropertyCache.GetOrAdd(
            (instance.GetType(), propertyName),
            key => key.Type.GetProperty(key.Property, BindingFlags.Public | BindingFlags.Instance));

        return property?.PropertyType == typeof(string) ? property.GetValue(instance) as string : null;
    }

    /// <summary>
    /// Mirrors what OpenTelemetry.Instrumentation.AWS writes: a <c>traceparent</c> message attribute
    /// on SNS publishes, and an X-Ray <c>AWSTraceHeader</c> system attribute on SQS sends.
    /// </summary>
    private static void Propagate(Amazon.Runtime.AmazonWebServiceRequest request, Activity activity)
    {
        switch (request)
        {
            case Amazon.SimpleNotificationService.Model.PublishRequest publish:
                AddTraceParent(publish.MessageAttributes ??= [], activity);
                break;

            case Amazon.SimpleNotificationService.Model.PublishBatchRequest publishBatch:
                foreach (var entry in publishBatch.PublishBatchRequestEntries ?? [])
                {
                    AddTraceParent(entry.MessageAttributes ??= [], activity);
                }

                break;

            case Amazon.SQS.Model.SendMessageRequest send:
                AddTraceHeader(send.MessageSystemAttributes ??= [], activity);
                break;

            case Amazon.SQS.Model.SendMessageBatchRequest sendBatch:
                foreach (var entry in sendBatch.Entries ?? [])
                {
                    AddTraceHeader(entry.MessageSystemAttributes ??= [], activity);
                }

                break;

            default:
                break;
        }
    }

    // A caller that set its own trace context meant it; leave it be.
    private static void AddTraceParent(Dictionary<string, SnsMessageAttributeValue> attributes, Activity activity)
    {
        if (activity.Id is not null && !attributes.ContainsKey("traceparent"))
        {
            attributes["traceparent"] = new SnsMessageAttributeValue { DataType = "String", StringValue = activity.Id };
        }
    }

    private static void AddTraceHeader(Dictionary<string, SqsMessageSystemAttributeValue> attributes, Activity activity)
    {
        if (attributes.ContainsKey("AWSTraceHeader"))
        {
            return;
        }

        var traceId = activity.TraceId.ToHexString();
        var header =
            $"Root=1-{traceId[..8]}-{traceId[8..]};Parent={activity.SpanId.ToHexString()};Sampled={(activity.Recorded ? 1 : 0)}";

        attributes["AWSTraceHeader"] = new SqsMessageSystemAttributeValue { DataType = "String", StringValue = header };
    }
}
