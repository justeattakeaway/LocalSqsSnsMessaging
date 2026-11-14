// Translated from behavioral/sns/check_add_permission_lifecycle_and_duplicate_label.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.add-permission-lifecycle-and-duplicate-label</summary>
[Category("AwsEvaluationSuite")]
public sealed class AddPermissionLifecycleAndDuplicateLabel
{
    /// <summary>AddPermission inserts a policy statement by Label; a reused Label is rejected; RemovePermission deletes it.</summary>
    [Test]
    [DisplayName("sns.add-permission-lifecycle-and-duplicate-label")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsPermissions, "AddPermission with a Label already in the policy fails with an internal error (HTTP 500, no error code) instead of InvalidParameter 'Invalid parameter: Statement already exists'")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-topic-perm-");
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = name })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        var accountId = ctx.Account;
        var label = FidelityContext.UniqueName("sim2real-behav-label-");

        await sns.AddPermissionAsync(new AddPermissionRequest
        {
            TopicArn = arn, Label = label, AWSAccountId = [accountId], ActionName = ["Publish"],
        });
        var statements = await Statements(sns, arn);
        Ensure(statements.ContainsKey(label), $"expected label '{label}' in policy statements {PySet(statements.Keys)}");
        var originalAction = statements[label]?["Action"];

        var (code, message) = await Error(() => sns.AddPermissionAsync(new AddPermissionRequest
        {
            TopicArn = arn, Label = label, AWSAccountId = [accountId], ActionName = ["Subscribe"],
        }));
        Ensure(code == "InvalidParameter", $"expected InvalidParameter for duplicate label, got {code}");
        Ensure(message == "Invalid parameter: Statement already exists",
            $"unexpected InvalidParameter message for a duplicate label: '{message}'");

        statements = await Statements(sns, arn);
        var rejectedAction = statements.GetValueOrDefault(label)?["Action"];
        Ensure(JsonNode.DeepEquals(rejectedAction, originalAction),
            "rejected duplicate-label AddPermission must leave the existing statement unchanged, " +
            $"expected Action '{originalAction?.ToJsonString()}', got '{rejectedAction?.ToJsonString()}'");

        await sns.RemovePermissionAsync(new RemovePermissionRequest { TopicArn = arn, Label = label });
        var sids = (await Statements(sns, arn)).Keys.ToHashSet();
        Ensure(!sids.Contains(label), $"label '{label}' should be removed, still present in {PySet(sids)}");

        await sns.AddPermissionAsync(new AddPermissionRequest
        {
            TopicArn = arn, Label = label, AWSAccountId = [accountId], ActionName = ["Subscribe"],
        });
        sids = (await Statements(sns, arn)).Keys.ToHashSet();
        Ensure(sids.Contains(label), $"expected freed label '{label}' to be reusable once removed, got {PySet(sids)}");
    }

    /// <summary>The topic policy's statements keyed by Sid (<c>{s.get("Sid"): s for s in policy["Statement"]}</c>).</summary>
    private static async Task<Dictionary<string, JsonNode?>> Statements(AmazonSimpleNotificationServiceClient sns, string arn)
    {
        var policy = JsonNode.Parse((await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = arn })).Attributes["Policy"])!;
        var statements = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var s in policy["Statement"]?.AsArray() ?? [])
        {
            statements[(string?)s?["Sid"] ?? "None"] = s;
        }

        return statements;
    }

    private static string PySet(IEnumerable<string> items) => "{" + string.Join(", ", items.Select(i => $"'{i}'")) + "}";

    /// <summary>
    /// Call fn(); return botocore (Error.Code, Error.Message) if a ClientError was raised. An
    /// unexpected SUCCESS raises.
    /// </summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected a ClientError, but the call SUCCEEDED and returned {result}");
        }

        return (error.Code, error.Message);
    }
}
