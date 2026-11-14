// Translated from behavioral/sns/check_add_permission_merges_into_custom_policy.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.add-permission-merges-into-custom-policy</summary>
[Category("AwsEvaluationSuite")]
public sealed class AddPermissionMergesIntoCustomPolicy
{
    private const string Check = "add-permission-merges-into-custom-policy";

    /// <summary>
    /// AddPermission appends a statement granting the named actions to the account on this topic
    /// to a custom SetTopicAttributes policy, leaving it intact; RemovePermission removes only that
    /// statement.
    /// </summary>
    [Test]
    [DisplayName("sns.add-permission-merges-into-custom-policy")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName("sim2real-behav-sns-polmerge-"),
            Tags = [new() { Key = "sim2real", Value = Check }],
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        var account = arn.Split(':')[4];

        var custom = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Sid"] = "CustomOwnerGetAttributes",
                ["Effect"] = "Allow",
                ["Principal"] = new JsonObject { ["AWS"] = $"arn:aws:iam::{account}:root" },
                ["Action"] = "SNS:GetTopicAttributes",
                ["Resource"] = arn,
            }),
        };
        await sns.SetTopicAttributesAsync(new SetTopicAttributesRequest { TopicArn = arn, AttributeName = "Policy", AttributeValue = custom.ToJsonString() });
        Ensure((await Statements(sns, arn)).Keys.ToHashSet().SetEquals(["CustomOwnerGetAttributes"]), "the custom policy must replace the default");

        var label = FidelityContext.UniqueName("sim2real-behav-lbl-");
        await sns.AddPermissionAsync(new AddPermissionRequest
        {
            TopicArn = arn,
            Label = label,
            AWSAccountId = [account],
            ActionName = ["Publish", "Subscribe"],
        });
        var stmts = await Statements(sns, arn);
        Ensure(stmts.Keys.ToHashSet().SetEquals(["CustomOwnerGetAttributes", label]),
            $"AddPermission must append to the custom policy, got Sids {SortedRepr(stmts.Keys)}");
        var added = stmts[label];
        var actions = StringList(added?["Action"]).Order(StringComparer.Ordinal).ToList();
        var principal = StringList(added?["Principal"]?["AWS"]);
        Ensure((string?)added?["Effect"] == "Allow", $"the added statement's Effect is '{added?["Effect"]}'");
        Ensure(actions.SequenceEqual(["SNS:Publish", "SNS:Subscribe"]), $"the added statement's actions are {SortedRepr(actions)}");
        Ensure(principal.SequenceEqual([$"arn:aws:iam::{account}:root"]), "the added statement must name the account root principal");
        Ensure((string?)added?["Resource"] == arn, "the added statement's Resource must be this topic");
        Ensure(stmts["CustomOwnerGetAttributes"]?["Action"] is JsonValue a && a.TryGetValue<string>(out var s) && s == "SNS:GetTopicAttributes",
            "AddPermission must leave the custom statement unchanged");

        await sns.RemovePermissionAsync(new RemovePermissionRequest { TopicArn = arn, Label = label });
        Ensure((await Statements(sns, arn)).Keys.ToHashSet().SetEquals(["CustomOwnerGetAttributes"]),
            $"RemovePermission must remove only its statement, got {SortedRepr((await Statements(sns, arn)).Keys)}");
    }

    private static async Task<Dictionary<string, JsonNode?>> Statements(AmazonSimpleNotificationServiceClient sns, string arn)
    {
        var policy = (await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = arn })).Attributes["Policy"];
        var statements = JsonNode.Parse(policy)?["Statement"]?.AsArray() ?? [];
        var result = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var s in statements)
        {
            result[(string?)s?["Sid"] ?? "None"] = s;
        }

        return result;
    }

    /// <summary>A string-or-list policy element as a list (None stays a one-element list, as in Python).</summary>
    private static List<string?> StringList(JsonNode? node) =>
        node is JsonArray array ? array.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?.ToJsonString()).ToList()
        : [node is JsonValue value && value.TryGetValue<string>(out var str) ? str : node?.ToJsonString()];

    private static string SortedRepr(IEnumerable<string?> values) =>
        $"[{string.Join(", ", values.Order(StringComparer.Ordinal).Select(v => v is null ? "None" : $"'{v}'"))}]";
}
