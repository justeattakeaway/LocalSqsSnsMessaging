// Translated from behavioral/sqs/check_add_remove_permission.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.add-remove-permission</summary>
[Category("AwsEvaluationSuite")]
public sealed class AddRemovePermission
{
    /// <summary>
    /// AddPermission inserts a policy statement identified by Label; RemovePermission removes it by Label.
    /// </summary>
    [Test]
    [DisplayName("sqs.add-remove-permission")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];
        var account = arn.Split(':')[4];
        // SQS documents that an attribute change can take up to 60s to propagate, so the read is
        // eventually consistent with the write and must be polled rather than read once.
        const double timeoutS = 30;

        await sqs.AddPermissionAsync(new AddPermissionRequest { QueueUrl = url, Label = "TestLabel1", AWSAccountIds = [account], Actions = ["SendMessage"] });
        var policy = await Poll(
            () => Policy(sqs, url),
            p => Sids(p).Contains("TestLabel1"),
            timeoutS: timeoutS,
            intervalS: 2);
        var sids = Sids(policy);
        Ensure(sids.Contains("TestLabel1"), $"policy statement Sid TestLabel1 missing after AddPermission: {{{string.Join(", ", sids)}}}");

        await sqs.RemovePermissionAsync(new RemovePermissionRequest { QueueUrl = url, Label = "TestLabel1" });
        var removed = await Poll(
            () => Policy(sqs, url),
            p => !Sids(p).Contains("TestLabel1"),
            timeoutS: timeoutS,
            intervalS: 2);
        var removedSids = Sids(removed);
        Ensure(!removedSids.Contains("TestLabel1"), $"Sid TestLabel1 still present after RemovePermission: {{{string.Join(", ", removedSids)}}}");
    }

    private static async Task<T> Poll<T>(Func<Task<T>> fn, Func<T, bool> pred, double timeoutS, double intervalS)
    {
        var deadline = FidelityTime.StartNew();
        var val = await fn();
        while (!pred(val))
        {
            if (deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                return val;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
            val = await fn();
        }

        return val;
    }

    // A queue with no policy set omits the "Attributes" key entirely.
    private static async Task<string?> Policy(AmazonSQSClient sqs, string url)
        => (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["Policy"] }))
            .Attributes?.GetValueOrDefault("Policy");

    private static HashSet<string?> Sids(string? policyJson)
    {
        if (string.IsNullOrEmpty(policyJson))
        {
            return [];
        }

        using var doc = JsonDocument.Parse(policyJson);
        return doc.RootElement.GetProperty("Statement").EnumerateArray()
            .Select(s => s.TryGetProperty("Sid", out var sid) ? sid.GetString() : null)
            .ToHashSet();
    }
}
