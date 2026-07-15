using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.SQS;
using Shouldly;
using EventBridgeTarget = Amazon.EventBridge.Model.Target;

namespace LocalSqsSnsMessaging.Tests.EventBridge;

/// <summary>
/// Unit tests for EventBridge target-input transformation and for the client's
/// error paths and less-common operations.
/// </summary>
public sealed class EventBridgeDeliveryAndClientTests
{
    private static async Task<(AmazonEventBridgeClient Eb, AmazonSQSClient Sqs, string QueueUrl, string QueueArn)> SetupAsync(string queueName = "q")
    {
        var bus = new InMemoryAwsBus();
        var eb = bus.CreateEventBridgeClient();
        var sqs = bus.CreateSqsClient();
        var queueUrl = (await sqs.CreateQueueAsync(queueName)).QueueUrl;
        var queueArn = (await sqs.GetQueueAttributesAsync(queueUrl, ["QueueArn"])).Attributes["QueueArn"];
        return (eb, sqs, queueUrl, queueArn);
    }

    private static async Task RuleWithTargetAsync(AmazonEventBridgeClient eb, EventBridgeTarget target, string pattern = """{"source":["s"]}""")
    {
        await eb.PutRuleAsync(new PutRuleRequest { Name = "r", EventPattern = pattern });
        await eb.PutTargetsAsync(new PutTargetsRequest { Rule = "r", Targets = [target] });
    }

    private static PutEventsRequest Event(string detail = """{"orderId":"123"}""") => new()
    {
        Entries = [new PutEventsRequestEntry { Source = "s", DetailType = "t", Detail = detail }]
    };

    // ---- input transformation ----

    [Test]
    public async Task StaticInput_ReplacesBody()
    {
        var (eb, sqs, queueUrl, queueArn) = await SetupAsync();
        using var _1 = eb;
        using var _2 = sqs;
        await RuleWithTargetAsync(eb, new EventBridgeTarget { Id = "t1", Arn = queueArn, Input = """{"fixed":true}""" });

        await eb.PutEventsAsync(Event());

        var msg = (await sqs.ReceiveMessageAsync(queueUrl)).Messages.ShouldHaveSingleItem();
        msg.Body.ShouldBe("""{"fixed":true}""");
    }

    [Test]
    public async Task InputPath_SelectsSubtree()
    {
        var (eb, sqs, queueUrl, queueArn) = await SetupAsync();
        using var _1 = eb;
        using var _2 = sqs;
        await RuleWithTargetAsync(eb, new EventBridgeTarget { Id = "t1", Arn = queueArn, InputPath = "$.detail" });

        await eb.PutEventsAsync(Event());

        var msg = (await sqs.ReceiveMessageAsync(queueUrl)).Messages.ShouldHaveSingleItem();
        msg.Body.ShouldContain("orderId");
        msg.Body.ShouldNotContain("detail-type");
    }

    [Test]
    public async Task InputTransformer_RendersTemplate()
    {
        var (eb, sqs, queueUrl, queueArn) = await SetupAsync();
        using var _1 = eb;
        using var _2 = sqs;
        await RuleWithTargetAsync(eb, new EventBridgeTarget
        {
            Id = "t1",
            Arn = queueArn,
            InputTransformer = new InputTransformer
            {
                InputPathsMap = new Dictionary<string, string> { ["id"] = "$.detail.orderId" },
                InputTemplate = """{"orderNumber":"<id>"}"""
            }
        });

        await eb.PutEventsAsync(Event());

        var msg = (await sqs.ReceiveMessageAsync(queueUrl)).Messages.ShouldHaveSingleItem();
        msg.Body.ShouldBe("""{"orderNumber":"123"}""");
    }

