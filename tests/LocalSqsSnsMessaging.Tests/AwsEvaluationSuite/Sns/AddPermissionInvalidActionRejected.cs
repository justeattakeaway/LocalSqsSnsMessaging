// Translated from behavioral/sns/check_add_permission_invalid_action_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.add-permission-invalid-action-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class AddPermissionInvalidActionRejected
{
    private const string Check = "add-permission-invalid-action-rejected";

    /// <summary>
    /// AddPermission must reject an ActionName that is not a real SNS action, and must not
    /// insert a policy statement for the rejected attempt — while a real action is still accepted
    /// on the same topic (positive control).
    /// </summary>
    [Test]
    [DisplayName("sns.add-permission-invalid-action-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Sns;
        var accountId = ctx.Account;
        var topicArn = await CreateTopic(
            ev, ctx, FidelityContext.UniqueName("sim2real-behav-sns-badaction-"),
            tags: [new() { Key = "sim2real", Value = Check }, new() { Key = "sim2real:managed", Value = "true" }]);

        var goodLabel = FidelityContext.UniqueName("sim2real-behav-goodaction-");
        await ev.AddPermissionAsync(new AddPermissionRequest { TopicArn = topicArn, Label = goodLabel, AWSAccountId = [accountId], ActionName = ["Publish"] });
        var sids = await PolicySids(ev, topicArn);
        Ensure(sids.Contains(goodLabel),
            $"positive control failed: expected label '{goodLabel}' for a real ActionName in policy {ShowSet(sids)}");

        var badLabel = FidelityContext.UniqueName("sim2real-behav-badaction-");
        var (code, message) = await Error(
            () => ev.AddPermissionAsync(new AddPermissionRequest
            {
                TopicArn = topicArn,
                Label = badLabel,
                AWSAccountId = [accountId],
                ActionName = ["NotARealSnsAction"],
            }));
        Ensure(code == "InvalidParameter", $"expected InvalidParameter for a bogus ActionName, got {code}");
        Ensure(message == "Invalid parameter: Policy statement action out of service scope!",
            $"unexpected InvalidParameter message for a bogus ActionName: '{message}'");

        sids = await PolicySids(ev, topicArn);
        Ensure(!sids.Contains(badLabel),
            $"rejected AddPermission must not insert a statement: found '{badLabel}' in policy {ShowSet(sids)}");
    }

    private static string ShowSet(IEnumerable<string?> values) =>
        "{" + string.Join(", ", values.Select(v => v is null ? "None" : $"'{v}'")) + "}";

    /// <summary>CreateTopic + register best-effort DeleteTopic cleanup; returns the TopicArn.</summary>
    private static async Task<string> CreateTopic(AmazonSimpleNotificationServiceClient client, FidelityContext ctx, string name,
        Dictionary<string, string>? attributes = null, List<Tag>? tags = null)
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

    /// <summary>Statement Sids currently in the topic's access policy (independent read).</summary>
    private static async Task<HashSet<string?>> PolicySids(AmazonSimpleNotificationServiceClient client, string topicArn)
    {
        var policy = JsonNode.Parse((await client.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = topicArn })).Attributes["Policy"])!;
        return (policy["Statement"]?.AsArray() ?? []).Select(stmt => (string?)stmt?["Sid"]).ToHashSet();
    }

    /// <summary>(Code, Message) of the ClientError fn() raises; an unexpected success raises.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be rejected, but it SUCCEEDED and returned {result}");
        }

        return (error.Code, error.Message);
    }
}
