// Translated from behavioral/events/check_input_transformer_array_paths_render_elements_slices_and_null_exactly.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SqsMessage = Amazon.SQS.Model.Message;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.input-transformer-array-paths-render-elements-slices-and-null-exactly</summary>
[Category("AwsEvaluationSuite")]
public sealed class InputTransformerArrayPathsRenderElementsSlicesAndNullExactly
{
    private const string Check = "input-transformer-array-paths-render-elements-slices-and-null-exactly";
    private const double LivenessTimeoutS = 120.0;
    private const double DeliveryTimeoutS = 90.0;

    /// <summary><c>json.dumps({**_DETAIL, "m": marker})</c>, byte for byte.</summary>
    private static string Detail(string m) =>
        $$"""{"items": [10, 20, 30], "grid": [[1, 2], [3, 4]], "objs": [{"id": "o1", "v": 1}, {"id": "o2"}], "empty": [], "nul": null, "m": "{{m}}"}""";

    private static readonly Dictionary<string, string> Paths = new()
    {
        ["items"] = "$.detail.items",
        ["second"] = "$.detail.items[1]",
        ["beyond"] = "$.detail.items[7]",
        ["cell"] = "$.detail.grid[1][0]",
        ["grid"] = "$.detail.grid",
        ["firstId"] = "$.detail.objs[0].id",
        ["ids"] = "$.detail.objs[*].id",
        ["objs"] = "$.detail.objs",
        ["empty"] = "$.detail.empty",
        ["nul"] = "$.detail.nul",
    };

    private const string JsonTemplate =
        "{\"whole\": <items>, \"wholeText\": \"<items>\", \"second\": <second>, \"beyond\": <beyond>, " +
        "\"cell\": <cell>, \"grid\": <grid>, \"firstId\": <firstId>, \"ids\": <ids>, \"empty\": <empty>, " +
        "\"nul\": <nul>, \"nulText\": \"<nul>\", \"m\": <m>}";

    private static JsonObject ExpectedJson() => new()
    {
        ["whole"] = new JsonArray(10, 20, 30),
        ["wholeText"] = "[10,20,30]",
        ["second"] = 20,
        ["beyond"] = "",
        ["cell"] = 3,
        ["grid"] = new JsonArray(new JsonArray(1, 2), new JsonArray(3, 4)),
        ["firstId"] = "o1",
        ["ids"] = new JsonArray("o1", "o2"),
        ["empty"] = new JsonArray(),
        ["nul"] = null,
        ["nulText"] = "null",
    };

    private const string TextTemplate = "\"<m> ids=<ids> objs=<objs> second=<second> empty=<empty>\"";

    /// <summary>
    /// InputTransformer array paths (whole array, index, nested index, out-of-range index, [*]
    /// projection, empty array, JSON null) render exactly as EventBridge does in JSON and plain-text
    /// templates.
    ///
    /// Reference: https://docs.aws.amazon.com/eventbridge/latest/userguide/eb-transform-target-input.html
    /// </summary>
    [Test]
    [DisplayName("events.input-transformer-array-paths-render-elements-slices-and-null-exactly")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgeInput, "an InputPathsMap index past the end of the array ($.detail.items[7]) fails PutEvents with ArgumentOutOfRangeException instead of rendering empty")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;

