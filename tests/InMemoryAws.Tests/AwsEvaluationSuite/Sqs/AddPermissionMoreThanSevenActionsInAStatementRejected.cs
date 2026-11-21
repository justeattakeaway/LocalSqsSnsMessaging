// Translated from behavioral/sqs/check_add_permission_more_than_seven_actions_in_a_statement_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.add-permission-more-than-seven-actions-in-a-statement-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class AddPermissionMoreThanSevenActionsInAStatementRejected
{
    private static readonly string[] Seven =
    [
        "SendMessage", "ReceiveMessage", "DeleteMessage", "ChangeMessageVisibility",
        "GetQueueAttributes", "GetQueueUrl", "PurgeQueue",
    ];

    private static readonly string[] Eight = [.. Seven, "DeleteQueue"];

    /// <summary>
    /// AddPermission rejects a statement naming 8 actions; the identical label with 7 actions,
    /// the documented maximum, is granted and appears in the queue's policy.
    /// </summary>
    [Test]
    [DisplayName("sqs.add-permission-more-than-seven-actions-in-a-statement-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var accountId = ctx.Account;

        var url = (await sqs.CreateQueueAsync(FidelityContext.UniqueName("sim2real-behav-addpermact-"))).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        Ensure((await Sids(sqs, url)).Count == 0, "a fresh queue must carry no policy statements");

        var tooManyLabel = FidelityContext.UniqueName("EightActions");
        var (code, message) = await Error(() => sqs.AddPermissionAsync(new AddPermissionRequest
        {
            QueueUrl = url, Label = tooManyLabel, AWSAccountIds = [accountId], Actions = [.. Eight],
        }));
        Ensure(code == "OverLimit", $"AddPermission with 8 actions in one statement gave code '{code}'");
        // The trailing space is part of the message AWS returns.
        Ensure(message == "8 Actions were found, maximum allowed is 7. ", $"unexpected refusal message '{message}'");
        var sidsNow = await Sids(sqs, url);
        Ensure(!sidsNow.Contains(tooManyLabel),
            $"the 8-action statement must not have been added; sids are {Show(sidsNow)}");

        var sevenLabel = FidelityContext.UniqueName("SevenActions");
        await sqs.AddPermissionAsync(new AddPermissionRequest
        {
            QueueUrl = url, Label = sevenLabel, AWSAccountIds = [accountId], Actions = [.. Seven],
        });
        var sidsAfter = await Sids(sqs, url);
        Ensure(sidsAfter.Contains(sevenLabel),
            $"a 7-action statement, the documented maximum, must be granted; sids are {Show(sidsAfter)}");
        Ensure(!sidsAfter.Contains(tooManyLabel),
            $"the earlier 8-action refusal must still be absent; sids are {Show(sidsAfter)}");

        var policy = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["Policy"] })).Attributes!["Policy"];
        using var doc = JsonDocument.Parse(policy);
        var statement = doc.RootElement.GetProperty("Statement").EnumerateArray()
            .First(s => s.GetProperty("Sid").GetString() == sevenLabel);
        var actions = statement.GetProperty("Action").EnumerateArray().Select(a => a.GetString()!).Order(StringComparer.Ordinal).ToList();
        var expected = Seven.Select(a => $"SQS:{a}").Order(StringComparer.Ordinal).ToList();
        Ensure(actions.SequenceEqual(expected),
            $"granted statement carries actions {statement.GetProperty("Action").GetRawText()}");

        await sqs.RemovePermissionAsync(new RemovePermissionRequest { QueueUrl = url, Label = sevenLabel });
    }

    private static async Task<HashSet<string?>> Sids(AmazonSQSClient sqs, string url)
    {
        var attributes = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["Policy"] })).Attributes;
        if (attributes is null || !attributes.TryGetValue("Policy", out var policy))
        {
            return [];
        }

        using var doc = JsonDocument.Parse(policy);
        return doc.RootElement.GetProperty("Statement").EnumerateArray()
            .Select(s => s.TryGetProperty("Sid", out var sid) ? sid.GetString() : null)
            .ToHashSet();
    }

    private static string Show(HashSet<string?> sids) => "{" + string.Join(", ", sids.Select(s => $"'{s}'")) + "}";

    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be refused, but it succeeded: {result}");
    }
}
