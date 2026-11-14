// Translated from behavioral/sqs/check_add_permission_over_limit_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.add-permission-over-limit-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class AddPermissionOverLimitRejected
{
    /// <summary>
    /// AddPermission rejects a 21st statement once a queue policy already holds the 20-label maximum,
    /// and the identical call succeeds again once a slot is freed.
    /// </summary>
    [Test]
    [DisplayName("sqs.add-permission-over-limit-rejected")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var accountId = ctx.Account;

        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-"),
            Tags = new() { ["sim2real"] = "true" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var labels = Enumerable.Range(0, 20).Select(i => $"Perm{i}").ToList();
        foreach (var label in labels)
        {
            await sqs.AddPermissionAsync(new AddPermissionRequest { QueueUrl = url, Label = label, AWSAccountIds = [accountId], Actions = ["SendMessage"] });
        }

        var policyAttrs = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["Policy"] })).Attributes!;
        var statementSids = Sids(policyAttrs["Policy"]);
        Ensure(labels.All(statementSids.Contains),
            $"expected all 20 positive-control labels [{string.Join(", ", labels)}] present, got {{{string.Join(", ", statementSids)}}}");

        var (code, message) = await Error(() => sqs.AddPermissionAsync(new AddPermissionRequest
        {
            QueueUrl = url, Label = "OneTooMany", AWSAccountIds = [accountId], Actions = ["SendMessage"],
        }));
        Ensure(code == "OverLimit", $"expected OverLimit for a 21st statement, got {code}");
        // The trailing space is part of the message AWS returns.
        Ensure(message == "21 Statements were found, maximum allowed is 20. ", $"unexpected refusal message '{message}'");

        // Effect absent: the 21st label must not have been added.
        var policyAfter = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["Policy"] })).Attributes!["Policy"];
        var sidsAfter = Sids(policyAfter);
        Ensure(!sidsAfter.Contains("OneTooMany"), $"OneTooMany must be absent from {{{string.Join(", ", sidsAfter)}}}");
        Ensure(sidsAfter.Count == 20, $"expected 20 statements to remain, got {sidsAfter.Count}");

        // Restore: free exactly one slot and demand the identical call now succeeds.
        await sqs.RemovePermissionAsync(new RemovePermissionRequest { QueueUrl = url, Label = labels[0] });
        await sqs.AddPermissionAsync(new AddPermissionRequest { QueueUrl = url, Label = "OneTooMany", AWSAccountIds = [accountId], Actions = ["SendMessage"] });
        var policyRestored = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["Policy"] })).Attributes!["Policy"];
        var sidsRestored = Sids(policyRestored);
        Ensure(sidsRestored.Contains("OneTooMany"), $"expected OneTooMany present after freeing a slot, got {{{string.Join(", ", sidsRestored)}}}");
        Ensure(!sidsRestored.Contains(labels[0]), $"expected {labels[0]} removed, got {{{string.Join(", ", sidsRestored)}}}");

        foreach (var label in labels[1..].Append("OneTooMany"))
        {
            await sqs.RemovePermissionAsync(new RemovePermissionRequest { QueueUrl = url, Label = label });
        }
    }

    private static HashSet<string?> Sids(string policyJson)
    {
        using var doc = JsonDocument.Parse(policyJson);
        return doc.RootElement.GetProperty("Statement").EnumerateArray()
            .Select(s => s.TryGetProperty("Sid", out var sid) ? sid.GetString() : null)
            .ToHashSet();
    }

    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail("expected the call to raise a ClientError, but it succeeded");
    }
}
