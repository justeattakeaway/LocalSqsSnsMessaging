// Translated from behavioral/sqs/check_receive_message_attribute_names_wrong_json_type_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Amazon.Runtime;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.receive-message-attribute-names-wrong-json-type-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ReceiveMessageAttributeNamesWrongJsonTypeRejected
{
    private const string Ser = "SerializationException";

    /// <summary>
    /// ReceiveMessage refuses a MessageAttributeNames entry that is a JSON number or a nested
    /// structure, and refuses the field itself when it is not a JSON array, each with
    /// SerializationException and an exact message; a well-formed call still returns the message.
    /// </summary>
    [Test]
    [DisplayName("sqs.receive-message-attribute-names-wrong-json-type-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;

        var url = (await sqs.CreateQueueAsync(FidelityContext.UniqueName("sim2real-behav-sqsrmatype-"))).QueueUrl;
        ctx.OnCleanup(async () =>
        {
            try
            {
                await sqs.DeleteQueueAsync(url);
            }
            catch (AmazonServiceException exc) when (exc.ErrorCode == "AWS.SimpleQueueService.NonExistentQueue")
            {
            }
        });
        var sent = await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = url, MessageBody = "payload",
            MessageAttributes = new() { ["Color"] = new MessageAttributeValue { DataType = "String", StringValue = "blue" } },
        });
        var sentId = sent.MessageId;

        // ---- the refusal under test: a list entry that is a JSON number or a nested structure.
        (string Label, JsonNode Value, string ExpectedMessage)[] matrix =
        [
            ("a number entry", new JsonArray(5), "NUMBER_VALUE cannot be converted to String"),
            ("a nested structure entry", new JsonArray(new JsonObject { ["a"] = "b" }), "Start of structure or map found where not expected"),
        ];
        foreach (var (label, value, expectedMessage) in matrix)
        {
            var (status, body) = await Raw(ctx, url, value);
            Ensure(status == 400, $"MessageAttributeNames={value.ToJsonString()} ({label}) gave {status}: {body.ToJsonString()}");
            var errType = TypeOf(body);
            Ensure(errType == Ser,
                $"MessageAttributeNames={value.ToJsonString()} ({label}) gave __type {body["__type"]?.ToJsonString()}, expected '{Ser}'");
            Ensure(MessageOf(body) == expectedMessage,
                $"MessageAttributeNames={value.ToJsonString()} ({label}) gave message {body["Message"]?.ToJsonString()}, expected '{expectedMessage}'");
            Ensure(body.Select(kv => kv.Key).ToHashSet().SetEquals(["__type", "Message"]),
                $"MessageAttributeNames={value.ToJsonString()} ({label}) refusal body carried extra members: {body.ToJsonString()}");
        }

        // ---- the field itself sent as something other than a JSON array.
        var (statusScalar, bodyScalar) = await Raw(ctx, url, JsonValue.Create(42));
        Ensure(statusScalar == 400, $"MessageAttributeNames=42 gave {statusScalar}: {bodyScalar.ToJsonString()}");
        Ensure(TypeOf(bodyScalar) == Ser, $"MessageAttributeNames=42 gave __type {bodyScalar["__type"]?.ToJsonString()}, expected '{Ser}'");
        Ensure(MessageOf(bodyScalar) == "Unexpected field type",
            $"MessageAttributeNames=42 gave message {bodyScalar["Message"]?.ToJsonString()}, expected 'Unexpected field type'");
        Ensure(bodyScalar.Select(kv => kv.Key).ToHashSet().SetEquals(["__type", "Message"]),
            $"MessageAttributeNames=42 refusal body carried extra members: {bodyScalar.ToJsonString()}");

        // ---- effect-absent: a well-formed poll still returns the one untouched message, unharmed
        //      by the refused calls above.
        var deadline = FidelityTime.StartNew();
        Message? msg = null;
        while (deadline.Elapsed < TimeSpan.FromSeconds(30) && msg is null)
        {
            var msgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 10, MessageAttributeNames = ["All"],
            })).Messages ?? [];
            if (msgs.Count > 0)
            {
                msg = msgs[0];
            }
        }

        Ensure(msg is not null, "a well-formed ReceiveMessage never returned the sent message in 30s");
        Ensure(msg.MessageId == sentId, $"the well-formed receive returned message '{msg.MessageId}', expected '{sentId}'");
        var attrs = msg.MessageAttributes ?? [];
        Ensure(attrs.Count == 1 && attrs.TryGetValue("Color", out var color)
                && color is { StringValue: "blue", DataType: "String", BinaryValue: null }
                && color.StringListValues is null or { Count: 0 } && color.BinaryListValues is null or { Count: 0 },
            $"the well-formed receive returned attributes {{{string.Join(", ", attrs.Select(kv => $"'{kv.Key}': {{'StringValue': '{kv.Value.StringValue}', 'DataType': '{kv.Value.DataType}'}}"))}}}");
        await sqs.DeleteMessageAsync(url, msg.ReceiptHandle);
    }

    private static string TypeOf(JsonObject body) =>
        (body["__type"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "").Split('#')[^1];

    private static string? MessageOf(JsonObject body) =>
        body["Message"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>
    /// POST ReceiveMessage with the raw value for MessageAttributeNames. The Python SigV4-signs the
    /// request; the in-memory handler ignores SQS signatures, so signing is skipped.
    /// </summary>
    private static async Task<(int Status, JsonObject Body)> Raw(FidelityContext ctx, string queueUrl, JsonNode messageAttributeNamesValue)
    {
        var data = new JsonObject
        {
            ["QueueUrl"] = queueUrl,
            ["MessageAttributeNames"] = messageAttributeNamesValue.DeepClone(),
        }.ToJsonString();
        var http = ctx.RawHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/");
        request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(data));
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/x-amz-json-1.0");
        request.Headers.TryAddWithoutValidation("X-Amz-Target", "AmazonSQS.ReceiveMessage");
        using var resp = await http.SendAsync(request, ctx.CancellationToken);
        var text = await resp.Content.ReadAsStringAsync(ctx.CancellationToken);
        var body = text.Length > 0 ? JsonNode.Parse(text)!.AsObject() : [];
        return ((int)resp.StatusCode, body);
    }
}
