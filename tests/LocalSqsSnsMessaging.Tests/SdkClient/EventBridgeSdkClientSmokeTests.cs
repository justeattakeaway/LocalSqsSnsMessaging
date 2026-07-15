using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.SQS;
using Shouldly;
using Target = Amazon.EventBridge.Model.Target;

namespace LocalSqsSnsMessaging.Tests.SdkClient;

/// <summary>
/// End-to-end smoke tests for the SDK EventBridge client using the HTTP message handler,
/// mirroring the way AWS.Messaging publishes to EventBridge and consumes from SQS.
/// </summary>
public sealed class EventBridgeSdkClientSmokeTests
{
    private static async Task<(InMemoryAwsBus Bus, AmazonEventBridgeClient Eb, AmazonSQSClient Sqs, string QueueUrl, string QueueArn)> SetupAsync()
    {
        var bus = new InMemoryAwsBus();
        var eb = bus.CreateEventBridgeClient();
        var sqs = bus.CreateSqsClient();

        var queueUrl = (await sqs.CreateQueueAsync("orders-queue")).QueueUrl;
        var queueArn = (await sqs.GetQueueAttributesAsync(queueUrl, ["QueueArn"])).Attributes["QueueArn"];
        return (bus, eb, sqs, queueUrl, queueArn);
    }

