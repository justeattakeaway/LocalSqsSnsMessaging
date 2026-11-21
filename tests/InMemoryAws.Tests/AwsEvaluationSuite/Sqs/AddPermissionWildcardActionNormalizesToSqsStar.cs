// Translated from behavioral/sqs/check_add_permission_wildcard_action_normalizes_to_sqs_star.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.add-permission-wildcard-action-normalizes-to-sqs-star</summary>
[Category("AwsEvaluationSuite")]
public sealed class AddPermissionWildcardActionNormalizesToSqsStar
{
    /// <summary>
    /// AddPermission with Actions=["*"] stores the statement's Action as "SQS:*", distinct from
    /// the "SQS:&lt;Name&gt;" form a named action produces.
    /// </summary>
    [Test]
    [DisplayName("sqs.add-permission-wildcard-action-normalizes-to-sqs-star")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var accountId = ctx.Account;

        var url = (await sqs.CreateQueueAsync(FidelityContext.UniqueName("sim2real-behav-addpermwc-"))).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var namedLabel = FidelityContext.UniqueName("NamedLabel");
        await sqs.AddPermissionAsync(new AddPermissionRequest { QueueUrl = url, Label = namedLabel, AWSAccountIds = [accountId], Actions = ["SendMessage"] });
        var namedAction = await StatementAction(sqs, url, namedLabel);
        Ensure(namedAction == "\"SQS:SendMessage\"", $"a named action statement read back Action={namedAction}");

        var wildcardLabel = FidelityContext.UniqueName("WildcardLabel");
        await sqs.AddPermissionAsync(new AddPermissionRequest { QueueUrl = url, Label = wildcardLabel, AWSAccountIds = [accountId], Actions = ["*"] });
        var wildcardAction = await StatementAction(sqs, url, wildcardLabel);
        Ensure(wildcardAction == "\"SQS:*\"",
            $"a wildcard Actions=['*'] statement read back Action={wildcardAction}, expected 'SQS:*'");
        Ensure(wildcardAction != namedAction,
            "the wildcard grant's Action must differ from the single named action's Action");
    }

    /// <summary>The statement's <c>Action</c> as raw JSON (<see langword="null"/> when absent).</summary>
    private static async Task<string?> StatementAction(AmazonSQSClient sqs, string url, string label)
    {
        var policy = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["Policy"] })).Attributes!["Policy"];
        using var doc = JsonDocument.Parse(policy);
        var statements = doc.RootElement.GetProperty("Statement");
        foreach (var stmt in statements.EnumerateArray())
        {
            if (stmt.TryGetProperty("Sid", out var sid) && sid.GetString() == label)
            {
                return stmt.TryGetProperty("Action", out var action) ? action.GetRawText() : null;
            }
        }

        throw Fail($"no statement with Sid '{label}' in {statements.GetRawText()}");
    }
}
