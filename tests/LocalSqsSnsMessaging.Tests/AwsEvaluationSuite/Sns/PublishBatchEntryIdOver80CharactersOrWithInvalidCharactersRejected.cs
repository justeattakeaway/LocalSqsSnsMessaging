// Translated from behavioral/sns/check_publish_batch_entry_id_over_80_characters_or_with_invalid_characters_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.publish-batch-entry-id-over-80-characters-or-with-invalid-characters-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PublishBatchEntryIdOver80CharactersOrWithInvalidCharactersRejected
{
    private const string Check = "publish-batch-entry-id-over-80-characters-or-with-invalid-characters-rejected";

    /// <summary>
    /// A PublishBatch with an 81-char or period-bearing entry Id is refused whole with
    /// InvalidBatchEntryId, delivering no sibling; 80-char Ids with "-" and "_" are all delivered.
    /// </summary>
    [Test]
    [DisplayName("sns.publish-batch-entry-id-over-80-characters-or-with-invalid-characters-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsBatch, "PublishBatch accepts an entry Id over 80 characters or with invalid characters instead of refusing it with InvalidBatchEntryId")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var sqs = ctx.Sqs;
        var topic = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName("sim2real-behav-sns-batchid-"),
            Tags = [new() { Key = "sim2real", Value = Check }],
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = topic }));
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-sns-batchid-"),
            Tags = new() { ["sim2real"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var qArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];
        var policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Effect"] = "Allow",
                ["Principal"] = new JsonObject { ["Service"] = "sns.amazonaws.com" },
                ["Action"] = "sqs:SendMessage",
                ["Resource"] = qArn,
                ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = topic } },
            }),
        };
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = url, Attributes = new() { ["Policy"] = policy.ToJsonString() } });
        var sub = (await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topic, Protocol = "sqs", Endpoint = qArn, ReturnSubscriptionArn = true,
            Attributes = new() { ["RawMessageDelivery"] = "true" },
        })).SubscriptionArn;
        ctx.OnCleanup(() => sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = sub }));

        var runId = FidelityContext.UniqueName("r");
        var longId = new string('a', 81);
        var (code, message) = await Error(() => sns.PublishBatchAsync(new PublishBatchRequest
        {
            TopicArn = topic,
            PublishBatchRequestEntries =
            [
                new() { Id = "valid-sibling", Message = $"{runId}-sibling-1" },
                new() { Id = longId, Message = $"{runId}-long" },
            ],
        }));
        Ensure((code, message) == ("InvalidBatchEntryId", $"The Id of a batch entry in the batch request is too long: {longId}"),
            $"an 81-character entry Id gave '{code}': '{message}'");
        (code, message) = await Error(() => sns.PublishBatchAsync(new PublishBatchRequest
        {
            TopicArn = topic,
            PublishBatchRequestEntries =
            [
                new() { Id = "valid-sibling", Message = $"{runId}-sibling-2" },
                new() { Id = "has.period", Message = $"{runId}-period" },
            ],
        }));
        Ensure((code, message) == ("InvalidBatchEntryId", "The Id of a batch entry in the batch request contains an impermissible character: has.period"),
            $"an entry Id with a period gave '{code}': '{message}'");

        string[] ids = [string.Join("-", new string('x', 39), new string('y', 40)), string.Join("_", new string('p', 40), new string('q', 39))];
        Ensure(ids.All(i => i.Length == 80), $"the boundary Ids are [{string.Join(", ", ids.Select(i => i.Length))}] characters long");
        var bodies = ids.Select((i, n) => (Id: i, Body: $"{runId}-ok-{n}")).ToList();
        var resp = await sns.PublishBatchAsync(new PublishBatchRequest
        {
            TopicArn = topic,
            PublishBatchRequestEntries = bodies.Select(b => new PublishBatchRequestEntry { Id = b.Id, Message = b.Body }).ToList(),
        });
        Ensure(resp.Failed is { Count: 0 } && resp.Successful.Select(s => s.Id).Order(StringComparer.Ordinal).SequenceEqual(ids.Order(StringComparer.Ordinal)),
            $"a batch of 80-character Ids must succeed entry for entry; Failed {FailedRepr(resp.Failed)}, " +
            $"{(resp.Successful ?? []).Count} Successful");

        var got = new List<string>();
        var clock = FidelityTime.StartNew();
        var deadline = TimeSpan.FromSeconds(90);
        TimeSpan? tail = null;
        while (clock.Elapsed < deadline && (tail is null || clock.Elapsed < tail))
        {
            if (tail is null && bodies.All(b => got.Contains(b.Body)))
            {
                // Keep receiving briefly so a late sibling of a refused batch shows up.
                tail = clock.Elapsed + TimeSpan.FromSeconds(6);
            }

            foreach (var m in (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 })).Messages ?? [])
            {
                got.Add(m.Body);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
            }
        }

        Ensure(got.Order(StringComparer.Ordinal).SequenceEqual(bodies.Select(b => b.Body).Order(StringComparer.Ordinal)),
            $"the queue must hold exactly the accepted batch's two messages; it received [{string.Join(", ", got.Order(StringComparer.Ordinal).Select(b => $"'{b}'"))}]");
    }

    private static string FailedRepr(List<Amazon.SimpleNotificationService.Model.BatchResultErrorEntry>? failed) => failed is null ? "None" :
        $"[{string.Join(", ", failed.Select(f => $"{{'Id': '{f.Id}', 'Code': '{f.Code}', 'Message': '{f.Message}', 'SenderFault': {f.SenderFault}}}"))}]";

    /// <summary>(Code, Message) of the error <c>fn</c> raises; an unexpected success raises, naming it.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {(result is PublishBatchResponse r ? $"{{'Successful': [{string.Join(", ", (r.Successful ?? []).Select(e => $"{{'Id': '{e.Id}', 'MessageId': '{e.MessageId}'}}"))}], 'Failed': {FailedRepr(r.Failed)}}}" : result)}");
        }

        return (error.Code, error.Message);
    }
}
