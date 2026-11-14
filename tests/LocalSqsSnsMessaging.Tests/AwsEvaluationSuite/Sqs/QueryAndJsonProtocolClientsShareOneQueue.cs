// Translated from behavioral/sqs/check_query_and_json_protocol_clients_share_one_queue.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.query-and-json-protocol-clients-share-one-queue</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryAndJsonProtocolClientsShareOneQueue
{
    private static Dictionary<string, string> Tags => new() { ["sim2real"] = "query-and-json-protocol-clients-share-one-queue" };

    private static string Local(XElement e) => e.Name.LocalName;

    private static List<XElement> FindAll(XElement root, string name) => [.. root.DescendantsAndSelf().Where(e => Local(e) == name)];

    private static string? Text(XElement elem, string name) => elem.Elements().FirstOrDefault(c => Local(c) == name)?.Value;

    /// <summary>
    /// A signed AWS Query (form-encoded, XML-answering) caller using the ctx client's credentials
    /// and endpoint.
    /// </summary>
    /// <remarks>
    /// The in-memory handler ignores SigV4, so the request is sent unsigned through
    /// <see cref="FidelityContext.RawHttpClient"/>; the method, body and Content-Type match the Python.
    /// </remarks>
    private static Func<Dictionary<string, string>, Task<(int Status, XElement Root)>> QueryClient(FidelityContext ctx)
    {
        var http = ctx.RawHttpClient();
        return async parameters =>
        {
            var all = new Dictionary<string, string>(parameters) { ["Version"] = "2012-11-05" };
            var body = string.Join("&", all.Select(kv => $"{WebUtility.UrlEncode(kv.Key)}={WebUtility.UrlEncode(kv.Value)}"));
            using var content = new StringContent(body, Encoding.UTF8);
            content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/x-www-form-urlencoded; charset=utf-8");
            using var resp = await http.PostAsync("/", content, ctx.CancellationToken);
            var raw = await resp.Content.ReadAsStringAsync(ctx.CancellationToken);
            return ((int)resp.StatusCode, XElement.Parse(raw));
        };
    }

    /// <summary>
    /// Messages and attributes written over the AWS Query protocol read back identically over JSON,
    /// and vice versa: both wire protocols SQS serves share one queue state.
    /// </summary>
    [Test]
    [DisplayName("sqs.query-and-json-protocol-clients-share-one-queue")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.Protocol, "SQS Query-protocol (form-encoded) requests get a JSON error, not XML: not well-formed (invalid token): line 1, column 0")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var query = QueryClient(ctx);
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-proto-"),
            Attributes = new() { ["VisibilityTimeout"] = "30" },
            Tags = Tags,
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        // Query -> JSON
        var qBody = FidelityContext.UniqueName("via-query-");
        var (status, root) = await query(new()
        {
            ["Action"] = "SendMessage",
            ["QueueUrl"] = url,
            ["MessageBody"] = qBody,
            ["MessageAttribute.1.Name"] = "proto",
            ["MessageAttribute.1.Value.DataType"] = "String",
            ["MessageAttribute.1.Value.StringValue"] = "query",
        });
        Ensure(status == 200, $"Query SendMessage answered HTTP {status}: {root.ToString()[..Math.Min(300, root.ToString().Length)]}");
        Ensure(Local(root) == "SendMessageResponse", $"Query answered a '{Local(root)}' root");
        var result = FindAll(root, "SendMessageResult");
        Ensure(result.Count == 1, $"Query SendMessage response has {result.Count} SendMessageResult");
        var qId = Text(result[0], "MessageId");
        Ensure(Text(result[0], "MD5OfMessageBody") == Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(qBody))),
            $"Query MD5OfMessageBody '{Text(result[0], "MD5OfMessageBody")}' is not the body's MD5");
        var qAttrMd5 = Text(result[0], "MD5OfMessageAttributes");

        List<Message> got = [];
        var deadline = Stopwatch.StartNew();
        while (got.Count == 0 && deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            got = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url,
                WaitTimeSeconds = 3,
                MaxNumberOfMessages = 10,
                MessageAttributeNames = ["All"],
            })).Messages ?? [];
        }

        Ensure(got.Select(x => x.Body).SequenceEqual([qBody]),
            $"the JSON client received [{string.Join(", ", got.Select(x => x.Body))}], expected the Query-sent body");
        var m = got[0];
        Ensure(m.MessageId == qId, "the JSON client saw a different MessageId than Query returned");
        var mAttrMd5 = m.MD5OfMessageAttributes ?? throw new KeyNotFoundException("MD5OfMessageAttributes");
        Ensure(mAttrMd5 == qAttrMd5,
            $"MD5OfMessageAttributes differs across protocols: JSON '{mAttrMd5}', Query '{qAttrMd5}'");
        var mAttrs = m.MessageAttributes ?? throw new KeyNotFoundException("MessageAttributes");
        Ensure(mAttrs.Count == 1 && mAttrs.TryGetValue("proto", out var proto)
                && proto is { DataType: "String", StringValue: "query", BinaryValue: null },
            $"the Query-sent attribute came back over JSON as {{{string.Join(", ", mAttrs.Select(kv => $"{kv.Key}: {kv.Value.DataType}/{kv.Value.StringValue}"))}}}");
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });

        // JSON -> Query
        var jBody = FidelityContext.UniqueName("via-json-");
        var sent = await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = url,
            MessageBody = jBody,
            MessageAttributes = new() { ["proto"] = new() { DataType = "Number", StringValue = "42" } },
        });
        List<XElement> msgs = [];
        deadline = Stopwatch.StartNew();
        while (msgs.Count == 0 && deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            (status, root) = await query(new()
            {
                ["Action"] = "ReceiveMessage",
                ["QueueUrl"] = url,
                ["MaxNumberOfMessages"] = "10",
                ["WaitTimeSeconds"] = "3",
                ["MessageAttributeName.1"] = "All",
            });
            Ensure(status == 200, $"Query ReceiveMessage answered HTTP {status}");
            msgs = FindAll(root, "Message");
        }

        Ensure(msgs.Count == 1, $"Query ReceiveMessage returned {msgs.Count} messages, expected 1");
        var qm = msgs[0];
        Ensure(Text(qm, "Body") == jBody, $"Query received body '{Text(qm, "Body")}'");
        Ensure(Text(qm, "MessageId") == (sent.MessageId ?? throw new KeyNotFoundException("MessageId")), "Query saw a different MessageId");
        Ensure(Text(qm, "MD5OfBody") == (sent.MD5OfMessageBody ?? throw new KeyNotFoundException("MD5OfMessageBody")), "MD5OfBody differs across protocols");
        Ensure(Text(qm, "MD5OfMessageAttributes") == (sent.MD5OfMessageAttributes ?? throw new KeyNotFoundException("MD5OfMessageAttributes")),
            "MD5OfMessageAttributes differs across protocols");
        var attrs = new Dictionary<string, XElement>();
        foreach (var a in qm.Elements().Where(a => Local(a) == "MessageAttribute"))
        {
            attrs[Text(a, "Name") ?? ""] = a;
        }

        Ensure(attrs.Keys.SequenceEqual(["proto"]), $"Query received attribute names [{string.Join(", ", attrs.Keys)}]");
        var value = attrs["proto"].Elements().Where(v => Local(v) == "Value").ToList()[0];
        Ensure((Text(value, "DataType"), Text(value, "StringValue")) == ("Number", "42"),
            $"the JSON-sent Number attribute came back over Query as ({Text(value, "DataType")}, {Text(value, "StringValue")})");
        var handle = Text(qm, "ReceiptHandle")!;
        (status, _) = await query(new() { ["Action"] = "DeleteMessage", ["QueueUrl"] = url, ["ReceiptHandle"] = handle });
        Ensure(status == 200, $"Query DeleteMessage with a Query-received handle answered {status}");

        // Control plane: a Query write is a JSON read.
        (status, _) = await query(new()
        {
            ["Action"] = "SetQueueAttributes",
            ["QueueUrl"] = url,
            ["Attribute.1.Name"] = "VisibilityTimeout",
            ["Attribute.1.Value"] = "45",
        });
        Ensure(status == 200, $"Query SetQueueAttributes answered {status}");
        string? vis = null;
        deadline = Stopwatch.StartNew();
        while (vis != "45" && deadline.Elapsed < TimeSpan.FromSeconds(60))
        {
            vis = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["VisibilityTimeout"] })).Attributes!["VisibilityTimeout"];
            if (vis != "45")
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }

        Ensure(vis == "45", $"a VisibilityTimeout set over Query reads back over JSON as '{vis}'");

        async Task<Dictionary<string, string>> Left()
            => (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = url,
                AttributeNames = ["ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible"],
            })).Attributes!;

        static bool AllZero(Dictionary<string, string> d) => d.Values.ToHashSet().SetEquals(["0"]);

        var left = await Left();
        deadline = Stopwatch.StartNew();
        while (!AllZero(left) && deadline.Elapsed < TimeSpan.FromSeconds(60))
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            left = await Left();
        }

        Ensure(AllZero(left),
            $"both messages were deleted (one per protocol) but the counters read {{{string.Join(", ", left.Select(kv => $"{kv.Key}: {kv.Value}"))}}}");
    }
}
