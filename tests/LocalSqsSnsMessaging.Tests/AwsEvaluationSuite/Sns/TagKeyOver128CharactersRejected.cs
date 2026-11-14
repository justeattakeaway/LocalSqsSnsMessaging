// Translated from behavioral/sns/check_tag_key_over_128_characters_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.tag-key-over-128-characters-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class TagKeyOver128CharactersRejected
{
    private const string Check = "tag-key-over-128-characters-rejected";

    /// <summary>
    /// TagResource refuses a 129-character tag key and stores nothing from that request,
    /// while a 128-character key -- the documented maximum -- is accepted and read back.
    /// </summary>
    [Test]
    [DisplayName("sns.tag-key-over-128-characters-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName("sim2real-behav-sns-longkey-"),
            Tags = [new() { Key = "sim2real", Value = Check }],
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        var (tooLong, atLimit) = (new string('k', 129), new string('k', 128));

        var (code, message) = await Error(() => sns.TagResourceAsync(new TagResourceRequest { ResourceArn = arn, Tags = [new() { Key = tooLong, Value = "v" }] }));
        // SNS answers ValidationError here, a code its API model does not declare for TagResource.
        Ensure(code == "ValidationError", $"a 129-character tag key gave '{code}'");
        var expected = $"1 validation error detected: Value '{tooLong}' at 'tags.1.member.key' failed " +
            "to satisfy constraint: Member must have length less than or equal to 128";
        Ensure(message == expected, $"unexpected message for a 129-character tag key: '{message}'");
        Ensure(TagsEqual(await Tags(sns, arn), new() { ["sim2real"] = Check }), "the refused TagResource must store nothing");

        await sns.TagResourceAsync(new TagResourceRequest { ResourceArn = arn, Tags = [new() { Key = atLimit, Value = "v" }] });
        Ensure(TagsEqual(await Tags(sns, arn), new() { ["sim2real"] = Check, [atLimit] = "v" }), "a 128-character tag key must be stored");
    }

    private static bool TagsEqual(Dictionary<string, string> a, Dictionary<string, string> b) => a.Count == b.Count && !a.Except(b).Any();

    private static async Task<Dictionary<string, string>> Tags(AmazonSimpleNotificationServiceClient sns, string arn) =>
        ((await sns.ListTagsForResourceAsync(new ListTagsForResourceRequest { ResourceArn = arn })).Tags ?? [])
        .ToDictionary(t => t.Key, t => t.Value);

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
