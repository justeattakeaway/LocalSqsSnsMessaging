// Translated from behavioral/sns/check_remove_permission_unknown_label_is_idempotent.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.remove-permission-unknown-label-is-idempotent</summary>
[Category("AwsEvaluationSuite")]
public sealed class RemovePermissionUnknownLabelIsIdempotent
{
    private const string Check = "remove-permission-unknown-label-is-idempotent";

    /// <summary>
    /// RemovePermission for a label that was never added is a no-op success in real SNS, and
    /// leaves an existing, unrelated permission statement untouched.
    /// </summary>
    [Test]
    [DisplayName("sns.remove-permission-unknown-label-is-idempotent")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var arn = await CreateTopic(ctx);
        var accountId = ctx.Account;
        var canaryLabel = FidelityContext.UniqueName("sim2real-behav-sns-canary-");

        // Positive control: a real permission exists, proven present via an independent read.
        await sns.AddPermissionAsync(new AddPermissionRequest
        {
            TopicArn = arn,
            Label = canaryLabel,
            AWSAccountId = [accountId],
            ActionName = ["Publish"],
        });
        var sidsBefore = await StatementSids(sns, arn);
        Ensure(sidsBefore.Contains(canaryLabel),
            $"expected canary label '{canaryLabel}' in topic policy before the no-op, got {Repr(sidsBefore)}");

        // RemovePermission for an unknown label succeeds silently; a refusal raises here.
        await sns.RemovePermissionAsync(new RemovePermissionRequest { TopicArn = arn, Label = "never-added-label-xyz" });

        // Terminal read: an independent GetTopicAttributes call, distinct from RemovePermission,
        // proving the no-op left the unrelated statement's presence exactly unchanged.
        var sidsAfter = await StatementSids(sns, arn);
        Ensure(sidsAfter.SetEquals(sidsBefore),
            "RemovePermission on an unknown label must not disturb existing statements: " +
            $"before={Repr(sidsBefore)} after={Repr(sidsAfter)}");
    }

    private static string Repr(IEnumerable<string?> items) => "{" + string.Join(", ", items.Select(s => s is null ? "None" : $"'{s}'")) + "}";

    /// <summary>Create a tagged topic, register its teardown, and return its ARN.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx, string? name = null)
    {
        var sns = ctx.Sns;
        name ??= FidelityContext.UniqueName("sim2real-behav-sns-topic-");
        List<Amazon.SimpleNotificationService.Model.Tag> tagsKv =
            [new() { Key = "sim2real", Value = Check }, new() { Key = "sim2real:managed", Value = "true" }];
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Tags = tagsKv })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>Read the topic's policy statements (Sid set) via an independent GetTopicAttributes read.</summary>
    private static async Task<HashSet<string?>> StatementSids(AmazonSimpleNotificationServiceClient sns, string arn)
    {
        var policy = JsonNode.Parse((await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = arn })).Attributes["Policy"])!;
        return ((policy["Statement"] as JsonArray) ?? []).Select(stmt => (string?)stmt?["Sid"]).ToHashSet();
    }
}
