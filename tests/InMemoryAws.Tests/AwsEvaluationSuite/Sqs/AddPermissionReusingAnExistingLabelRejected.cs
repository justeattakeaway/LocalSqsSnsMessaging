// Translated from behavioral/sqs/check_add_permission_reusing_an_existing_label_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.add-permission-reusing-an-existing-label-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class AddPermissionReusingAnExistingLabelRejected
{
    /// <summary>
    /// AddPermission reusing a Label already on the queue's policy is rejected and leaves that
    /// statement's Action unchanged, while a fresh Label is still accepted.
    /// </summary>
    [Test]
    [DisplayName("sqs.add-permission-reusing-an-existing-label-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var accountId = ctx.Account;

        var url = (await sqs.CreateQueueAsync(FidelityContext.UniqueName("sim2real-behav-addperm2-"))).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var label = FidelityContext.UniqueName("Perm");
        await sqs.AddPermissionAsync(new AddPermissionRequest { QueueUrl = url, Label = label, AWSAccountIds = [accountId], Actions = ["SendMessage"] });
        var before = await Statements(sqs, url);
        Ensure(ActionOf(before, label) == "['SQS:SendMessage']", $"first AddPermission read back {Show(before)}");

        var (code, message) = await Error(() => sqs.AddPermissionAsync(new AddPermissionRequest
        {
            QueueUrl = url, Label = label, AWSAccountIds = [accountId], Actions = ["ReceiveMessage"],
        }));
        Ensure(code == "InvalidParameterValue", $"reusing an existing Label gave ('{code}', '{message}')");
        Ensure(message == $"Value {label} for parameter Label is invalid. Reason: Already exists.",
            $"reusing an existing Label gave message '{message}'");

        var after = await Statements(sqs, url);
        Ensure(ActionOf(after, label) == "['SQS:SendMessage']",
            $"the refused re-add changed the existing statement's Action: {Show(after)}");

        var freshLabel = FidelityContext.UniqueName("PermFresh");
        await sqs.AddPermissionAsync(new AddPermissionRequest { QueueUrl = url, Label = freshLabel, AWSAccountIds = [accountId], Actions = ["ReceiveMessage"] });
        var final = await Statements(sqs, url);
        Ensure(ActionOf(final, freshLabel) == "['SQS:ReceiveMessage']",
            $"a fresh Label after the refusal was not accepted: {Show(final)}");
        Ensure(ActionOf(final, label) == "['SQS:SendMessage']",
            $"the original statement changed after the fresh add: {Show(final)}");
    }

    private static string? ActionOf(Dictionary<string, List<string>> statements, string sid) =>
        statements.TryGetValue(sid, out var actions) ? "[" + string.Join(", ", actions.Select(a => $"'{a}'")) + "]" : null;

    private static string Show(Dictionary<string, List<string>> statements) =>
        "{" + string.Join(", ", statements.Keys.Select(k => $"'{k}': {ActionOf(statements, k)}")) + "}";

    private static async Task<Dictionary<string, List<string>>> Statements(AmazonSQSClient sqs, string url)
    {
        var policy = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["Policy"] })).Attributes!["Policy"];
        using var doc = JsonDocument.Parse(policy);
        return doc.RootElement.GetProperty("Statement").EnumerateArray().ToDictionary(
            s => s.GetProperty("Sid").GetString()!,
            s => s.GetProperty("Action") is { ValueKind: JsonValueKind.Array } list
                ? list.EnumerateArray().Select(a => a.GetString()!).ToList()
                : [s.GetProperty("Action").GetString()!]);
    }

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