        var bus = await CreateBus(ctx, events);
        var source = FidelityContext.UniqueName("sim2real.arrays.");
        var rule = (await CreateRule(ctx, events, bus, $$"""{"source": ["{{source}}"]}""")).Split('/')[^1];
        var (queueUrl, queueArn) = await CreateQueue(ctx);
        var paths = new Dictionary<string, string>(Paths) { ["m"] = "$.detail.m" };
        var put = await events.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = rule,
            EventBusName = bus,
            Targets =
            [
                new Target { Id = "raw", Arn = queueArn },
                new Target { Id = "json", Arn = queueArn, InputTransformer = new InputTransformer { InputPathsMap = paths, InputTemplate = JsonTemplate } },
                new Target { Id = "text", Arn = queueArn, InputTransformer = new InputTransformer { InputPathsMap = paths, InputTemplate = TextTemplate } },
            ],
        });
        Ensure(put.FailedEntryCount == 0, $"PutTargets failed: {FailedEntries(put)}");

        var live = FidelityContext.UniqueName("live-");
        await PublishUntilLive(ctx, events, queueUrl, new PutEventsRequestEntry
        {
            EventBusName = bus,
            Source = source,
            DetailType = "live",
            Detail = Detail(live),
        }, msgs => msgs.Any(m => m.Body.Contains(live, StringComparison.Ordinal)));

        var marker = FidelityContext.UniqueName("arr-");
        var resp = await events.PutEventsAsync(new PutEventsRequest
        {
            Entries = [new PutEventsRequestEntry { EventBusName = bus, Source = source, DetailType = "arrays", Detail = Detail(marker) }],
        });
        Ensure(resp.FailedEntryCount == 0, $"PutEvents refused the event: {string.Join(", ", (resp.Entries ?? []).Select(e => $"({e.EventId}, {e.ErrorCode}, {e.ErrorMessage})"))}");

        Dictionary<string, JsonNode> Forms(List<SqsMessage> msgs)
        {
            var output = new Dictionary<string, JsonNode>();
            foreach (var msg in msgs)
            {
                var body = JsonNode.Parse(msg.Body)!;
                if (body is JsonValue v && v.TryGetValue<string>(out var text))
                {
                    if (text.StartsWith(marker + " ", StringComparison.Ordinal))
                    {
                        output["text"] = body;
                    }
                }
                else if (IsString(body.AsObject()["m"], marker))
                {
                    output["json"] = body;
                }
                else if (body["detail"] is JsonObject detail && IsString(detail["m"], marker))
                {
                    output["raw"] = body;
                }
            }

            return output;
        }

        var msgs = await Drain(ctx, queueUrl, until: ms => Forms(ms).Count == 3,
            timeoutS: DeliveryTimeoutS, description: $"event {marker} reaching all 3 targets");
        var forms = Forms(msgs);

        Ensure(
            JsonNode.DeepEquals(forms["raw"]["detail"], JsonNode.Parse(Detail(marker))),
            $"control: the raw target received detail {forms["raw"]["detail"]?.ToJsonString()}");
        var rendered = forms["json"].DeepClone().AsObject();
        rendered.Remove("m");
        var expectedJson = ExpectedJson();
        Ensure(
            JsonNode.DeepEquals(rendered, expectedJson),
            $"the JSON template rendered {rendered.ToJsonString()}; expected {expectedJson.ToJsonString()}");
        var expectedText = $"{marker} ids=[o1,o2] objs=[{{id:o1,v:1}},{{id:o2}}] second=20 empty=[]";
        var renderedText = (string?)forms["text"];
        Ensure(
            renderedText == expectedText,
            $"the plain-text template rendered '{renderedText}'; expected '{expectedText}'");
    }

    private static bool IsString(JsonNode? node, string expected) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && s == expected;

    private static string FailedEntries(PutTargetsResponse put) =>
        $"[{string.Join(", ", (put.FailedEntries ?? []).Select(f => $"{{'TargetId': '{f.TargetId}', 'ErrorCode': '{f.ErrorCode}', 'ErrorMessage': '{f.ErrorMessage}'}}"))}]";

    private static async Task<string> CreateBus(FidelityContext ctx, AmazonEventBridgeClient events)
    {
        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");
        await events.CreateEventBusAsync(new CreateEventBusRequest { Name = bus, Tags = [new Amazon.EventBridge.Model.Tag { Key = "sim2real", Value = Check }] });
        ctx.OnCleanup(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));
        return bus;
    }

    /// <summary>A rule whose teardown first removes whatever targets it still carries.</summary>
    private static async Task<string> CreateRule(FidelityContext ctx, AmazonEventBridgeClient events, string bus, string pattern)
    {
        var name = FidelityContext.UniqueName("sim2real-behav-rule-");
        var arn = (await events.PutRuleAsync(new PutRuleRequest { Name = name, EventBusName = bus, EventPattern = pattern, State = RuleState.ENABLED })).RuleArn;

        async Task Delete()
        {
            var ids = ((await events.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = name, EventBusName = bus })).Targets
                ?? throw new KeyNotFoundException("'Targets'")).Select(t => t.Id).ToList();
            if (ids.Count > 0)
            {
                await events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = name, EventBusName = bus, Ids = ids });
            }

            await events.DeleteRuleAsync(new DeleteRuleRequest { Name = name, EventBusName = bus });
        }

        ctx.OnCleanup(Delete);
        return arn;
    }

    /// <summary>A queue EventBridge may send to, as (url, arn).</summary>
    private static async Task<(string Url, string Arn)> CreateQueue(FidelityContext ctx, Dictionary<string, string>? attributes = null)
    {
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-q-");
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Attributes = attributes ?? [], Tags = new() { ["sim2real"] = Check } })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];
        var policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Effect"] = "Allow",
                ["Principal"] = new JsonObject { ["Service"] = "events.amazonaws.com" },
                ["Action"] = "sqs:SendMessage",
                ["Resource"] = arn,
                ["Condition"] = new JsonObject { ["StringEquals"] = new JsonObject { ["aws:SourceAccount"] = ctx.Account } },
            }),
        };
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = url, Attributes = new() { ["Policy"] = policy.ToJsonString() } });
        return (url, arn);
    }

    /// <summary>Receive (and delete) until <c>until(messages_so_far)</c> holds; raise on timeout.</summary>
    private static async Task<List<SqsMessage>> Drain(
        FidelityContext ctx, string queueUrl, Func<List<SqsMessage>, bool> until, double timeoutS, string description)
    {
        var sqs = ctx.Sqs;
        var seen = new List<SqsMessage>();
        var started = FidelityTime.StartNew();
        while (started.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
#pragma warning disable CS0618 // The check deliberately uses the legacy AttributeNames parameter.
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 5,
                AttributeNames = ["All"],
                MessageAttributeNames = ["All"],
            });
