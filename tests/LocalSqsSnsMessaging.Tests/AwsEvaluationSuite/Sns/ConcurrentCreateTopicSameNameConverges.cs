// Translated from behavioral/sns/check_concurrent_create_topic_same_name_converges.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.concurrent-create-topic-same-name-converges</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConcurrentCreateTopicSameNameConverges
{
    private const int Callers = 10;

    /// <summary>
    /// Ten concurrent CreateTopic calls for one new name, all with identical attributes, all
    /// succeed with the same TopicArn and leave exactly one topic of that name behind.
    /// </summary>
    [Test]
    [DisplayName("sns.concurrent-create-topic-same-name-converges")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-ccreate-");
        var attrs = new Dictionary<string, string> { ["DisplayName"] = "race" };
        // Topic ARNs are a pure function of partition, region, account and name, and DeleteTopic
        // is idempotent, so the net is registered before the racers can create anything.
        var expectedArn = $"arn:aws:sns:{ctx.Region}:{ctx.Account}:{name}";
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = expectedArn }));

        async Task<string> Create() =>
            (await sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Attributes = new(attrs) })).TopicArn;

        var arns = await Task.WhenAll(Enumerable.Range(0, Callers).Select(_ => Task.Run(Create)));
        var arn = arns[0];
        Ensure(arns.ToHashSet().SetEquals([arn]), $"concurrent creators got {arns.Distinct().Count()} distinct TopicArns");
        Ensure(arn == expectedArn, "the TopicArn must be the deterministic ARN for this name");
        Ensure(arn[(arn.LastIndexOf(':') + 1)..] == name, "the TopicArn's last segment must be the requested name");

        async Task<List<string>> Named()
        {
            var result = new List<string>();
            await foreach (var page in sns.Paginators.ListTopics(new ListTopicsRequest()).Responses)
            {
                result.AddRange((page.Topics ?? []).Select(t => t.TopicArn).Where(a => a[(a.LastIndexOf(':') + 1)..] == name));
            }

            return result;
        }

        var named = await Poll(Named, v => v.Count >= 1, timeoutS: 60.0, intervalS: 3.0,
            what: "the raced topic appears in ListTopics");
        Ensure(named.SequenceEqual([arn]), $"exactly one topic of the raced name must exist, got {named.Count}");
        var display = (await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = arn })).Attributes?.GetValueOrDefault("DisplayName");
        Ensure(display == "race", $"the converged topic must carry the requested DisplayName, got '{display}'");
    }

    /// <summary>Poll fn() until pred(val); raise AssertionError on deadline miss (never a bare sleep).</summary>
    private static async Task<List<string>> Poll(Func<Task<List<string>>> fn, Func<List<string>, bool> pred,
        double timeoutS, double intervalS, string what)
    {
        var deadline = FidelityTime.StartNew();
        var val = await fn();
        while (!pred(val))
        {
            if (deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw Fail($"{what}: condition not met within {timeoutS}s, last value [{string.Join(", ", val.Select(v => $"'{v}'"))}]");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
            val = await fn();
        }

        return val;
    }
}
