// Translated from behavioral/sns/check_topic_policy_round_trips_and_deleted_topic_set_attributes_not_found.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.topic-policy-round-trips-and-deleted-topic-set-attributes-not-found</summary>
[Category("AwsEvaluationSuite")]
public sealed class TopicPolicyRoundTripsAndDeletedTopicSetAttributesNotFound
{
    /// <summary>
    /// A topic Policy set with SetTopicAttributes reads back exactly through GetTopicAttributes.
    ///
    /// Once the topic is deleted, Get and SetTopicAttributes on its ARN are refused with NotFound.
    /// </summary>
    [Test]
    [DisplayName("sns.topic-policy-round-trips-and-deleted-topic-set-attributes-not-found")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsErrorMessages, "GetTopicAttributes on a deleted topic is refused with NotFound 'Topic not found.' instead of 'Topic does not exist'")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = FidelityContext.UniqueName("sim2real-behav-policytopic-") })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));

        // json.dumps(..., separators=(",", ":")): compact, in insertion order.
        var policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Id"] = "owner-only",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Sid"] = "OwnerPublish",
                ["Effect"] = "Allow",
                ["Principal"] = new JsonObject { ["AWS"] = $"arn:aws:iam::{ctx.Account}:root" },
                ["Action"] = "SNS:Publish",
                ["Resource"] = arn,
            }),
        }.ToJsonString();
        await sns.SetTopicAttributesAsync(new SetTopicAttributesRequest { TopicArn = arn, AttributeName = "Policy", AttributeValue = policy });
        var readBack = (await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = arn })).Attributes["Policy"];
        Ensure(readBack == policy, $"Policy read back as '{readBack}', expected '{policy}'");

        await sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn });
        var clock = FidelityTime.StartNew();
        var deadline = TimeSpan.FromSeconds(Math.Min(60.0, ctx.TimeLeftSeconds));
        while (true)
        {
            var error = await FidelityContext.CaptureError(() => sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = arn }));
            if (error is not null)
            {
                if (error.Code != "NotFound")
                {
                    throw error.Exception;
                }

                break;
            }

            Ensure(clock.Elapsed < deadline, $"deleted topic {arn} is still readable");
            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        var (code, message) = await Error(() => sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = arn }));
        Ensure((code, message) == ("NotFound", "Topic does not exist"),
            $"GetTopicAttributes on a deleted topic gave '{code}': '{message}'");
        (code, message) = await Error(() => sns.SetTopicAttributesAsync(new SetTopicAttributesRequest
        {
            TopicArn = arn,
            AttributeName = "Policy",
            AttributeValue = policy,
        }));
        Ensure((code, message) == ("NotFound", "Topic does not exist"),
            $"SetTopicAttributes on a deleted topic gave '{code}': '{message}'");
    }

    /// <summary>The (code, message) fn raises; an unexpected success raises AssertionError.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }
}
