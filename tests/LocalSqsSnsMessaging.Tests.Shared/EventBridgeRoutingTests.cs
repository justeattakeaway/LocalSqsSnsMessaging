using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Shouldly;
using EventBridgeTarget = Amazon.EventBridge.Model.Target;

namespace LocalSqsSnsMessaging.Tests;

/// <summary>
/// Behavioural tests for EventBridge rule/target routing to SQS. Runs against the in-memory
/// implementation and, via the verification project, against Floci (and optionally real AWS),
/// so the in-memory behaviour is checked against a real EventBridge implementation.
/// </summary>
public abstract class EventBridgeRoutingTests : WaitingTestBase
{
    protected IAmazonEventBridge EventBridge = null!;
    protected IAmazonSQS Sqs = null!;
    protected string AccountId = null!;

    private async Task<(string QueueUrl, string QueueArn)> CreateTargetQueueAsync(string logicalName, CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = UniqueName(logicalName) }, cancellationToken)).QueueUrl;
        TrackQueueForTeardown(queueUrl);
        var queueArn = (await Sqs.GetQueueAttributesAsync(queueUrl, ["QueueArn"], cancellationToken)).Attributes["QueueArn"];

        if (IsRealAwsMode)
        {
            // Real EventBridge requires the target queue to grant events.amazonaws.com SendMessage.
            var policy = $$"""
            {"Version":"2012-10-17","Statement":[{"Effect":"Allow","Principal":{"Service":"events.amazonaws.com"},"Action":"sqs:SendMessage","Resource":"{{queueArn}}"}]}
            """;
            await Sqs.SetQueueAttributesAsync(queueUrl, new Dictionary<string, string> { ["Policy"] = policy }, cancellationToken);
        }

        return (queueUrl, queueArn);
    }

    [Test]
    public async Task MatchingEvent_IsDeliveredToSqsTarget(CancellationToken cancellationToken)
    {
        var (queueUrl, queueArn) = await CreateTargetQueueAsync("eb-orders", cancellationToken);
        var ruleName = UniqueName("orders-rule");

        await EventBridge.PutRuleAsync(new PutRuleRequest
        {
            Name = ruleName,
            EventPattern = """{"source":["my.orders"],"detail-type":["OrderPlaced"]}"""
        }, cancellationToken);
        await EventBridge.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = ruleName,
            Targets = [new EventBridgeTarget { Id = "t1", Arn = queueArn }]
        }, cancellationToken);

        var put = await EventBridge.PutEventsAsync(new PutEventsRequest
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
        }, cancellationToken);
        put.FailedEntryCount.ShouldBe(0);

        var messages = await ReceiveAllAsync(Sqs, queueUrl, expectedCount: 1, cancellationToken: cancellationToken);
        var message = messages.ShouldHaveSingleItem();

        // Assert the EventBridge envelope semantics (field order/precision vary between
        // implementations, so match on parsed content rather than exact bytes).
        using var doc = System.Text.Json.JsonDocument.Parse(message!.Body);
        var root = doc.RootElement;
        root.GetProperty("source").GetString().ShouldBe("my.orders");
        root.GetProperty("detail-type").GetString().ShouldBe("OrderPlaced");
        root.GetProperty("detail").GetProperty("orderId").GetString().ShouldBe("123");
    }

    [Test]
    public async Task NonMatchingEvent_IsFilteredOut(CancellationToken cancellationToken)
    {
        var (queueUrl, queueArn) = await CreateTargetQueueAsync("eb-filter", cancellationToken);
        var ruleName = UniqueName("filter-rule");

        await EventBridge.PutRuleAsync(new PutRuleRequest
        {
            Name = ruleName,
            EventPattern = """{"source":["match.me"]}"""
        }, cancellationToken);
        await EventBridge.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = ruleName,
            Targets = [new EventBridgeTarget { Id = "t1", Arn = queueArn }]
        }, cancellationToken);

        // Publish a non-matching event followed by a matching one. Only the matching event
        // should be delivered - proving the filter works without waiting on a negative.
        await EventBridge.PutEventsAsync(new PutEventsRequest
        {
            Entries =
            [
                new PutEventsRequestEntry { Source = "no.match", DetailType = "X", Detail = """{"n":1}""" },
                new PutEventsRequestEntry { Source = "match.me", DetailType = "X", Detail = """{"n":2}""" }
            ]
        }, cancellationToken);

        var messages = await ReceiveAllAsync(Sqs, queueUrl, expectedCount: 2, timeout: TimeSpan.FromSeconds(IsRealAwsMode ? 20 : 3), cancellationToken: cancellationToken);
        var message = messages.ShouldHaveSingleItem();

        using var doc = System.Text.Json.JsonDocument.Parse(message!.Body);
        doc.RootElement.GetProperty("source").GetString().ShouldBe("match.me");
    }

    [Test]
    public async Task PrefixPattern_Matches(CancellationToken cancellationToken)
    {
        var (queueUrl, queueArn) = await CreateTargetQueueAsync("eb-prefix", cancellationToken);
        var ruleName = UniqueName("prefix-rule");

        await EventBridge.PutRuleAsync(new PutRuleRequest
        {
            Name = ruleName,
            EventPattern = """{"source":[{"prefix":"my."}]}"""
        }, cancellationToken);
        await EventBridge.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = ruleName,
            Targets = [new EventBridgeTarget { Id = "t1", Arn = queueArn }]
        }, cancellationToken);

        await EventBridge.PutEventsAsync(new PutEventsRequest
        {
            Entries = [new PutEventsRequestEntry { Source = "my.service", DetailType = "X", Detail = "{}" }]
        }, cancellationToken);

        var messages = await ReceiveAllAsync(Sqs, queueUrl, expectedCount: 1, cancellationToken: cancellationToken);
        messages.ShouldHaveSingleItem();
    }

    [Test]
    public async Task RuleAndTargetManagement_RoundTrips(CancellationToken cancellationToken)
    {
        var (_, queueArn) = await CreateTargetQueueAsync("eb-mgmt", cancellationToken);
        var ruleName = UniqueName("mgmt-rule");

        await EventBridge.PutRuleAsync(new PutRuleRequest
        {
            Name = ruleName,
            EventPattern = """{"source":["my.orders"]}""",
            Description = "managed rule"
        }, cancellationToken);

        var describe = await EventBridge.DescribeRuleAsync(new DescribeRuleRequest { Name = ruleName }, cancellationToken);
        describe.Name.ShouldBe(ruleName);
        describe.State.ShouldBe(RuleState.ENABLED);
        describe.Arn.ShouldContain(":rule/");

        var rules = await EventBridge.ListRulesAsync(new ListRulesRequest { NamePrefix = ruleName }, cancellationToken);
        rules.Rules.ShouldContain(r => r.Name == ruleName);

        await EventBridge.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = ruleName,
            Targets = [new EventBridgeTarget { Id = "t1", Arn = queueArn }]
        }, cancellationToken);

        var targets = await EventBridge.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = ruleName }, cancellationToken);
        targets.Targets.ShouldHaveSingleItem();
        targets.Targets[0].Arn.ShouldBe(queueArn);

        await EventBridge.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = ruleName, Ids = ["t1"] }, cancellationToken);
        var afterRemove = await EventBridge.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = ruleName }, cancellationToken);
        (afterRemove.Targets ?? []).ShouldBeEmpty();

        await EventBridge.DeleteRuleAsync(new DeleteRuleRequest { Name = ruleName }, cancellationToken);
    }

    [Test]
    public async Task TestEventPattern_MatchesAndRejects(CancellationToken cancellationToken)
    {
        var match = await EventBridge.TestEventPatternAsync(new TestEventPatternRequest
        {
            EventPattern = """{"detail":{"state":["running"]}}""",
            Event = """{"id":"1","account":"123","source":"x","detail-type":"y","detail":{"state":"running"}}"""
        }, cancellationToken);
        match.Result.ShouldBe(true);

        var noMatch = await EventBridge.TestEventPatternAsync(new TestEventPatternRequest
        {
            EventPattern = """{"detail":{"state":["stopped"]}}""",
            Event = """{"id":"1","account":"123","source":"x","detail-type":"y","detail":{"state":"running"}}"""
        }, cancellationToken);
        noMatch.Result.ShouldBe(false);
    }
}