    [Test]
    public async Task NonSqsTarget_IsSkipped_WhileSqsTargetStillReceives()
    {
        var (eb, sqs, queueUrl, queueArn) = await SetupAsync();
        using var _1 = eb;
        using var _2 = sqs;

        await eb.PutRuleAsync(new PutRuleRequest { Name = "r", EventPattern = """{"source":["s"]}""" });
        await eb.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = "r",
            Targets =
            [
                new EventBridgeTarget { Id = "lambda", Arn = "arn:aws:lambda:us-east-1:000000000000:function:fn" },
                new EventBridgeTarget { Id = "missing-queue", Arn = "arn:aws:sqs:us-east-1:000000000000:does-not-exist" },
                new EventBridgeTarget { Id = "real-queue", Arn = queueArn }
            ]
        });

        await eb.PutEventsAsync(Event());

        (await sqs.ReceiveMessageAsync(queueUrl)).Messages.ShouldHaveSingleItem();
    }

    [Test]
    public async Task MalformedDetail_StillDeliversWithEmptyDetail()
    {
        var (eb, sqs, queueUrl, queueArn) = await SetupAsync();
        using var _1 = eb;
        using var _2 = sqs;
        await RuleWithTargetAsync(eb, new EventBridgeTarget { Id = "t1", Arn = queueArn });

        await eb.PutEventsAsync(Event(detail: "not-json"));

        var msg = (await sqs.ReceiveMessageAsync(queueUrl)).Messages.ShouldHaveSingleItem();
        using var doc = System.Text.Json.JsonDocument.Parse(msg.Body);
        doc.RootElement.GetProperty("detail").GetRawText().ShouldBe("{}");
    }

    [Test]
    public async Task FifoQueueTarget_DeliversWithMessageGroup()
    {
        var bus = new InMemoryAwsBus();
        using var eb = bus.CreateEventBridgeClient();
        using var sqs = bus.CreateSqsClient();
        var queueUrl = (await sqs.CreateQueueAsync(new Amazon.SQS.Model.CreateQueueRequest
        {
            QueueName = "q.fifo",
            Attributes = new Dictionary<string, string> { ["FifoQueue"] = "true" }
        })).QueueUrl;
        var queueArn = (await sqs.GetQueueAttributesAsync(queueUrl, ["QueueArn"])).Attributes["QueueArn"];

        await eb.PutRuleAsync(new PutRuleRequest { Name = "r", EventPattern = """{"source":["s"]}""" });
        await eb.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = "r",
            Targets = [new EventBridgeTarget { Id = "t1", Arn = queueArn, SqsParameters = new SqsParameters { MessageGroupId = "g1" } }]
        });

        await eb.PutEventsAsync(Event());

        var msg = (await sqs.ReceiveMessageAsync(new Amazon.SQS.Model.ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MessageSystemAttributeNames = ["All"]
        })).Messages.ShouldHaveSingleItem();
        msg.Body.ShouldContain("orderId");
    }

    // ---- client error paths & less-common ops ----

    [Test]
    public async Task PutRule_InvalidPattern_Throws()
    {
        var (eb, sqs, _, _) = await SetupAsync();
        using var _1 = eb;
        using var _2 = sqs;
        await Should.ThrowAsync<InvalidEventPatternException>(
            eb.PutRuleAsync(new PutRuleRequest { Name = "bad", EventPattern = "not json" }));
    }

    [Test]
    public async Task DescribeRule_Missing_Throws()
    {
        var (eb, sqs, _, _) = await SetupAsync();
        using var _1 = eb;
        using var _2 = sqs;
        await Should.ThrowAsync<ResourceNotFoundException>(
            eb.DescribeRuleAsync(new DescribeRuleRequest { Name = "nope" }));
    }

    [Test]
    public async Task PutTargets_MissingRule_Throws()
    {
        var (eb, sqs, _, queueArn) = await SetupAsync();
        using var _1 = eb;
        using var _2 = sqs;
        await Should.ThrowAsync<ResourceNotFoundException>(
            eb.PutTargetsAsync(new PutTargetsRequest { Rule = "nope", Targets = [new EventBridgeTarget { Id = "t", Arn = queueArn }] }));
    }

    [Test]
    public async Task PutRule_OnMissingCustomBus_Throws()
    {
        var (eb, sqs, _, _) = await SetupAsync();
        using var _1 = eb;
        using var _2 = sqs;
        await Should.ThrowAsync<ResourceNotFoundException>(
            eb.PutRuleAsync(new PutRuleRequest { Name = "r", EventBusName = "no-such-bus", EventPattern = """{"source":["s"]}""" }));
    }

    [Test]
    public async Task CreateEventBus_Duplicate_Throws()
    {
        var (eb, sqs, _, _) = await SetupAsync();
        using var _1 = eb;
        using var _2 = sqs;
        await eb.CreateEventBusAsync(new CreateEventBusRequest { Name = "dup" });
        await Should.ThrowAsync<ResourceAlreadyExistsException>(
            eb.CreateEventBusAsync(new CreateEventBusRequest { Name = "dup" }));
    }

    [Test]
    public async Task EventBus_Describe_List_Delete()
    {
        var (eb, sqs, _, _) = await SetupAsync();
        using var _1 = eb;
        using var _2 = sqs;

        await eb.CreateEventBusAsync(new CreateEventBusRequest { Name = "custom", Description = "d" });

        var describe = await eb.DescribeEventBusAsync(new DescribeEventBusRequest { Name = "custom" });
        describe.Name.ShouldBe("custom");
        describe.Arn.ShouldContain("event-bus/custom");

        var list = await eb.ListEventBusesAsync(new ListEventBusesRequest());
        list.EventBuses.ShouldContain(b => b.Name == "custom");
        list.EventBuses.ShouldContain(b => b.Name == "default"); // default always present

        await eb.DeleteEventBusAsync(new DeleteEventBusRequest { Name = "custom" });
        (await eb.ListEventBusesAsync(new ListEventBusesRequest())).EventBuses.ShouldNotContain(b => b.Name == "custom");
    }

    [Test]
    public async Task ListRuleNamesByTarget_FindsRule()
    {
        var (eb, sqs, _, queueArn) = await SetupAsync();
        using var _1 = eb;
        using var _2 = sqs;
        await RuleWithTargetAsync(eb, new EventBridgeTarget { Id = "t1", Arn = queueArn });

        var names = await eb.ListRuleNamesByTargetAsync(new ListRuleNamesByTargetRequest { TargetArn = queueArn });
        names.RuleNames.ShouldContain("r");
    }

    [Test]
    public async Task EnableDisableRule_TogglesState()
    {
        var (eb, sqs, _, _) = await SetupAsync();
        using var _1 = eb;
        using var _2 = sqs;
        await eb.PutRuleAsync(new PutRuleRequest { Name = "r", EventPattern = """{"source":["s"]}""" });

        await eb.DisableRuleAsync(new DisableRuleRequest { Name = "r" });
        (await eb.DescribeRuleAsync(new DescribeRuleRequest { Name = "r" })).State.ShouldBe(RuleState.DISABLED);

        await eb.EnableRuleAsync(new EnableRuleRequest { Name = "r" });
        (await eb.DescribeRuleAsync(new DescribeRuleRequest { Name = "r" })).State.ShouldBe(RuleState.ENABLED);
    }
}