#pragma warning restore CS0618
            foreach (var msg in resp.Messages ?? [])
            {
                seen.Add(msg);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
            }

            if (until(seen))
            {
                return seen;
            }
        }

        throw Fail(
            $"{description}: not met within {timeoutS}s; received {seen.Count} message(s): " +
            $"[{string.Join(", ", seen.Select(m => $"'{m.Body[..Math.Min(300, m.Body.Length)]}'"))}]");
    }

    /// <summary>
    /// Re-publish <c>entry</c> until the queue shows it: a just-created rule can drop events until
    /// its routing is live. Bounded; raises naming the timeout.
    /// </summary>
    private static async Task PublishUntilLive(
        FidelityContext ctx, AmazonEventBridgeClient events, string queueUrl, PutEventsRequestEntry entry, Func<List<SqsMessage>, bool> arrived)
    {
        var sqs = ctx.Sqs;
        var seen = new List<SqsMessage>();
        var started = FidelityTime.StartNew();
        while (started.Elapsed < TimeSpan.FromSeconds(LivenessTimeoutS))
        {
            await events.PutEventsAsync(new PutEventsRequest { Entries = [entry] });
            var window = started.Elapsed + TimeSpan.FromSeconds(15.0);
            while (started.Elapsed < window)
            {
                var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
                foreach (var msg in resp.Messages ?? [])
                {
                    seen.Add(msg);
                    await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                }

                if (arrived(seen))
                {
                    return;
                }
            }
        }

        throw Fail(
            $"the rule never delivered a liveness event within {LivenessTimeoutS}s; " +
            $"{seen.Count} other message(s) seen");
    }
}
