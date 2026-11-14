// Translated from behavioral/sns/check_concurrent_add_permission_distinct_labels_all_retained.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.concurrent-add-permission-distinct-labels-all-retained</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConcurrentAddPermissionDistinctLabelsAllRetained
{
    private const string Check = "concurrent-add-permission-distinct-labels-all-retained";
    private const int Writers = 8;

    /// <summary>
    /// Eight concurrent AddPermission calls with distinct labels all survive in the topic policy
    /// beside the default statement; an unlocked read-modify-write store loses some of them.
    /// </summary>
    [Test]
    [DisplayName("sns.concurrent-add-permission-distinct-labels-all-retained")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsPermissions, "A new topic has no default Policy attribute, so reading the default statements fails before any AddPermission")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName("sim2real-behav-sns-ccperm-"),
            Tags = [new() { Key = "sim2real", Value = Check }],
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        var account = arn.Split(':')[4];
        var @base = await Sids(sns, arn);

        var labels = Enumerable.Range(0, Writers).Select(i => $"{FidelityContext.UniqueName("sim2real-behav-lbl-")}-{i}").ToList();
        await Task.WhenAll(labels.Select(lb => Task.Run(() => sns.AddPermissionAsync(new AddPermissionRequest
        {
            TopicArn = arn,
            Label = lb,
            AWSAccountId = [account],
            ActionName = ["Publish"],
        }))));

        var sids = await Sids(sns, arn);
        var missing = labels.Except(sids).Order(StringComparer.Ordinal).ToList();
        Ensure(missing.Count == 0, $"{missing.Count} of {Writers} concurrently added statements were lost");
        Ensure(sids.SetEquals(@base.Union(labels)),
            $"the policy must hold the default statement(s) plus the {Writers} labels, got {sids.Count} Sids");
    }

    private static async Task<HashSet<string?>> Sids(Amazon.SimpleNotificationService.AmazonSimpleNotificationServiceClient sns, string arn)
    {
        var policy = (await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = arn })).Attributes["Policy"];
        return JsonNode.Parse(policy)!["Statement"]!.AsArray().Select(s => (string?)s?["Sid"]).ToHashSet();
    }
}
