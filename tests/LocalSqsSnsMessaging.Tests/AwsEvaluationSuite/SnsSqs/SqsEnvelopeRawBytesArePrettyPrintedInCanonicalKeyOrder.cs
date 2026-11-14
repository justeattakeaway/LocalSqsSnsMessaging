// Translated from behavioral/sns__sqs/check_sqs_envelope_raw_bytes_are_pretty_printed_in_canonical_key_order.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SnsMessageAttributeValue = Amazon.SimpleNotificationService.Model.MessageAttributeValue;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.SnsSqs;

/// <summary>sns__sqs.sqs-envelope-raw-bytes-are-pretty-printed-in-canonical-key-order</summary>
[Category("AwsEvaluationSuite")]
public sealed partial class SqsEnvelopeRawBytesArePrettyPrintedInCanonicalKeyOrder
{
    private const string Check = "sqs-envelope-raw-bytes-are-pretty-printed-in-canonical-key-order";

    private static readonly string[] BareKeys =
    [
        "Type", "MessageId", "TopicArn", "Message", "Timestamp",
        "SignatureVersion", "Signature", "SigningCertURL", "UnsubscribeURL",
    ];

    private static readonly string[] FullKeys =
    [
        "Type", "MessageId", "TopicArn", "Subject", "Message", "Timestamp",
        "SignatureVersion", "Signature", "SigningCertURL", "UnsubscribeURL", "MessageAttributes",
    ];

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$")]
    private static partial Regex Timestamp();

    /// <summary>
    /// SNS delivers to SQS a pretty-printed envelope, one `"Key" : value` line per member in the order Type,
    /// MessageId, TopicArn, [Subject], Message, Timestamp, SignatureVersion, Signature, SigningCertURL,
    /// UnsubscribeURL, [MessageAttributes]; optional members absent, one compact line per attribute.
    /// </summary>
    [Test]
    [DisplayName("sns__sqs.sqs-envelope-raw-bytes-are-pretty-printed-in-canonical-key-order")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsEnvelope, "the envelope is written as compact JSON in a different key order instead of SNS's pretty-printed layout")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var sqs = ctx.Sqs;

