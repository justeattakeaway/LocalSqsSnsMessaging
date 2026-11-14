// Translated from behavioral/sqs/check_maximum_message_size_is_a_byte_boundary_not_a_character_count.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Text;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.maximum-message-size-is-a-byte-boundary-not-a-character-count</summary>
[Category("AwsEvaluationSuite")]
public sealed class MaximumMessageSizeIsAByteBoundaryNotACharacterCount
{
    private const string TooLongMessage = "One or more parameters are invalid. Reason: Message must be shorter than {0} bytes.";

    // 1024 is the MINIMUM legal MaximumMessageSize, the cheapest value at which to probe the boundary.
    private const int LimitBytes = 1024;
    private const int RaisedLimitBytes = 4096;
    private const double PropagateS = 90.0;  // attribute changes can take up to 60s to propagate (documented)
    // U+00E9 LATIN SMALL LETTER E WITH ACUTE: exactly 2 bytes in UTF-8, 1 Python character. The whole
    // point of the multi-byte leg is that len(str) and len(str.encode("utf-8")) disagree by 2x.
    private const char TwoByteChar = 'é';

    private static readonly Dictionary<string, string> Tags = new()
    {
        ["sim2real:managed"] = "true",
        ["sim2real"] = "max-message-size-bytes",
    };

