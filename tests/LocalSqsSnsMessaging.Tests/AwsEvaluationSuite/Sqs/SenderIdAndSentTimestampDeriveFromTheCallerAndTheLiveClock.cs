// Translated from behavioral/sqs/check_sender_id_and_sent_timestamp_derive_from_the_caller_and_the_live_clock.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.sender-id-and-sent-timestamp-derive-from-the-caller-and-the-live-clock</summary>
[Category("AwsEvaluationSuite")]
public sealed class SenderIdAndSentTimestampDeriveFromTheCallerAndTheLiveClock
{
    private const long SkewMs = 300_000; // allowance for client-vs-service clock skew
    private const double PollSeconds = 40.0;

    private static Dictionary<string, string> Tags() => new()
    {
        ["sim2real:managed"] = "true",
        ["sim2real"] = "sender-id-derivation",
    };

    /// <summary>
    /// SenderId equals the caller's sts UserId and SentTimestamp comes from the live clock.
    ///
    /// No replayed recording can carry either value, so both are checked against fresh ground truth.
    ///
    /// Reference: https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_ReceiveMessage.html
    /// Reference: https://docs.aws.amazon.com/STS/latest/APIReference/API_GetCallerIdentity.html
    /// </summary>
    [Test]
    [DisplayName("sqs.sender-id-and-sent-timestamp-derive-from-the-caller-and-the-live-clock")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.Other, "SenderId is the caller's ACCOUNT id (000000000000); SQS documents it as the IAM user id or IAM role id of the sender, which is a distinct value")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var (userId, account) = GetCallerIdentity(ctx);
        Ensure(!string.IsNullOrEmpty(userId), "sts:GetCallerIdentity returned no UserId");

        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-"), Tags = Tags() })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var token = Guid.NewGuid().ToString("N")[..8];
        var firstBody = $"sender-first-{token}";
        var secondBody = $"sender-second-{token}";

        var beforeMs = FidelityTime.UtcNow.ToUnixTimeMilliseconds();
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = firstBody });
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = secondBody });
        var afterMs = FidelityTime.UtcNow.ToUnixTimeMilliseconds();
        Ensure(afterMs >= beforeMs, "the runner's wall clock went backwards across the two sends");

        var first = await ReceiveOne(sqs, url, body: firstBody, timeoutSeconds: PollSeconds);
        var second = await ReceiveOne(sqs, url, body: secondBody, timeoutSeconds: PollSeconds);

        var firstSender = SystemAttribute(first, "SenderId");
        var secondSender = SystemAttribute(second, "SenderId");

        Ensure(firstSender != account,
            $"SenderId is the caller's ACCOUNT id ({account}); SQS documents it as the IAM user id or " +
            "IAM role id of the sender, which is a distinct value");
        Ensure(firstSender.Trim().Length > 0, $"SenderId came back blank: '{firstSender}'");

        Ensure(firstSender == secondSender,
            $"two sends by the SAME principal reported different SenderIds: '{firstSender}' then " +
            $"'{secondSender}'. SenderId is a property of the caller, not of the message");
        var wantPrincipal = userId.Split(':', 2)[0];
        var gotPrincipal = firstSender.Split(':', 2)[0];
        Ensure(gotPrincipal == wantPrincipal,
            $"SenderId's principal-id component is '{gotPrincipal}', but this caller's " +
            $"sts:GetCallerIdentity UserId is '{userId}' (principal component '{wantPrincipal}'). " +
            "SenderId must identify the principal that actually signed the SendMessage");

        // Exact equality is asserted last: it is the leg most sensitive to the SenderId format.
        Ensure(firstSender == userId,
            $"SenderId is '{firstSender}' but sts:GetCallerIdentity's UserId is '{userId}'. The " +
            "ReceiveMessage reference documents SenderId as the IAM user id (for a user) or the IAM " +
            "role id in <role-id>:<session-name> form (for a role), which is the same value UserId " +
            "carries -- see this file's hypothesis 1 if real SQS disagrees");

        var firstSentRaw = SystemAttribute(first, "SentTimestamp");
        Ensure(firstSentRaw.Length > 0 && firstSentRaw.All(char.IsDigit),
            $"SentTimestamp must be a decimal epoch-milliseconds string, got '{firstSentRaw}'");
        var firstSentMs = long.Parse(firstSentRaw, System.Globalization.CultureInfo.InvariantCulture);
        Ensure(beforeMs - SkewMs <= firstSentMs && firstSentMs <= afterMs + SkewMs,
            $"SentTimestamp {firstSentMs} is outside the window this run measured around its own " +
            $"SendMessage call [{beforeMs - SkewMs}, {afterMs + SkewMs}] (raw window " +
            $"[{beforeMs}, {afterMs}], skew allowance {SkewMs}ms). A constant, a zero, or a value " +
            "replayed from a recording lands here");

        // >= not >: two sends can share a millisecond.
        var secondSentMs = long.Parse(SystemAttribute(second, "SentTimestamp"), System.Globalization.CultureInfo.InvariantCulture);
        Ensure(secondSentMs >= firstSentMs,
            $"the second send's SentTimestamp {secondSentMs} precedes the first's {firstSentMs}; " +
            "the stamped enqueue instant must not go backwards within one caller's program order");

        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = first.ReceiptHandle });
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = second.ReceiptHandle });
    }

    /// <summary>
    /// <c>ctx.client("sts").get_caller_identity()</c>. The context has no STS client; the account is
    /// <see cref="FidelityContext.Account"/>, and the UserId is a stand-in IAM user id for the
    /// "test" credentials every client signs with, which is a value distinct from the account id.
    /// </summary>
    private static (string UserId, string Account) GetCallerIdentity(FidelityContext ctx) => ("AIDAIOSFODNN7EXAMPLE", ctx.Account);

    /// <summary>
    /// Poll until the message with exactly <paramref name="body"/> arrives; raise on the deadline.
    ///
    /// Other messages are re-exposed with visibility 0 so a later poll need not wait them out.
    /// </summary>
    private static async Task<Message> ReceiveOne(AmazonSQSClient sqs, string url, string body, double timeoutSeconds)
    {
        var deadline = FidelityTime.StartNew();
        var seen = new List<string>();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 2,
                MessageSystemAttributeNames = ["All"],
            });
            foreach (var msg in resp.Messages ?? [])
            {
                if (msg.Body == body)
                {
                    return msg;
                }

                seen.Add(msg.Body);
                await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest
                {
                    QueueUrl = url,
                    ReceiptHandle = msg.ReceiptHandle,
                    VisibilityTimeout = 0,
                });
            }
        }

        throw Fail($"'{body}' was not delivered within {timeoutSeconds}s; bodies seen instead: [{string.Join(", ", seen)}]");
    }

    private static string SystemAttribute(Message msg, string name)
    {
        var attrs = msg.Attributes ?? [];
        Ensure(attrs.ContainsKey(name),
            $"ReceiveMessage(MessageSystemAttributeNames=['All']) did not return {name}; SQS documents " +
            $"it as a supported system attribute. Got [{string.Join(", ", attrs.Keys.Order(StringComparer.Ordinal))}]");
        return attrs[name];
    }
}
