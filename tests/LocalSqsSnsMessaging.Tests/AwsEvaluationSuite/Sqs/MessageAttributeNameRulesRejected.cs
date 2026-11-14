// Translated from behavioral/sqs/check_message_attribute_name_rules_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.Runtime;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.message-attribute-name-rules-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class MessageAttributeNameRulesRejected
{
    private const string Reserved =
        "You can't use message attribute names beginning with 'AWS.' or 'Amazon'. These strings are " +
        "reserved for internal use.";

    // A Python dict: iterated in insertion order.
    private static readonly (string Name, string Want)[] Refused =
    [
        ("AWS.Trace", Reserved),
        ("aws.trace", Reserved),
        ("Amazon.Route", Reserved),
        (".leading", "Message (user) attribute name must not begin with character '.'."),
        ("trailing.", "Message (user) attribute name must not end with character '.'."),
        ("two..dots", "Message (user) attribute name must not have successive '.' characters."),
        ("has space",
            "Message (user) attribute name contains invalid non-alphanumeric character '#x20'. You can " +
            "use only alphanumeric characters, hyphens (-), underscores (_), or periods (.)."),
        (new string('n', 257), "Message (user) attribute name must be shorter than 256 bytes."),
    ];

    private static readonly string[] Accepted = ["Corp.Trace-Id_v2", "AWSRegionHint", "amazonroute", new string('n', 256)];

    /// <summary>
    /// SendMessage refuses attribute names with a reserved 'AWS.'/'Amazon.' prefix (any case), a
    /// leading, trailing or doubled '.', a space, or over 256 bytes, with exact messages and nothing
    /// enqueued; look-alikes (no dot, hyphens, exactly 256 bytes) round-trip under their exact names.
    /// </summary>
    [Test]
    [DisplayName("sqs.message-attribute-name-rules-rejected")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-attrname-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        for (var i = 0; i < Accepted.Length; i++)
        {
            var name = Accepted[i];
            try
            {
                await sqs.SendMessageAsync(new SendMessageRequest
                {
                    QueueUrl = url, MessageBody = $"accepted-{i}",
                    MessageAttributes = new() { [name] = new MessageAttributeValue { DataType = "String", StringValue = "v" } },
                });
            }
            catch (AmazonServiceException exc)
            {
                throw new FidelityAssertionException(
                    $"attribute name of length {name.Length} starting '{name[..Math.Min(16, name.Length)]}' must be accepted, got " +
                    $"({exc.ErrorCode}, {exc.Message})", exc);
            }
        }

        foreach (var (name, want) in Refused)
        {
            var err = await Error(() => sqs.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = url, MessageBody = "refused",
                MessageAttributes = new() { [name] = new MessageAttributeValue { DataType = "String", StringValue = "v" } },
            }));
            Ensure(err.Code == "InvalidParameterValue",
                $"attribute name '{name[..Math.Min(24, name.Length)]}' (length {name.Length}) gave code '{err.Code}'");
            Ensure(err.Message == want,
                $"attribute name '{name[..Math.Min(24, name.Length)]}': expected message '{want}', got '{err.Message}'");
        }

        var got = new Dictionary<string, List<string>>();
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            var msgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2, MessageAttributeNames = ["All"],
            })).Messages ?? [];
            if (msgs.Count == 0 && got.Count >= Accepted.Length)
            {
                break;
            }

            foreach (var m in msgs)
            {
                got[m.Body] = [.. (m.MessageAttributes ?? []).Keys.Order(StringComparer.Ordinal)];
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
            }
        }

        var wantGot = Accepted.Select((name, i) => (Body: $"accepted-{i}", Names: new List<string> { name })).ToDictionary(x => x.Body, x => x.Names);
        Ensure(got.Count == wantGot.Count && wantGot.All(kv => got.TryGetValue(kv.Key, out var v) && v.SequenceEqual(kv.Value)),
            $"the queue must hold exactly the {Accepted.Length} accepted messages under their exact " +
            $"attribute names and no refused one; received bodies [{string.Join(", ", got.Keys.Order(StringComparer.Ordinal))}]");
    }

    /// <summary>
    /// The botocore Error dict ``fn`` raises. An unexpected success raises too, naming what came
    /// back: an illegal attribute name that is accepted must not read as a code mismatch.
    /// </summary>
    private static async Task<AwsError> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"expected the call to be refused, but it succeeded: {result}");
    }
}