    /// <summary>
    /// MaximumMessageSize is an inclusive UTF-8 BYTE limit: at-limit bodies pass, one byte or one
    /// 2-byte character over is refused, and raising the limit admits the identical body byte-exact.
    ///
    /// Reference: https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_GetQueueAttributes.html
    /// </summary>
    [Test]
    [DisplayName("sqs.maximum-message-size-is-a-byte-boundary-not-a-character-count")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-"),
            Attributes = new() { ["MaximumMessageSize"] = LimitBytes.ToString(CultureInfo.InvariantCulture) },
            Tags = new(Tags),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        // The limit under test is read back off the target, not assumed.
        var reported = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["MaximumMessageSize"] })).Attributes!["MaximumMessageSize"];
        Ensure(reported == LimitBytes.ToString(CultureInfo.InvariantCulture),
            $"the queue reports MaximumMessageSize='{reported}', not {LimitBytes}; every boundary " +
            "assertion below would be probing the wrong limit");
        var limit = int.Parse(reported, CultureInfo.InvariantCulture);

        var asciiAtLimit = new string('a', limit);
        var asciiOverByOne = new string('a', limit + 1);
        var multibyteAtLimit = new string(TwoByteChar, limit / 2);
        var multibyteOverByOne = new string(TwoByteChar, (limit / 2) + 1);

        // Sanity on the fixtures themselves: the multi-byte pair is the whole point, so if the encoding
        // assumption is broken locally the check must say so rather than assert something else.
        Ensure(Utf8Len(asciiAtLimit) == limit, "ASCII fixture is not exactly at the limit");
        Ensure(Utf8Len(multibyteAtLimit) == limit,
            "the multi-byte at-limit fixture encodes to " +
            $"{Utf8Len(multibyteAtLimit)} bytes, expected {limit}");
        Ensure(multibyteOverByOne.Length <= limit,
            $"the over-limit multi-byte fixture is {multibyteOverByOne.Length} CHARACTERS, which must " +
            $"stay under the {limit}-byte limit for the leg to discriminate byte- from " +
            "character-counting");
        Ensure(Utf8Len(multibyteOverByOne) == limit + 2,
            "the over-limit multi-byte fixture encodes to " +
            $"{Utf8Len(multibyteOverByOne)} bytes, expected {limit + 2}");

        // --- positive control: both at-limit bodies are accepted --------------------------
        await SendOk(sqs, url, asciiAtLimit, why: "an ASCII body of exactly MaximumMessageSize bytes");
        await SendOk(
            sqs,
            url,
            multibyteAtLimit,
            why: $"a {multibyteAtLimit.Length}-character body that encodes to exactly " +
                 $"{limit} UTF-8 bytes");

        // --- break exactly one thing: one byte over ---------------------------------------
        var error = await ClientError(() => sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = asciiOverByOne }));
        Ensure(error.Code == "InvalidParameterValue",
            $"a {limit + 1}-byte body (one byte over MaximumMessageSize={limit}) was rejected with " +
            $"'{error.Code}', expected 'InvalidParameterValue'");
        var expectedMessage = string.Format(CultureInfo.InvariantCulture, TooLongMessage, limit);
        Ensure(error.Message == expectedMessage,
            $"expected message '{expectedMessage}', got '{error.Message}'");

        // --- and the encoding leg: one 2-byte character over, well under the CHARACTER limit
        error = await ClientError(() => sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = multibyteOverByOne }));
        Ensure(error.Code == "InvalidParameterValue",
            $"a {multibyteOverByOne.Length}-character body encoding to " +
            $"{Utf8Len(multibyteOverByOne)} UTF-8 bytes was rejected with " +
            $"'{error.Code}', expected 'InvalidParameterValue'. MaximumMessageSize={limit} is " +
            $"a BYTE limit, so a body far under {limit} characters can still exceed it");
        Ensure(error.Message == expectedMessage,
            $"expected message '{expectedMessage}', got '{error.Message}'");

        // --- effect absent: exactly the two accepted bodies are on the queue --------------
        var drained = await DrainBodies(sqs, url, expectedN: 2, timeoutS: 20.0);
        Ensure(drained.SetEquals([asciiAtLimit, multibyteAtLimit]),
            "the queue's delivered set is not exactly the two accepted bodies: got byte-lengths " +
            $"[{string.Join(", ", drained.Select(Utf8Len).Order())}], expected " +
            $"[{limit}, {limit}]. A rejected body that lands anyway, or an accepted body that arrives " +
            "truncated, both show up here");

        // --- restore: raise the limit, and the IDENTICAL rejected call must now succeed ---
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = url, Attributes = new() { ["MaximumMessageSize"] = RaisedLimitBytes.ToString(CultureInfo.InvariantCulture) },
        });
        var deadline = FidelityTime.StartNew();
        string? lastError = null;
        var accepted = false;
        while (deadline.Elapsed.TotalSeconds < PropagateS)
        {
            try
            {
                await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = multibyteOverByOne });
            }
            catch (AmazonServiceException exc)
            {
                // Only the pre-propagation rejection is tolerated here; the loop's deadline turns a
                // permanent rejection into a FAILURE below rather than a silent pass.
                lastError = exc.ErrorCode ?? throw new KeyNotFoundException("Code");
                await FidelityTime.Delay(TimeSpan.FromSeconds(2.0));
                continue;
            }

            accepted = true;
            break;
        }

        Ensure(accepted,
            $"after raising MaximumMessageSize to {RaisedLimitBytes}, the identical " +
            $"{Utf8Len(multibyteOverByOne)}-byte body was still rejected " +
            $"('{lastError}') throughout {PropagateS}s -- the attribute is not consulted per send");
        var restored = await DrainBodies(sqs, url, expectedN: 1, timeoutS: 20.0);
        Ensure(restored.SetEquals([multibyteOverByOne]),
            $"after the limit was raised, the queue delivered [{string.Join(", ", restored.Select(b => b.Length).Order())}] " +
            $"character-length(s) instead of the one {multibyteOverByOne.Length}-character body; the " +
            "body must arrive byte-identical, not truncated to the old limit");
    }

    private static int Utf8Len(string s) => Encoding.UTF8.GetByteCount(s);

    /// <summary>Return the botocore Error dict; raise if the call unexpectedly succeeds.</summary>
    private static async Task<AwsError> ClientError(Func<Task> fn) =>
        await FidelityContext.CaptureError(fn)
        ?? throw Fail("the call succeeded, but this leg exists because it must be rejected");

    /// <summary>
    /// Receive-and-delete until `expected_n` distinct messages are seen; return their bodies.
    ///
    /// Raises on the deadline so an under-delivery fails rather than returning a short set.
    /// </summary>
    private static async Task<HashSet<string>> DrainBodies(AmazonSQSClient sqs, string url, int expectedN, double timeoutS)
    {
        var seen = new Dictionary<string, string>();
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed.TotalSeconds < timeoutS && seen.Count < expectedN)
        {
            foreach (var msg in (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 })).Messages ?? [])
            {
                seen[msg.MessageId] = msg.Body;
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
            }
        }

        Ensure(seen.Count == expectedN,
            $"expected exactly {expectedN} message(s) on the queue within {timeoutS}s, drained " +
            $"{seen.Count}: [{string.Join(", ", seen.Values.Select(Utf8Len))}] byte-lengths");
        return [.. seen.Values];
    }

    private static async Task<SendMessageResponse> SendOk(AmazonSQSClient sqs, string url, string body, string why)
    {
        var resp = await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = body });
        Ensure(!string.IsNullOrEmpty(resp.MessageId),
            $"{why}: SendMessage returned no MessageId for a {Utf8Len(body)}-byte body");
        return resp;
    }
}