        var topic = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName("sim2real-xsvc-sns-layout-"),
            Tags = [new() { Key = "sim2real", Value = Check }],
        })).TopicArn;
        ctx.OnCleanup(Teardown(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = topic }), "NotFound"));
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-xsvc-sns-layout-"),
            Tags = new() { ["sim2real"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(Teardown(() => sqs.DeleteQueueAsync(url),
            "AWS.SimpleQueueService.NonExistentQueue", "QueueDoesNotExist"));
        var qArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = url,
            Attributes = new()
            {
                ["Policy"] = new JsonObject
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
                }.ToJsonString(),
            },
        });
        var sub = (await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topic,
            Protocol = "sqs",
            Endpoint = qArn,
            ReturnSubscriptionArn = true,
        })).SubscriptionArn;
        ctx.OnCleanup(Teardown(() => sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = sub }), "NotFound"));

        var bare = FidelityContext.UniqueName("sim2real-layout-bare-");
        var full = FidelityContext.UniqueName("sim2real-layout-full-");
        const string subject = "layout subject";
        byte[] blob = [0x00, 0x01, 0xfe];
        var attrs = new Dictionary<string, SnsMessageAttributeValue>
        {
            ["kind"] = new() { DataType = "String", StringValue = "plain" },
            ["count"] = new() { DataType = "Number", StringValue = "42" },
            ["blob"] = new() { DataType = "Binary", BinaryValue = new MemoryStream(blob) },
        };
        var bareId = (await sns.PublishAsync(new PublishRequest { TopicArn = topic, Message = bare })).MessageId;
        var fullId = (await sns.PublishAsync(new PublishRequest
        {
            TopicArn = topic,
            Message = full,
            Subject = subject,
            MessageAttributes = attrs,
        })).MessageId;

        var got = await Receive(sqs, url, [bare, full]);

        foreach (var (payload, msgId, keys) in new[] { (bare, bareId, BareKeys), (full, fullId, FullKeys) })
        {
            var raw = got[payload];
            using var parsed = JsonDocument.Parse(raw);
            var pairs = parsed.RootElement.EnumerateObject().ToList(); // object_pairs_hook=list
            var order = pairs.Select(p => p.Name).ToList();
            Ensure(order.SequenceEqual(keys), $"envelope keys for '{payload}' are {Show(order)}, expected {Show(keys)}");
            var doc = new Dictionary<string, JsonElement>();
            foreach (var p in pairs)
            {
                doc[p.Name] = p.Value;
            }

            var rendered = Render(pairs);
            Ensure(raw == rendered,
                $"the delivered body is not laid out as SNS writes it; got {PyRepr(Head(raw, 400))}, " +
                $"expected {PyRepr(Head(rendered, 400))}");
            Ensure(doc["Type"].GetString() == "Notification", $"Type was {PyRepr(doc["Type"].GetString())}");
            Ensure(doc["MessageId"].GetString() == msgId,
                $"the envelope MessageId does not match the one Publish returned for '{payload}'");
            Ensure(doc["TopicArn"].GetString() == topic, "the envelope TopicArn is not the topic published to");
            Ensure(doc["Message"].GetString() == payload, $"Message was {PyRepr(doc["Message"].GetString())}, expected '{payload}'");
            Ensure(doc["SignatureVersion"].GetString() == "1", $"SignatureVersion was {PyRepr(doc["SignatureVersion"].GetString())}");
            Ensure(Timestamp().IsMatch(doc["Timestamp"].GetString()!), $"Timestamp was {PyRepr(doc["Timestamp"].GetString())}");
            var unsubQuery = new Uri(doc["UnsubscribeURL"].GetString()!).Query.TrimStart('?');
            var qsl = ParseQsl(unsubQuery);
            Ensure(qsl.SequenceEqual([("Action", "Unsubscribe"), ("SubscriptionArn", sub)]),
                $"UnsubscribeURL query was {PyRepr(unsubQuery.Split("SubscriptionArn=")[0])}..., expected " +
                "Action=Unsubscribe then this subscription's ARN");
        }

        var fullDoc = JsonNode.Parse(got[full])!.AsObject();
        Ensure((string?)fullDoc["Subject"] == subject, $"Subject was {PyRepr((string?)fullDoc["Subject"])}");
        var delivered = fullDoc["MessageAttributes"];
        var expected = new JsonObject
        {
            ["kind"] = new JsonObject { ["Type"] = "String", ["Value"] = "plain" },
            ["count"] = new JsonObject { ["Type"] = "Number", ["Value"] = "42" },
            ["blob"] = new JsonObject { ["Type"] = "Binary", ["Value"] = Convert.ToBase64String(blob) },
        };
        Ensure(JsonNode.DeepEquals(delivered, expected),
            $"envelope MessageAttributes were {delivered?.ToJsonString()}, expected {expected.ToJsonString()}");
    }

    private static string Head(string s, int n) => s.Length <= n ? s : s[..n];

    private static string Show(IEnumerable<string> values) => "[" + string.Join(", ", values.Select(v => $"'{v}'")) + "]";

    /// <summary>Python's repr of a str (single-quoted, escapes for backslashes, quotes and control characters).</summary>
    private static string PyRepr(string? s)
    {
        if (s is null)
        {
            return "None";
        }

        var sb = new StringBuilder("'");
        foreach (var c in s)
        {
            sb.Append(c switch
            {
                '\\' => @"\\",
                '\'' => @"\'",
                '\n' => @"\n",
                '\r' => @"\r",
                '\t' => @"\t",
                < ' ' => $"\\x{(int)c:x2}",
                _ => c.ToString(),
            });
        }

        return sb.Append('\'').ToString();
    }

    /// <summary>json.dumps(value) with Python's defaults: ", " and ": " separators, ensure_ascii.</summary>
    private static string PyDumps(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => PyDumpsString(value.GetString()!),
        JsonValueKind.Object => "{" + string.Join(", ", value.EnumerateObject().Select(p => $"{PyDumpsString(p.Name)}: {PyDumps(p.Value)}")) + "}",
        JsonValueKind.Array => "[" + string.Join(", ", value.EnumerateArray().Select(PyDumps)) + "]",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        _ => value.GetRawText(),
    };

    private static string PyDumpsString(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s)
        {
            sb.Append(c switch
            {
                '"' => "\\\"",
                '\\' => @"\\",
                '\n' => @"\n",
                '\r' => @"\r",
                '\t' => @"\t",
                '\b' => @"\b",
                '\f' => @"\f",
                < ' ' or > '~' => "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture),
                _ => c.ToString(),
            });
        }

        return sb.Append('"').ToString();
    }

    /// <summary>The envelope as SNS lays it out, from its own parsed members.</summary>
    private static string Render(List<JsonProperty> pairs)
    {
        var lines = new List<string>();
        foreach (var pair in pairs)
        {
            if (pair.Name == "MessageAttributes")
            {
                var inner = string.Join(",\n", pair.Value.EnumerateObject().Select(attr =>
                    $"    {PyDumpsString(attr.Name)} : "
                    + "{" + string.Join(",", attr.Value.EnumerateObject().Select(kv => $"{PyDumpsString(kv.Name)}:{PyDumps(kv.Value)}")) + "}"));
                lines.Add($"  {PyDumpsString(pair.Name)} : {{\n{inner}\n  }}");
            }
            else
            {
                lines.Add($"  {PyDumpsString(pair.Name)} : {PyDumps(pair.Value)}");
            }
        }

        return "{\n" + string.Join(",\n", lines) + "\n}";
    }

    /// <summary>urllib.parse.parse_qsl: the query's (name, value) pairs in order, blank values dropped.</summary>
    private static List<(string, string)> ParseQsl(string query)
    {
        var result = new List<(string, string)>();
        foreach (var pair in query.Split(['&', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq < 0)
            {
                continue;
            }

            var value = Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            if (value.Length > 0)
            {
                result.Add((Uri.UnescapeDataString(pair[..eq].Replace('+', ' ')), value));
            }
        }

        return result;
    }

    /// <summary>Raw body of the first delivery of each payload, keyed by its Message.</summary>
    private static async Task<Dictionary<string, string>> Receive(AmazonSQSClient sqs, string url, HashSet<string> payloads, double timeoutS = 90.0)
    {
        var got = new Dictionary<string, string>();
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS) && !payloads.SetEquals(got.Keys))
        {
            foreach (var m in (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 })).Messages ?? [])
            {
                var message = (string?)JsonNode.Parse(m.Body)!["Message"];
                if (message is not null && payloads.Contains(message))
                {
                    got.TryAdd(message, m.Body);
                }

                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
            }
        }

        Ensure(payloads.SetEquals(got.Keys),
            $"only {Show(got.Keys.Order(StringComparer.Ordinal))} of {Show(payloads.Order(StringComparer.Ordinal))} reached the queue in {timeoutS.ToString("F0", CultureInfo.InvariantCulture)}s");
        return got;
    }

    /// <summary>A cleanup running undo() where a failed delete raises; only notFound means gone.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };
}
