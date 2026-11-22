// Translated from behavioral/sns/check_remove_permission_nonexistent_topic_fails.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Text.Json;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.remove-permission-nonexistent-topic-fails</summary>
[Category("AwsEvaluationSuite")]
public sealed class RemovePermissionNonexistentTopicFails
{
    /// <summary>
    /// RemovePermission on a deleted topic fails NotFound, while the identical call still works
    /// on a live topic before the delete and again once the topic is recreated under the same name.
    /// </summary>
    [Test]
    [DisplayName("sns.remove-permission-nonexistent-topic-fails")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var accountId = ctx.Account;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-rmperm-");
        const string label = "sim2real-probe-removable";
        var arn = (await sns.CreateTopicAsync(name)).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(arn));

        // Positive control: grant the statement, then remove it from the live topic, proven by an
        // independent read of the policy rather than the mutating call's own response.
        await sns.AddPermissionAsync(new AddPermissionRequest { TopicArn = arn, Label = label, AWSAccountId = [accountId], ActionName = ["Publish"] });
        Ensure((await StatementSids(sns, arn)).Contains(label), $"expected {Py.Repr(label)} present after AddPermission on the live topic");
        await sns.RemovePermissionAsync(new RemovePermissionRequest { TopicArn = arn, Label = label });
        Ensure(!(await StatementSids(sns, arn)).Contains(label), $"expected {Py.Repr(label)} gone after RemovePermission on the live topic");

        // Break exactly one thing: the topic itself is gone.
        await sns.DeleteTopicAsync(arn);
        var (goneCode, goneMessage) = await Error(() => sns.GetTopicAttributesAsync(arn));
        Ensure(goneCode == "NotFound", $"expected NotFound reading a deleted topic, got {Py.Repr(goneCode)}");
        Ensure(goneMessage == "Topic does not exist", $"unexpected NotFound message reading a deleted topic: {Py.Repr(goneMessage)}");

        var (rejectCode, rejectMessage) = await Error(() => sns.RemovePermissionAsync(new RemovePermissionRequest { TopicArn = arn, Label = label }));
        Ensure(rejectCode == "NotFound", $"expected NotFound for RemovePermission on a deleted topic, got {Py.Repr(rejectCode)}");
        Ensure(rejectMessage == "Topic does not exist", $"unexpected message for RemovePermission on a deleted topic: {Py.Repr(rejectMessage)}");

        // Restore: SNS topic ARNs are deterministic on account+region+name, so recreating reuses it.
        var newArn = (await sns.CreateTopicAsync(name)).TopicArn;
        Ensure(newArn == arn, $"expected the recreated topic to reuse the ARN, got {Py.Repr(newArn)} vs {Py.Repr(arn)}");
        await sns.AddPermissionAsync(new AddPermissionRequest { TopicArn = newArn, Label = label, AWSAccountId = [accountId], ActionName = ["Publish"] });
        Ensure((await StatementSids(sns, newArn)).Contains(label), $"expected {Py.Repr(label)} present after AddPermission on the recreated topic");
        await sns.RemovePermissionAsync(new RemovePermissionRequest { TopicArn = newArn, Label = label });
        Ensure(!(await StatementSids(sns, newArn)).Contains(label),
            $"expected RemovePermission to succeed again on the recreated topic, but {Py.Repr(label)} is still present");
    }

    /// <summary>The set of Sid values present in the topic's access policy, read independently.</summary>
    private static async Task<HashSet<string?>> StatementSids(AmazonSimpleNotificationServiceClient sns, string arn)
    {
        var policy = (await sns.GetTopicAttributesAsync(arn)).Attributes!["Policy"];
        using var doc = JsonDocument.Parse(policy);
        return doc.RootElement.TryGetProperty("Statement", out var statements)
            ? statements.EnumerateArray().Select(s => s.TryGetProperty("Sid", out var sid) ? sid.GetString() : null).ToHashSet()
            : [];
    }

    /// <summary>(Code, Message) of the ClientError fn() raises; an unexpected success raises.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error is null
            ? throw Fail($"expected the call to be rejected, but it SUCCEEDED and returned {result}")
            : (error.Code, error.Message);
    }
}
