using System.Diagnostics;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Shouldly;

namespace LocalSqsSnsMessaging.Tests;

/// <summary>
/// Covers the test-suite's own AWS SDK tracing: spans are always recorded and carry the AWS
/// semantic conventions, and trace context is written into outgoing messages unless a test asks for
/// that to be held back.
/// </summary>
public sealed class AwsSdkTracingTests : IDisposable
{
    private readonly InMemoryAwsBus _bus = new();
    private readonly AmazonSimpleNotificationServiceClient _sns;
    private readonly AmazonSQSClient _sqs;

    public AwsSdkTracingTests()
    {
        _sns = _bus.CreateSnsClient();
        _sqs = _bus.CreateSqsClient();
    }

    public void Dispose()
    {
        _sns.Dispose();
        _sqs.Dispose();
    }

    private async Task<string> PublishAndReceiveAsync(string name)
    {
        var topicArn = (await _sns.CreateTopicAsync(new CreateTopicRequest { Name = $"{name}-topic" })).TopicArn;
        var queueUrl = (await _sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = $"{name}-queue" })).QueueUrl;
        var queueArn = (await _sqs.GetQueueAttributesAsync(queueUrl, ["QueueArn"])).Attributes["QueueArn"];
        await _sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = queueArn,
            Attributes = new Dictionary<string, string> { ["RawMessageDelivery"] = "true" }
        });

        await _sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = "hello" });
        return queueUrl;
    }

    [Test]
    public async Task Publish_CarriesTraceContextByDefault()
    {
        var queueUrl = await PublishAndReceiveAsync("traced");

        var received = await _sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MessageAttributeNames = ["All"]
        });

        var message = received.Messages.ShouldNotBeNull().ShouldHaveSingleItem();
        message.MessageAttributes.ShouldContainKey("traceparent");
        message.MessageAttributes["traceparent"].StringValue.ShouldStartWith("00-");
    }

    [Test]
    public async Task Publish_UnderSuppression_SendsOnlyWhatTheTestWrote()
    {
        string queueUrl;
        using (AwsSdkTracing.SuppressPropagation())
        {
            queueUrl = await PublishAndReceiveAsync("untraced");
        }

        var received = await _sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MessageAttributeNames = ["All"]
        });

        var message = received.Messages.ShouldNotBeNull().ShouldHaveSingleItem();
        (message.MessageAttributes ?? []).ShouldNotContainKey("traceparent");
    }

    [Test]
    public async Task SendMessage_CarriesTheTraceHeaderByDefault_ButNotUnderSuppression()
    {
        var queueUrl = (await _sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "trace-header-queue" })).QueueUrl;

        await _sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = queueUrl, MessageBody = "traced" });
        using (AwsSdkTracing.SuppressPropagation())
        {
            await _sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = queueUrl, MessageBody = "untraced" });
        }

        var received = await _sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 2,
            MessageSystemAttributeNames = ["All"]
        });

        var messages = received.Messages.ShouldNotBeNull();
        var traced = messages.Single(m => m.Body == "traced");
        var untraced = messages.Single(m => m.Body == "untraced");

        traced.Attributes.ShouldContainKey("AWSTraceHeader");
        traced.Attributes["AWSTraceHeader"].ShouldStartWith("Root=1-");
        (untraced.Attributes ?? []).ShouldNotContainKey("AWSTraceHeader");
    }

    /// <summary>
    /// Collects the SDK spans produced while <paramref name="action"/> runs. Other tests share the
    /// process, so callers pick their own spans out by name.
    /// </summary>
    private static async Task<List<Activity>> CaptureSpansAsync(Func<Task> action)
    {
        var captured = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name.StartsWith("AWSSDK.", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => { lock (captured) { captured.Add(activity); } }
        };
        ActivitySource.AddActivityListener(listener);

        await action();

        lock (captured)
        {
            return [.. captured];
        }
    }

    [Test]
    public async Task PublishSpan_CarriesTheAwsSemanticConventions()
    {
        const string topicName = "semconv-topic";
        string topicArn = null!;

        var spans = await CaptureSpansAsync(async () =>
        {
            topicArn = (await _sns.CreateTopicAsync(new CreateTopicRequest { Name = topicName })).TopicArn;
            await _sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = "hello" });
        });

        var publish = spans.Single(s =>
            s.OperationName == "SNS.Publish" && (string?)s.GetTagItem("aws.sns.topic.arn") == topicArn);

        publish.Kind.ShouldBe(ActivityKind.Client);

        // From the SDK's own telemetry.
        publish.GetTagItem("rpc.system").ShouldBe("aws-api");
        publish.GetTagItem("rpc.service").ShouldBe("SNS");
        publish.GetTagItem("rpc.method").ShouldBe("Publish");
        publish.GetTagItem("aws.request_id").ShouldNotBeNull();

        // Added by the tracing handler, which knows what the call is about.
        publish.GetTagItem("messaging.system").ShouldBe("aws.sns");
        publish.GetTagItem("messaging.operation.name").ShouldBe("Publish");
        publish.GetTagItem("messaging.operation.type").ShouldBe("send");
        publish.GetTagItem("messaging.destination.name").ShouldBe(topicName);
        publish.GetTagItem("messaging.message.id").ShouldNotBeNull();
    }

    [Test]
    public async Task SendMessageSpan_CarriesTheQueueConventions_AndNestsTheHttpSpan()
    {
        const string queueName = "semconv-queue";
        string queueUrl = null!;

        var spans = await CaptureSpansAsync(async () =>
        {
            queueUrl = (await _sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = queueName })).QueueUrl;
            await _sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = queueUrl, MessageBody = "hello" });
        });

        var send = spans.Single(s =>
            s.OperationName == "SQS.SendMessage" && (string?)s.GetTagItem("aws.sqs.queue.url") == queueUrl);

        send.GetTagItem("rpc.service").ShouldBe("SQS");
        send.GetTagItem("messaging.system").ShouldBe("aws_sqs");
        send.GetTagItem("messaging.operation.type").ShouldBe("send");
        send.GetTagItem("messaging.destination.name").ShouldBe(queueName);
        send.GetTagItem("server.address").ShouldBe("sqs.us-east-1.amazonaws.com");

        // The SDK nests its own HTTP span inside the call, and that comes through too.
        var http = spans.Single(s => s.OperationName == "HttpRequest" && s.ParentSpanId == send.SpanId);
        http.GetTagItem("http.status_code").ShouldBe(200);
        http.GetTagItem("http.method").ShouldBe("POST");
    }

    [Test]
    public async Task Suppression_StopsThePropagationButKeepsTheSpans()
    {
        var spans = new List<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name.StartsWith("AWSSDK.", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => { lock (spans) { spans.Add(activity.OperationName); } }
        };
        ActivitySource.AddActivityListener(listener);

        using (AwsSdkTracing.SuppressPropagation())
        {
            await _sns.CreateTopicAsync(new CreateTopicRequest { Name = "span-topic" });
        }

        lock (spans)
        {
            spans.ShouldContain("SNS.CreateTopic");
        }
    }

    [Test]
    public async Task Suppression_UnwindsWhenTheScopeEnds()
    {
        using (AwsSdkTracing.SuppressPropagation())
        {
            AwsSdkTracing.PropagationIsSuppressed.ShouldBeTrue();
            await Task.Yield();
        }

        AwsSdkTracing.PropagationIsSuppressed.ShouldBeFalse();
    }

    [Test]
    public async Task Propagation_LeavesACallerSuppliedTraceHeaderAlone()
    {
        var queueUrl = (await _sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "explicit-header-queue" })).QueueUrl;
        const string header = "Root=1-5e3d83c1-e6a0db584850d61342823d4c";

        await _sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "body",
            MessageSystemAttributes = new Dictionary<string, Amazon.SQS.Model.MessageSystemAttributeValue>
            {
                ["AWSTraceHeader"] = new() { DataType = "String", StringValue = header }
            }
        });

        var received = await _sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MessageSystemAttributeNames = ["All"]
        });

        received.Messages.ShouldNotBeNull().ShouldHaveSingleItem().Attributes["AWSTraceHeader"].ShouldBe(header);
    }
}
