// Translated from behavioral/events/check_recreated_event_bus_inherits_no_tags_policy_or_description.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.recreated-event-bus-inherits-no-tags-policy-or-description</summary>
[Category("AwsEvaluationSuite")]
public sealed class RecreatedEventBusInheritsNoTagsPolicyOrDescription
{
    private const string Check = "recreated-event-bus-inherits-no-tags-policy-or-description";

    /// <summary>
    /// A custom bus deleted and recreated under the same name gets the same ARN but none of the old
    /// tags, resource policy or description; only what the second CreateEventBus supplied is present.
    /// </summary>
    [Test]
    [DisplayName("events.recreated-event-bus-inherits-no-tags-policy-or-description")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");

        var firstArn = (await events.CreateEventBusAsync(new CreateEventBusRequest
        {
            Name = bus,
            Description = "sim2real first incarnation",
            Tags = [new Tag { Key = "sim2real", Value = Check }, new Tag { Key = "generation", Value = "1" }],
        })).EventBusArn;
        ctx.OnCleanup(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));
        await events.TagResourceAsync(new TagResourceRequest { ResourceARN = firstArn, Tags = [new Tag { Key = "generation", Value = "1-retagged" }] });
        await events.PutPermissionAsync(new PutPermissionRequest
        {
            EventBusName = bus,
            Action = "events:PutEvents",
            Principal = "111122223333",
            StatementId = "sim2real-first",
        });

        var first = await events.DescribeEventBusAsync(new DescribeEventBusRequest { Name = bus });
        Ensure(first.Description == "sim2real first incarnation", $"first Description {Repr(first.Description)}");
        var policy = first.Policy ?? throw new KeyNotFoundException("'Policy'");
        var sids = JsonNode.Parse(policy)!["Statement"]!.AsArray().Select(s => (string?)s!["Sid"]).ToList();
        Ensure(
            sids.Count == 1 && sids[0] == "sim2real-first",
            "positive control: the first bus must carry the granted statement");
        var firstTags = await Tags(events, firstArn);
        Ensure(
            DictEquals(firstTags, new() { ["sim2real"] = Check, ["generation"] = "1-retagged" }),
            $"TagResource with an existing key must overwrite its value; tags are {Show(firstTags)}");

        await events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus });
        var secondArn = (await events.CreateEventBusAsync(new CreateEventBusRequest
        {
            Name = bus,
            Tags = [new Tag { Key = "generation", Value = "2" }],
        })).EventBusArn;
        Ensure(secondArn == firstArn, "a recreated bus has the same name-derived ARN");

        var second = await events.DescribeEventBusAsync(new DescribeEventBusRequest { Name = bus });
        Ensure(second.Policy is null, $"the recreated bus must not inherit the old policy, got {Repr(second.Policy)}");
        Ensure(
            second.Description is null,
            $"the recreated bus must not inherit the old Description, got {Repr(second.Description)}");
        var secondTags = await Tags(events, secondArn);
        Ensure(
            DictEquals(secondTags, new() { ["generation"] = "2" }),
            $"the recreated bus must carry only the tags its own CreateEventBus supplied, got {Show(secondTags)}");
    }

    // Python indexes ["Tags"], which raises KeyError when the response omits it.
    private static async Task<Dictionary<string, string>> Tags(AmazonEventBridgeClient events, string arn) =>
        ((await events.ListTagsForResourceAsync(new ListTagsForResourceRequest { ResourceARN = arn })).Tags
            ?? throw new KeyNotFoundException("'Tags'"))
        .ToDictionary(t => t.Key, t => t.Value);

    private static bool DictEquals(Dictionary<string, string> a, Dictionary<string, string> b)
        => a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    private static string Show(Dictionary<string, string> d) => "{" + string.Join(", ", d.Select(kv => $"'{kv.Key}': '{kv.Value}'")) + "}";

    private static string Repr(string? value) => value is null ? "None" : $"'{value}'";
}
