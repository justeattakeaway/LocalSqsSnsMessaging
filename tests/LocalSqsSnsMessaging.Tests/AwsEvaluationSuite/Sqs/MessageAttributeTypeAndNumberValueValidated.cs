// Translated from behavioral/sqs/check_message_attribute_type_and_number_value_validated.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.Runtime;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.message-attribute-type-and-number-value-validated</summary>
[Category("AwsEvaluationSuite")]
public sealed class MessageAttributeTypeAndNumberValueValidated
{
    private const string NotANumber = "Can't cast the value of message (user) attribute 'N' to a number.";
    private const string BadType =
        "The type of message (user) attribute 'N' is invalid. You must use only the following " +
        "supported type prefixes: Binary, Number, String.";
    private static readonly string TooPrecise = new('4', 39);

    /// <summary>An attribute spec as the Python dict literal: only the fields it sets.</summary>
    private sealed record Spec(string DataType, string? StringValue = null, byte[]? BinaryValue = null)
    {
        public MessageAttributeValue ToSdk() => new()
        {
            DataType = DataType,
            StringValue = StringValue,
            BinaryValue = BinaryValue is null ? null : new MemoryStream(BinaryValue),
        };

        public override string ToString() =>
            "{" + string.Join(", ", new[]
            {
                $"'DataType': '{DataType}'",
                StringValue is null ? null : $"'StringValue': '{StringValue}'",
                BinaryValue is null ? null : $"'BinaryValue': {Convert.ToHexString(BinaryValue)}",
            }.Where(s => s is not null)) + "}";
    }

    private static readonly (string Label, Spec Attr, string Want)[] Refused =
    [
        ("non-numeric", new Spec("Number", StringValue: "abc"), NotANumber),
        ("leading-space", new Spec("Number", StringValue: " 1"), NotANumber),
        (
            "too-precise",
            new Spec("Number", StringValue: TooPrecise),
            $"Message (user) attribute 'N' number value [{TooPrecise}] must contain fewer than 38 " +
            "precision digits."
        ),
        ("unknown-type", new Spec("Strin", StringValue: "1"), BadType),
        ("empty-custom-label", new Spec("Number.", StringValue: "1"), BadType),
    ];

    // A Python dict: iterated in insertion order.
    private static readonly (string Body, Spec Attr)[] Accepted =
    [
        ("negative-decimal", new Spec("Number", StringValue: "-12.5")),
        ("exponent", new Spec("Number", StringValue: "1e3")),
        ("custom-number", new Spec("Number.float", StringValue: "0.000001")),
        ("custom-binary", new Spec("Binary.png", BinaryValue: [0x89, (byte)'P', (byte)'N', (byte)'G'])),
    ];

    // A Number in exponent form is stored in plain notation; every other accepted value comes back
    // exactly as sent.
    private static readonly Dictionary<string, string> ReadBackStringValue = new() { ["exponent"] = "1000" };

    /// <summary>
    /// SendMessage refuses a non-numeric, space-padded or over-38-digit Number, an unknown type
    /// prefix and an empty custom label, each with its exact message; valid values and custom labels
    /// are stored and returned as sent, except '1e3' which reads back as '1000'.
    /// </summary>
    [Test]
    [DisplayName("sqs.message-attribute-type-and-number-value-validated")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.ErrorMessageText, "non-numeric: code 'SqsService'")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-attrtype-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        foreach (var (body, attr) in Accepted)
        {
            try
            {
                await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = body, MessageAttributes = new() { ["N"] = attr.ToSdk() } });
            }
            catch (AmazonServiceException exc)
            {
                throw new FidelityAssertionException($"{body}: attribute {attr} must be accepted, got ({exc.ErrorCode}, {exc.Message})", exc);
            }
        }

        foreach (var (label, attr, want) in Refused)
        {
            var err = await Error(() => sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "refused", MessageAttributes = new() { ["N"] = attr.ToSdk() } }));
            Ensure(err.Code == "InvalidParameterValue", $"{label}: code '{err.Code}'");
            Ensure(err.Message == want,
                $"{label}: expected message '{want}', got '{err.Message}'");
        }

        var got = new Dictionary<string, MessageAttributeValue?>();
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
                got[m.Body] = (m.MessageAttributes ?? []).GetValueOrDefault("N");
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
            }
        }

        Ensure(got.Keys.Order(StringComparer.Ordinal).SequenceEqual(Accepted.Select(a => a.Body).Order(StringComparer.Ordinal)),
            $"the queue must hold exactly the accepted messages, none refused; received [{string.Join(", ", got.Keys.Order(StringComparer.Ordinal))}]");
        foreach (var (body, sent) in Accepted)
        {
            var want = ReadBackStringValue.TryGetValue(body, out var readBack) ? sent with { StringValue = readBack } : sent;
            var back = Back(got[body]);
            Ensure(SpecEquals(back, want),
                $"{body}: attribute came back as {back}, expected {want} (sent {sent})");
        }
    }

    private static bool SpecEquals(Spec a, Spec b) =>
        a.DataType == b.DataType && a.StringValue == b.StringValue
        && (a.BinaryValue is null ? b.BinaryValue is null : b.BinaryValue is not null && a.BinaryValue.AsSpan().SequenceEqual(b.BinaryValue));

    /// <summary><c>{k: v for k, v in got.items() if k in ("DataType", "StringValue", "BinaryValue")}</c>.</summary>
    private static Spec Back(MessageAttributeValue? value) => new(
        value?.DataType!,
        value?.StringValue,
        value?.BinaryValue?.ToArray());

    /// <summary>
    /// The botocore Error dict ``fn`` raises. An unexpected success raises too, naming what came
    /// back: an invalid attribute that is accepted must not read as a code mismatch.
    /// </summary>
    private static async Task<AwsError> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"expected the call to be refused, but it succeeded: {result}");
    }
}
