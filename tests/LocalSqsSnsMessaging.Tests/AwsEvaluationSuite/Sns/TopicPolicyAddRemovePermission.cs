// Translated from behavioral/sns/check_topic_policy_add_remove_permission.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.topic-policy-add-remove-permission</summary>
[Category("AwsEvaluationSuite")]
public sealed class TopicPolicyAddRemovePermission
{
    /// <summary>AddPermission inserts a statement (by Label) into the topic policy; RemovePermission deletes it.</summary>
    [Test]
    [DisplayName("sns.topic-policy-add-remove-permission")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Sns;
        var topicArn = await CreateTopic(ev, ctx, FidelityContext.UniqueName("sim2real-behav-sns-perm-"),
            tags: [new() { Key = "env", Value = "fidelity" }]);
        const string label = "fidelity-check-statement";
        await ev.AddPermissionAsync(new AddPermissionRequest
        {
            TopicArn = topicArn,
            Label = label,
            AWSAccountId = [ctx.Account],
            ActionName = ["Publish"],
        });
        var policy = JsonNode.Parse((await ev.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = topicArn })).Attributes!["Policy"])!;
        var sids = Sids(policy);
        Ensure(sids.Contains(label), $"expected statement labeled '{label}' in topic policy, got {Show(sids)}");
        await ev.RemovePermissionAsync(new RemovePermissionRequest { TopicArn = topicArn, Label = label });
        var policyAfter = JsonNode.Parse((await ev.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = topicArn })).Attributes!["Policy"])!;
        var sidsAfter = Sids(policyAfter);
        Ensure(!sidsAfter.Contains(label), $"expected statement '{label}' removed from policy, still present: {Show(sidsAfter)}");
    }

    private static HashSet<string?> Sids(JsonNode policy) =>
        [.. (policy["Statement"] as JsonArray ?? []).Select(stmt => (string?)stmt?["Sid"])];

    private static string Show(HashSet<string?> sids) =>
        sids.Count == 0 ? "set()" : $"{{{string.Join(", ", sids.Select(s => s is null ? "None" : $"'{s}'"))}}}";

    /// <summary>CreateTopic + register best-effort DeleteTopic cleanup; returns the TopicArn.</summary>
    private static async Task<string> CreateTopic(AmazonSimpleNotificationServiceClient client, FidelityContext ctx, string name,
        Dictionary<string, string>? attributes = null, List<Amazon.SimpleNotificationService.Model.Tag>? tags = null)
    {
        var request = new CreateTopicRequest { Name = name };
        if (attributes is { Count: > 0 })
        {
            request.Attributes = attributes;
        }

        if (tags is { Count: > 0 })
        {
            request.Tags = tags;
        }

        var arn = (await client.CreateTopicAsync(request)).TopicArn;
        ctx.OnCleanup(() => client.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }
}
