// Translated from behavioral/sqs/check_custom_type_message_attribute_value_errors_use_different_type_names.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.custom-type-message-attribute-value-errors-use-different-type-names</summary>
[Category("AwsEvaluationSuite")]
public sealed class CustomTypeMessageAttributeValueErrorsUseDifferentTypeNames
{
    // Each value is a factory: an SDK MessageAttributeValue carries a MemoryStream, so every
    // request gets its own instance, as each botocore call serializes the dict afresh.
    private static readonly (string Label, Func<MessageAttributeValue> Attr, string WantMessage)[] Refused =
    [
        ("missing-value-string-custom", () => new() { DataType = "String.custom" },
            "Message (user) attribute 'N' must contain a non-empty value of type 'String.custom'."),
        ("missing-value-binary-custom", () => new() { DataType = "Binary.custom" },
            "Message (user) attribute 'N' must contain a non-empty value of type 'Binary.custom'."),
        ("wrong-field-binary-custom", () => new() { DataType = "Binary.custom", StringValue = "abc" },
            "Message (user) attribute 'N' with type 'Binary' must use field 'Binary'."),
        ("wrong-field-string-custom", () => new() { DataType = "String.custom", BinaryValue = new MemoryStream("abc"u8.ToArray()) },
            "Message (user) attribute 'N' with type 'String' must use field 'String'."),
    ];

    /// <summary>
    /// SendMessage refuses a custom-typed attribute with a missing or misplaced value: the
    /// missing-value message names the full custom DataType, the wrong-field message names only
    /// its base type; a well-formed custom-typed attribute is accepted and round-trips unchanged.
    /// </summary>
    [Test]
    [DisplayName("sqs.custom-type-message-attribute-value-errors-use-different-type-names")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(FidelityContext.UniqueName("sim2real-behav-attrcustom-"))).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        foreach (var (label, attr, wantMessage) in Refused)
        {
            var (result, err) = await FidelityContext.Capture(() => sqs.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = url, MessageBody = $"refused-{label}", MessageAttributes = new() { ["N"] = attr() },
            }));
            if (err is null)
            {
                throw Fail($"expected the call to be refused, but it succeeded: {result?.MessageId}");
            }

            Ensure(err.Code == "InvalidParameterValue", $"{label}: code '{err.Code}'");
            Ensure(err.Message == wantMessage, $"{label}: expected message '{wantMessage}', got '{err.Message}'");
        }

        await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = url, MessageBody = "accepted",
            MessageAttributes = new() { ["N"] = new MessageAttributeValue { DataType = "String.custom", StringValue = "ok-value" } },
        });
        var got = new Dictionary<string, MessageAttributeValue?>();
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(30) && !got.ContainsKey("accepted"))
        {
            var msgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2, MessageAttributeNames = ["All"],
            })).Messages ?? [];
            foreach (var msg in msgs)
            {
                got[msg.Body] = msg.MessageAttributes?.GetValueOrDefault("N");
                await sqs.DeleteMessageAsync(url, msg.ReceiptHandle);
            }
        }

        Ensure(got.Count == 1 && got.ContainsKey("accepted"),
            "the queue must hold exactly the accepted message, none of the refused ones; " +
            $"received [{string.Join(", ", got.Keys.Order(StringComparer.Ordinal).Select(k => $"'{k}'"))}]");
        var back = got["accepted"];
        Ensure(back is { DataType: "String.custom", StringValue: "ok-value" },
            $"accepted attribute came back as {{'DataType': '{back?.DataType}', 'StringValue': '{back?.StringValue}'}}, " +
            "expected {'DataType': 'String.custom', 'StringValue': 'ok-value'}");
    }
}
