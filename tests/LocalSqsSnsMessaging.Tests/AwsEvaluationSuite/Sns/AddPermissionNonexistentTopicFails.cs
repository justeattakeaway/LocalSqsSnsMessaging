// Translated from behavioral/sns/check_add_permission_nonexistent_topic_fails.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.add-permission-nonexistent-topic-fails</summary>
[Category("AwsEvaluationSuite")]
public sealed class AddPermissionNonexistentTopicFails
{
    /// <summary>
    /// AddPermission on a deleted topic fails NotFound, while the identical call succeeds on the
    /// live topic and again once it is recreated under the same name (and so the same ARN).
    /// </summary>
    [Test]
    [DisplayName("sns.add-permission-nonexistent-topic-fails")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var accountId = ctx.Account;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-topic-");
        const string label = "sim2real-probe-permission";

        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = name })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));

        Task<AddPermissionResponse> AddPermission(string targetArn) => sns.AddPermissionAsync(new AddPermissionRequest
        {
            TopicArn = targetArn,
            Label = label,
            AWSAccountId = [accountId],
            ActionName = ["Publish"],
        });

        var controlError = (await FidelityContext.CaptureError(() => AddPermission(arn)))?.Code;
        Ensure(controlError is null,
            $"positive control failed: AddPermission on a live topic should succeed, got '{controlError}'");
        Ensure((await PolicySids(sns, arn)).Contains(label),
            $"expected '{label}' in the live topic's policy statements after AddPermission");

        await sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn });
        var (goneCode, goneMessage) = await RejectOrRaise(() => sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = arn }));
        Ensure(goneCode == "NotFound", $"expected NotFound reading a deleted topic, got '{goneCode}'");
        Ensure(goneMessage == "Topic does not exist",
            $"unexpected NotFound message reading a deleted topic: '{goneMessage}'");

        var (rejectCode, rejectMessage) = await RejectOrRaise(() => AddPermission(arn));
        Ensure(rejectCode == "NotFound", $"expected NotFound, got '{rejectCode}'");
        Ensure(rejectMessage == "Topic does not exist",
            $"unexpected NotFound message for AddPermission on a deleted topic: '{rejectMessage}'");

        // SNS topic ARNs are deterministic on account+region+name, so recreating reuses the ARN.
        var newArn = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = name })).TopicArn;
        Ensure(newArn == arn, $"expected the recreated topic to reuse the ARN, got '{newArn}' vs '{arn}'");

        var restoreError = (await FidelityContext.CaptureError(() => AddPermission(newArn)))?.Code;
        Ensure(restoreError is null,
            $"expected AddPermission to succeed again once the topic was recreated, got '{restoreError}'");
        Ensure((await PolicySids(sns, newArn)).Contains(label),
            $"expected '{label}' in the recreated topic's policy statements after AddPermission");
    }

    /// <summary>
    /// Call fn(); return botocore (Error.Code, Error.Message) of its ClientError.
    ///
    /// An unexpected success raises.
    /// </summary>
    private static async Task<(string? Code, string? Message)> RejectOrRaise<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be rejected, but it SUCCEEDED and returned {result}");
        }

        return (error.Code, error.Message);
    }

    /// <summary>Statement ids present in the topic's access policy.</summary>
    private static async Task<HashSet<string?>> PolicySids(AmazonSimpleNotificationServiceClient sns, string arn)
    {
        var policy = JsonNode.Parse((await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = arn })).Attributes["Policy"]);
        return (policy?["Statement"]?.AsArray() ?? []).Select(stmt => (string?)stmt?["Sid"]).ToHashSet();
    }
}
