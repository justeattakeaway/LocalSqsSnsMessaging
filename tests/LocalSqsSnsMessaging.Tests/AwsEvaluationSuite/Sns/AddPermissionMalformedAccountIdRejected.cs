// Translated from behavioral/sns/check_add_permission_malformed_account_id_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.add-permission-malformed-account-id-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class AddPermissionMalformedAccountIdRejected
{
    private const string Check = "add-permission-malformed-account-id-rejected";

    /// <summary>
    /// AddPermission refuses an AWSAccountId that is not a 12-digit account id and adds no
    /// statement; the same label with the caller's own account id is then accepted.
    /// </summary>
    [Test]
    [DisplayName("sns.add-permission-malformed-account-id-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsPermissions, "AddPermission accepts a malformed account id instead of refusing it with 'Invalid parameter: A provided account ID is not valid'")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName("sim2real-behav-sns-badacct-"),
            Tags = [new() { Key = "sim2real", Value = Check }],
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        var account = arn.Split(':')[4];
        var label = FidelityContext.UniqueName("sim2real-behav-lbl-");
        var before = await Sids(sns, arn);

        var (code, message) = await Error(() => sns.AddPermissionAsync(new AddPermissionRequest
        {
            TopicArn = arn, Label = label, AWSAccountId = ["not-an-account"], ActionName = ["Publish"],
        }));
        Ensure(code == "InvalidParameter", $"AddPermission with a malformed account id gave '{code}'");
        Ensure(message == "Invalid parameter: A provided account ID is not valid", $"unexpected message for a malformed account id: '{message}'");
        Ensure((await Sids(sns, arn)).SetEquals(before), "the refused AddPermission must not change the policy");

        await sns.AddPermissionAsync(new AddPermissionRequest { TopicArn = arn, Label = label, AWSAccountId = [account], ActionName = ["Publish"] });
        Ensure((await Sids(sns, arn)).SetEquals(before.Append(label)), "the legal AddPermission must add exactly its label");
    }

    private static async Task<HashSet<string?>> Sids(AmazonSimpleNotificationServiceClient sns, string arn)
    {
        var policy = JsonNode.Parse((await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = arn })).Attributes["Policy"])!;
        return (policy["Statement"]?.AsArray() ?? []).Select(s => (string?)s?["Sid"]).ToHashSet();
    }

    /// <summary>Return (Error.Code, Error.Message) of the ClientError fn raises; a success raises.</summary>
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