    [Test]
    public async Task PutEvents_MatchingRule_DeliversEnvelopeToSqsTarget()
    {
        var (_, eb, sqs, queueUrl, queueArn) = await SetupAsync();
        using var _1 = eb;
        using var _2 = sqs;

        await eb.PutRuleAsync(new PutRuleRequest
        {
            Name = "orders-rule",
            EventPattern = """{"source":["my.orders"],"detail-type":["OrderPlaced"]}"""
        });
        await eb.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = "orders-rule",
            Targets = [new Target { Id = "queue-target", Arn = queueArn }]
        });

        var putResponse = await eb.PutEventsAsync(new PutEventsRequest
        {
            Entries =
            [
                new PutEventsRequestEntry
                {
                    Source = "my.orders",
                    DetailType = "OrderPlaced",
                    Detail = """{"orderId":"123"}"""
                }
            ]
        });

        putResponse.FailedEntryCount.ShouldBe(0);
        putResponse.Entries.ShouldHaveSingleItem();
        putResponse.Entries[0].EventId.ShouldNotBeNullOrEmpty();

        var receive = await sqs.ReceiveMessageAsync(queueUrl);
        receive.Messages.ShouldHaveSingleItem();

        var body = receive.Messages[0].Body;
        body.ShouldContain("\"detail-type\":\"OrderPlaced\"");
        body.ShouldContain("\"source\":\"my.orders\"");
        body.ShouldContain("\"orderId\":\"123\"");
    }

    [Test]
    public async Task PutEvents_NonMatchingEvent_DeliversNothing()
    {
        var (_, eb, sqs, queueUrl, queueArn) = await SetupAsync();
        using var _1 = eb;
        using var _2 = sqs;

        await eb.PutRuleAsync(new PutRuleRequest
        {
            Name = "orders-rule",
            EventPattern = """{"source":["my.orders"]}"""
        });
        await eb.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = "orders-rule",
            Targets = [new Target { Id = "queue-target", Arn = queueArn }]
        });

        await eb.PutEventsAsync(new PutEventsRequest
        {
            Entries = [new PutEventsRequestEntry { Source = "other.source", DetailType = "X", Detail = "{}" }]
        });

        var receive = await sqs.ReceiveMessageAsync(queueUrl);
        (receive.Messages ?? []).ShouldBeEmpty();
    }

    [Test]
    public async Task PutEvents_DisabledRule_DeliversNothing()
    {
        var (_, eb, sqs, queueUrl, queueArn) = await SetupAsync();
        using var _1 = eb;
        using var _2 = sqs;

        await eb.PutRuleAsync(new PutRuleRequest
        {
            Name = "orders-rule",
            EventPattern = """{"source":["my.orders"]}"""
        });
        await eb.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = "orders-rule",
            Targets = [new Target { Id = "queue-target", Arn = queueArn }]
        });
        await eb.DisableRuleAsync(new DisableRuleRequest { Name = "orders-rule" });

        await eb.PutEventsAsync(new PutEventsRequest
        {
            Entries = [new PutEventsRequestEntry { Source = "my.orders", DetailType = "X", Detail = "{}" }]
        });

        var receive = await sqs.ReceiveMessageAsync(queueUrl);
        (receive.Messages ?? []).ShouldBeEmpty();
    }

    [Test]
    public async Task RuleAndTargetManagement_RoundTrips()
    {
        var (_, eb, sqs, _, queueArn) = await SetupAsync();
        using var _1 = eb;
        using var _2 = sqs;

        await eb.PutRuleAsync(new PutRuleRequest
        {
            Name = "orders-rule",
            EventPattern = """{"source":["my.orders"]}""",
            Description = "orders"
        });
        await eb.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = "orders-rule",
            Targets = [new Target { Id = "t1", Arn = queueArn }]
        });

        var describe = await eb.DescribeRuleAsync(new DescribeRuleRequest { Name = "orders-rule" });
        describe.Name.ShouldBe("orders-rule");
        describe.State.ShouldBe(RuleState.ENABLED);
        describe.Arn.ShouldContain(":rule/orders-rule");

        var rules = await eb.ListRulesAsync(new ListRulesRequest());
        rules.Rules.ShouldContain(r => r.Name == "orders-rule");

        var targets = await eb.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = "orders-rule" });
        targets.Targets.ShouldHaveSingleItem();
        targets.Targets[0].Arn.ShouldBe(queueArn);

        await eb.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = "orders-rule", Ids = ["t1"] });
        ((await eb.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = "orders-rule" })).Targets ?? []).ShouldBeEmpty();

        await eb.DeleteRuleAsync(new DeleteRuleRequest { Name = "orders-rule" });
        ((await eb.ListRulesAsync(new ListRulesRequest())).Rules ?? []).ShouldBeEmpty();
    }

    [Test]
    public async Task CustomEventBus_RoutesIndependentlyOfDefault()
    {
        var (_, eb, sqs, queueUrl, queueArn) = await SetupAsync();
        using var _1 = eb;
        using var _2 = sqs;

        await eb.CreateEventBusAsync(new CreateEventBusRequest { Name = "custom-bus" });
        await eb.PutRuleAsync(new PutRuleRequest
        {
            Name = "custom-rule",
            EventBusName = "custom-bus",
            EventPattern = """{"source":["my.orders"]}"""
        });
        await eb.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = "custom-rule",
            EventBusName = "custom-bus",
            Targets = [new Target { Id = "t1", Arn = queueArn }]
        });

        // Event on the default bus should NOT match the custom bus rule.
        await eb.PutEventsAsync(new PutEventsRequest
        {
            Entries = [new PutEventsRequestEntry { Source = "my.orders", DetailType = "X", Detail = "{}" }]
        });
        ((await sqs.ReceiveMessageAsync(queueUrl)).Messages ?? []).ShouldBeEmpty();

        // Event on the custom bus should match.
        await eb.PutEventsAsync(new PutEventsRequest
        {
            Entries = [new PutEventsRequestEntry { Source = "my.orders", DetailType = "X", Detail = "{}", EventBusName = "custom-bus" }]
        });
        (await sqs.ReceiveMessageAsync(queueUrl)).Messages.ShouldHaveSingleItem();
    }

    [Test]
    public async Task TestEventPattern_ReturnsMatchResult()
    {
        var bus = new InMemoryAwsBus();
        using var eb = bus.CreateEventBridgeClient();

        var match = await eb.TestEventPatternAsync(new TestEventPatternRequest
        {
            EventPattern = """{"detail":{"state":["running"]}}""",
            Event = """{"source":"x","detail-type":"y","detail":{"state":"running"}}"""
        });
        match.Result.ShouldBe(true);

        var noMatch = await eb.TestEventPatternAsync(new TestEventPatternRequest
        {
            EventPattern = """{"detail":{"state":["stopped"]}}""",
            Event = """{"source":"x","detail-type":"y","detail":{"state":"running"}}"""
        });
        noMatch.Result.ShouldBe(false);
    }
}
