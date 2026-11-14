// Translated from behavioral/sqs/check_add_remove_permission_updates_policy.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.add-remove-permission-updates-policy</summary>
[Category("AwsEvaluationSuite")]
public sealed class AddRemovePermissionUpdatesPolicy
{
    /// <summary>
    /// AddPermission adds a labeled Statement to the queue policy; RemovePermission removes it by Label.
    /// </summary>
    [Test]
    [DisplayName("sqs.add-remove-permission-updates-policy")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var accountId = ctx.Account;

        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-"),
            Tags = new() { ["sim2real:managed"] = "true" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        await sqs.AddPermissionAsync(new AddPermissionRequest
        {
            QueueUrl = url, Label = "AllowSend", AWSAccountIds = [accountId], Actions = ["SendMessage"],
        });
        var policy = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["Policy"] })).Attributes!["Policy"];
        var labels = Sids(policy);
        Ensure(labels.Contains("AllowSend"), $"expected Sid 'AllowSend' in policy statements after AddPermission, got {{{string.Join(", ", labels)}}}");

        await sqs.RemovePermissionAsync(new RemovePermissionRequest { QueueUrl = url, Label = "AllowSend" });
        var attrsAfter = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["Policy"] })).Attributes;
        var policyAfter = attrsAfter?.GetValueOrDefault("Policy");
        if (!string.IsNullOrEmpty(policyAfter))
        {
            var labelsAfter = Sids(policyAfter);
            Ensure(!labelsAfter.Contains("AllowSend"),
                $"expected Sid 'AllowSend' removed from policy statements after RemovePermission, got {{{string.Join(", ", labelsAfter)}}}");
        }
        // else: the Policy attribute is absent entirely because the removed statement was the only one -- also correct.
    }

    private static HashSet<string?> Sids(string policyJson)
    {
        using var doc = JsonDocument.Parse(policyJson);
        return doc.RootElement.GetProperty("Statement").EnumerateArray()
            .Select(s => s.TryGetProperty("Sid", out var sid) ? sid.GetString() : null)
            .ToHashSet();
    }
}
