using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.SimpleNotificationService;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using EventBridgeTarget = Amazon.EventBridge.Model.Target;

namespace LocalSqsSnsMessaging.Tests.EventBridge;

/// <summary>
/// Delivery to EventBridge targets: dead-lettering failed deliveries, queue and topic policies,
/// SNS topic and event bus targets, FIFO queues and the X-Ray trace header.
/// </summary>
public sealed class EventBridgeTargetDeliveryTests : IDisposable
{
    private const string Pattern = """{"source":["s"]}""";

    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly InMemoryAwsBus _bus;
    private readonly AmazonEventBridgeClient _eb;
    private readonly AmazonSQSClient _sqs;
    private readonly AmazonSimpleNotificationServiceClient _sns;

    public EventBridgeTargetDeliveryTests()
    {
        _bus = new InMemoryAwsBus { TimeProvider = _time };
        _eb = _bus.CreateEventBridgeClient();
        _sqs = _bus.CreateSqsClient();
        _sns = _bus.CreateSnsClient();
    }

    public void Dispose()
    {
        _eb.Dispose();
        _sqs.Dispose();
        _sns.Dispose();
    }

    private async Task<(string Url, string Arn)> QueueAsync(string name, Dictionary<string, string>? attributes = null)
    {
        var url = (await _sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Attributes = attributes ?? [] })).QueueUrl;
        var arn = (await _sqs.GetQueueAttributesAsync(url, ["QueueArn"])).Attributes["QueueArn"];
        return (url, arn);
    }

    private async Task<string> RuleAsync(string name, params EventBridgeTarget[] targets) =>
        await RuleOnBusAsync(null, name, targets);

    private async Task<string> RuleOnBusAsync(string? busName, string name, params EventBridgeTarget[] targets)
    {
        var arn = (await _eb.PutRuleAsync(new PutRuleRequest { Name = name, EventBusName = busName, EventPattern = Pattern })).RuleArn;
        await _eb.PutTargetsAsync(new PutTargetsRequest { Rule = name, EventBusName = busName, Targets = [.. targets] });
        return arn;
    }

    private async Task<string> PutEventAsync(string? busName = null, string? traceHeader = null, string detail = """{"n":1}""")
    {
        var response = await _eb.PutEventsAsync(new PutEventsRequest
        {
            Entries = [new PutEventsRequestEntry { EventBusName = busName, Source = "s", DetailType = "t", Detail = detail, TraceHeader = traceHeader }]
        });
        return response.Entries[0].EventId;
    }

    private async Task<List<Message>> ReceiveAllAsync(string url) =>
        (await _sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = url,
            MaxNumberOfMessages = 10,
            MessageAttributeNames = ["All"],
            MessageSystemAttributeNames = ["All"],
        })).Messages ?? [];

    private async Task SetPolicyAsync(string url, string arn, string effect, string sourceArn) =>
        await _sqs.SetQueueAttributesAsync(url, new Dictionary<string, string> { ["Policy"] = Policy(effect, "sqs:SendMessage", arn, sourceArn) });

    private static string Policy(string effect, string action, string resource, string sourceArn) => $$"""
        {"Version":"2012-10-17","Statement":[{"Effect":"{{effect}}","Principal":{"Service":"events.amazonaws.com"},
         "Action":"{{action}}","Resource":"{{resource}}","Condition":{"ArnEquals":{"aws:SourceArn":"{{sourceArn}}" } } }]}
        """;

    private static Dictionary<string, string> Attributes(Message message) =>
        message.MessageAttributes.ToDictionary(a => a.Key, a => a.Value.StringValue, StringComparer.Ordinal);

    [Test]
    public async Task DeletedQueueTarget_DeadLettersAsNoResource_WhileSiblingDelivers()
    {
        var victim = await QueueAsync("victim");
        var healthy = await QueueAsync("healthy");
        var dlq = await QueueAsync("dlq");
        var ruleArn = await RuleAsync("r",
            new EventBridgeTarget { Id = "victim", Arn = victim.Arn, DeadLetterConfig = new DeadLetterConfig { Arn = dlq.Arn } },
            new EventBridgeTarget { Id = "healthy", Arn = healthy.Arn, DeadLetterConfig = new DeadLetterConfig { Arn = dlq.Arn } });
        await _sqs.DeleteQueueAsync(victim.Url);

        var eventId = await PutEventAsync();

        var delivered = (await ReceiveAllAsync(healthy.Url)).ShouldHaveSingleItem();
        var deadLetter = (await ReceiveAllAsync(dlq.Url)).ShouldHaveSingleItem();
        deadLetter.Body.ShouldBe(delivered.Body);
        JsonNode.Parse(deadLetter.Body)!["id"]!.GetValue<string>().ShouldBe(eventId);
        var attributes = Attributes(deadLetter);
        attributes.Keys.Order(StringComparer.Ordinal).ShouldBe(["ERROR_CODE", "ERROR_MESSAGE", "RULE_ARN", "TARGET_ARN"]);
        attributes["ERROR_CODE"].ShouldBe("NO_RESOURCE");
        attributes["ERROR_MESSAGE"].ShouldStartWith(
            "The specified queue does not exist or you do not have access to it. (Service: AmazonSQS; Status Code: 400; " +
            "Error Code: AWS.SimpleQueueService.NonExistentQueue; Request ID: ");
        attributes["RULE_ARN"].ShouldBe(ruleArn);
        attributes["TARGET_ARN"].ShouldBe(victim.Arn);
    }

    [Test]
    public async Task FailedTargetWithoutDeadLetterConfig_DropsTheEvent()
    {
        var victim = await QueueAsync("victim");
        var dlq = await QueueAsync("dlq");
        await RuleAsync("r", new EventBridgeTarget { Id = "victim", Arn = victim.Arn });
        await _sqs.DeleteQueueAsync(victim.Url);

        await PutEventAsync();

        (await ReceiveAllAsync(dlq.Url)).ShouldBeEmpty();
    }

    [Test]
    public async Task QueuePolicy_DenyingEventBridge_DeadLettersAsNoResource()
    {
        var denied = await QueueAsync("denied");
        var dlq = await QueueAsync("dlq");
        var ruleArn = await RuleAsync("r",
            new EventBridgeTarget { Id = "denied", Arn = denied.Arn, DeadLetterConfig = new DeadLetterConfig { Arn = dlq.Arn } });
        await SetPolicyAsync(denied.Url, denied.Arn, "Deny", ruleArn);

        await PutEventAsync();

        (await ReceiveAllAsync(denied.Url)).ShouldBeEmpty();
        Attributes((await ReceiveAllAsync(dlq.Url)).ShouldHaveSingleItem())["ERROR_CODE"].ShouldBe("NO_RESOURCE");

        // Replacing the Deny with an Allow lets the next event land.
        await SetPolicyAsync(denied.Url, denied.Arn, "Allow", ruleArn);
        await PutEventAsync();
        (await ReceiveAllAsync(denied.Url)).ShouldHaveSingleItem();
    }

    [Test]
    public async Task QueuePolicy_GrantingAnotherRule_RefusesThisRule_WhileNoPolicyStillReceives()
    {
        var granted = await QueueAsync("granted");
        var bare = await QueueAsync("bare");
        await RuleAsync("r", new EventBridgeTarget { Id = "granted", Arn = granted.Arn }, new EventBridgeTarget { Id = "bare", Arn = bare.Arn });
        await SetPolicyAsync(granted.Url, granted.Arn, "Allow", "arn:aws:events:us-east-1:000000000000:rule/other");

        await PutEventAsync();

        (await ReceiveAllAsync(granted.Url)).ShouldBeEmpty();
        (await ReceiveAllAsync(bare.Url)).ShouldHaveSingleItem();
    }

    [Test]
    public async Task DisabledRule_NeitherDeliversNorDeadLetters()
    {
        var victim = await QueueAsync("victim");
        var dlq = await QueueAsync("dlq");
        await RuleAsync("r", new EventBridgeTarget { Id = "victim", Arn = victim.Arn, DeadLetterConfig = new DeadLetterConfig { Arn = dlq.Arn } });
        await _sqs.DeleteQueueAsync(victim.Url);
        await _eb.DisableRuleAsync(new DisableRuleRequest { Name = "r" });

        await PutEventAsync();

        (await ReceiveAllAsync(dlq.Url)).ShouldBeEmpty();
    }

    [Test]
    public async Task EachMatchingRule_DeliversItsOwnCopy()
    {
        var queue = await QueueAsync("q");
        await RuleAsync("r1", new EventBridgeTarget { Id = "q", Arn = queue.Arn });
        await RuleAsync("r2", new EventBridgeTarget { Id = "q", Arn = queue.Arn });

        var eventId = await PutEventAsync();

        var copies = await ReceiveAllAsync(queue.Url);
        copies.Count.ShouldBe(2);
        copies.ShouldAllBe(m => JsonNode.Parse(m.Body)!["id"]!.GetValue<string>() == eventId);
    }

    [Test]
    public async Task QueueDelaySeconds_AppliesToDeliveries()
    {
        var queue = await QueueAsync("delayed", new Dictionary<string, string> { ["DelaySeconds"] = "5" });
        await RuleAsync("r", new EventBridgeTarget { Id = "q", Arn = queue.Arn });

        await PutEventAsync();

        (await ReceiveAllAsync(queue.Url)).ShouldBeEmpty();
        _time.Advance(TimeSpan.FromSeconds(5));
        (await ReceiveAllAsync(queue.Url)).ShouldHaveSingleItem();
    }

    [Test]
    public async Task FifoQueueTarget_KeepsTheGroupInOrder()
    {
        var queue = await QueueAsync("q.fifo", new Dictionary<string, string> { ["FifoQueue"] = "true" });
        await RuleAsync("r", new EventBridgeTarget { Id = "q", Arn = queue.Arn, SqsParameters = new SqsParameters { MessageGroupId = "g" } });

        var ids = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            ids.Add(await PutEventAsync(detail: $$"""{"n":{{i}}}"""));
        }

        var received = await ReceiveAllAsync(queue.Url);
        received.Select(m => JsonNode.Parse(m.Body)!["id"]!.GetValue<string>()).ShouldBe(ids);
        received.ShouldAllBe(m => m.Attributes["MessageGroupId"] == "g");
    }

    [Test]
    public async Task TraceHeader_ReachesSqsTargetsAsAwsTraceHeader_UnlessMalformed()
    {
        const string trace = "Root=1-5759e988-bd862e3fe1be46a994272793;Parent=53995c3f42cd8ad8;Sampled=1";
        var queue = await QueueAsync("q");
        await RuleAsync("r", new EventBridgeTarget { Id = "q", Arn = queue.Arn });

        await PutEventAsync(traceHeader: trace);
        (await ReceiveAllAsync(queue.Url)).ShouldHaveSingleItem().Attributes["AWSTraceHeader"].ShouldBe(trace);

        await PutEventAsync(traceHeader: "not-a-trace-header");
        (await ReceiveAllAsync(queue.Url)).ShouldHaveSingleItem().Attributes.ShouldNotContainKey("AWSTraceHeader");
    }

    [Test]
    public async Task SnsTopicTarget_PublishesTheCompactEnvelopeToTheTopicsSubscriptions()
    {
        var topicArn = (await _sns.CreateTopicAsync("topic")).TopicArn;
        var subscriber = await QueueAsync("subscriber");
        await _sns.SubscribeAsync(topicArn, "sqs", subscriber.Arn);
        await RuleAsync("r", new EventBridgeTarget { Id = "topic", Arn = topicArn });

        var eventId = await PutEventAsync();

        var notification = JsonNode.Parse((await ReceiveAllAsync(subscriber.Url)).ShouldHaveSingleItem().Body)!.AsObject();
        notification["Type"]!.GetValue<string>().ShouldBe("Notification");
        notification["TopicArn"]!.GetValue<string>().ShouldBe(topicArn);
        notification.ContainsKey("Subject").ShouldBeFalse();
        notification.ContainsKey("MessageAttributes").ShouldBeFalse();
        var message = notification["Message"]!.GetValue<string>();
        JsonNode.Parse(message)!["id"]!.GetValue<string>().ShouldBe(eventId);
        message.ShouldNotContain(" ");
    }

    [Test]
    public async Task SnsTopicTarget_WhosePolicyDeniesEventBridge_DeadLettersAsNoPermissions()
    {
        var topicArn = (await _sns.CreateTopicAsync("topic")).TopicArn;
        var subscriber = await QueueAsync("subscriber");
        await _sns.SubscribeAsync(topicArn, "sqs", subscriber.Arn);
        var dlq = await QueueAsync("dlq");
        var ruleArn = await RuleAsync("r", new EventBridgeTarget { Id = "topic", Arn = topicArn, DeadLetterConfig = new DeadLetterConfig { Arn = dlq.Arn } });
        await _sns.SetTopicAttributesAsync(topicArn, "Policy", Policy("Deny", "sns:Publish", topicArn, ruleArn));

        await PutEventAsync();

        (await ReceiveAllAsync(subscriber.Url)).ShouldBeEmpty();
        var attributes = Attributes((await ReceiveAllAsync(dlq.Url)).ShouldHaveSingleItem());
        attributes["ERROR_CODE"].ShouldBe("NO_PERMISSIONS");
        attributes["ERROR_MESSAGE"].ShouldStartWith(
            $"User: events.amazonaws.com is not authorized to perform: SNS:Publish on resource: {topicArn} " +
            "with an explicit deny in a resource-based policy (Service: AmazonSNS; Status Code: 403; Error Code: AuthorizationError; ");
        attributes["TARGET_ARN"].ShouldBe(topicArn);
    }

    /// <summary>EventBridge requires a role on event bus targets; the bus stores it and never assumes it.</summary>
    private const string ForwardingRoleArn = "arn:aws:iam::000000000000:role/forward-events";

    [Test]
    public async Task EventBusTarget_ForwardsTheEnvelopeUnchanged_OnlyOnce()
    {
        var busB = (await _eb.CreateEventBusAsync(new CreateEventBusRequest { Name = "b" })).EventBusArn;
        var busC = (await _eb.CreateEventBusAsync(new CreateEventBusRequest { Name = "c" })).EventBusArn;
        var onB = await QueueAsync("on-b");
        var onC = await QueueAsync("on-c");
        await RuleAsync("default-to-b", new EventBridgeTarget { Id = "b", Arn = busB, RoleArn = ForwardingRoleArn });
        await RuleOnBusAsync("b", "b-to-c", new EventBridgeTarget { Id = "c", Arn = busC, RoleArn = ForwardingRoleArn });
        await RuleOnBusAsync("b", "b-to-queue", new EventBridgeTarget { Id = "q", Arn = onB.Arn });
        await RuleOnBusAsync("c", "c-to-queue", new EventBridgeTarget { Id = "q", Arn = onC.Arn });

        var eventId = await PutEventAsync();

        var forwarded = JsonNode.Parse((await ReceiveAllAsync(onB.Url)).ShouldHaveSingleItem().Body)!;
        forwarded["id"]!.GetValue<string>().ShouldBe(eventId);
        forwarded["source"]!.GetValue<string>().ShouldBe("s");
        (await ReceiveAllAsync(onC.Url)).ShouldBeEmpty();

        // An event put on B directly is forwarded to C.
        await PutEventAsync(busName: "b");
        (await ReceiveAllAsync(onC.Url)).ShouldHaveSingleItem();
    }

    [Test]
    public async Task MissingEventBusTarget_DeadLettersAsNoResource()
    {
        var dlq = await QueueAsync("dlq");
        await RuleAsync("r", new EventBridgeTarget
        {
            Id = "bus",
            Arn = "arn:aws:events:us-east-1:000000000000:event-bus/missing",
            RoleArn = ForwardingRoleArn,
            DeadLetterConfig = new DeadLetterConfig { Arn = dlq.Arn },
        });

        await PutEventAsync();

        var deadLetter = (await ReceiveAllAsync(dlq.Url)).ShouldHaveSingleItem();
        Attributes(deadLetter)["ERROR_CODE"].ShouldBe("NO_RESOURCE");
        JsonDocument.Parse(deadLetter.Body).RootElement.GetProperty("source").GetString().ShouldBe("s");
    }
}
