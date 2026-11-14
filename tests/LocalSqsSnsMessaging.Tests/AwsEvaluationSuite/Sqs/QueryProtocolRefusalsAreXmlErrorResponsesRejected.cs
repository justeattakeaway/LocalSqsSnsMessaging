// Translated from behavioral/sqs/check_query_protocol_refusals_are_xml_error_responses_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.query-protocol-refusals-are-xml-error-responses-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryProtocolRefusalsAreXmlErrorResponsesRejected
{
    private const string NoSuchQueue = "AWS.SimpleQueueService.NonExistentQueue";
    private const string DelayMsg = "Value 901 for parameter DelaySeconds is invalid. Reason: DelaySeconds must be >= 0 and <= 900.";

    private static Dictionary<string, string> Tags => new() { ["sim2real"] = "query-protocol-refusals-are-xml-error-responses-rejected" };

    private static string Local(XElement e) => e.Name.LocalName;

    /// <summary>A signer for the AWS Query protocol using the ctx client's credentials and endpoint.</summary>
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

    /// <summary>(status, root element, Error/Type, Error/Code, Error/Message) of a Query response.</summary>
    private static (int Status, string Root, string? Type, string? Code, string? Message) QueryError((int Status, XElement Root) response)
    {
        var (status, root) = response;
        var error = root.Elements().Where(e => Local(e) == "Error").ToList();
        var fields = new Dictionary<string, string?>();
        if (error.Count > 0)
        {
            foreach (var c in error[0].Elements())
            {
                fields[Local(c)] = c.Value;
            }
        }

        return (status, Local(root), fields.GetValueOrDefault("Type"), fields.GetValueOrDefault("Code"), fields.GetValueOrDefault("Message"));
    }

    /// <summary>The botocore Error dict ``fn`` raises. An unexpected success raises, naming the result.</summary>
    private static async Task<AwsError> JsonError<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"expected the call to be refused, but it succeeded: {result}");
    }

    /// <summary>
    /// Over the AWS Query protocol SQS refuses with an XML ErrorResponse (Type, Code, Message) for an
    /// absent queue, DelaySeconds 901, an 11-entry batch and an unknown Action; the absent-queue message
    /// is Query-specific ("... for this wsdl version."), and only the valid send is enqueued.
    /// </summary>
    [Test]
    [DisplayName("sqs.query-protocol-refusals-are-xml-error-responses-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.Protocol, "SQS Query-protocol (form-encoded) requests get a JSON error, not XML: not well-formed (invalid token): line 1, column 0")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var query = QueryClient(ctx);
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-qerr-"), Tags = Tags })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var absent = FidelityContext.UniqueName("sim2real-behav-qerr-absent-");

        var got = QueryError(await query(new() { ["Action"] = "GetQueueUrl", ["QueueName"] = absent }));
        Ensure(got == (400, "ErrorResponse", "Sender", NoSuchQueue, "The specified queue does not exist for this wsdl version."),
            $"Query GetQueueUrl for an absent queue answered {got}");
        var j = await JsonError(() => sqs.GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = absent }));
        Ensure((j.Code, j.Message) == (NoSuchQueue, "The specified queue does not exist."),
            $"JSON GetQueueUrl for the same absent queue answered {j}");

        got = QueryError(await query(new()
        {
            ["Action"] = "SendMessage",
            ["QueueUrl"] = url,
            ["MessageBody"] = "too-late",
            ["DelaySeconds"] = "901",
        }));
        Ensure(got == (400, "ErrorResponse", "Sender", "InvalidParameterValue", DelayMsg),
            $"Query SendMessage with DelaySeconds 901 answered {got}");
        j = await JsonError(() => sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "too-late", DelaySeconds = 901 }));
        Ensure((j.Code, j.Message) == ("InvalidParameterValue", DelayMsg),
            $"JSON SendMessage with DelaySeconds 901 answered {j}");

        var entries = new Dictionary<string, string> { ["Action"] = "DeleteMessageBatch", ["QueueUrl"] = url };
        for (var k = 1; k < 12; k++)
        {
            entries[$"DeleteMessageBatchRequestEntry.{k}.Id"] = $"i{k}";
            entries[$"DeleteMessageBatchRequestEntry.{k}.ReceiptHandle"] = "not-a-handle";
        }

        got = QueryError(await query(entries));
        Ensure(got == (400, "ErrorResponse", "Sender",
                "AWS.SimpleQueueService.TooManyEntriesInBatchRequest",
                "Maximum number of entries per request are 10. You have sent 11."),
            $"Query DeleteMessageBatch with 11 entries answered {got}");

        got = QueryError(await query(new() { ["Action"] = "NoSuchAction", ["QueueUrl"] = url }));
        Ensure(got == (400, "ErrorResponse", "Sender", "InvalidAction", "The action NoSuchAction is not valid for this endpoint."),
            $"Query with an unknown Action answered {got}");

        // Positive control: a valid Query send on the same queue is accepted, and it is the only
        // message the queue holds.
        var okBody = FidelityContext.UniqueName("ok-");
        var (status, root) = await query(new() { ["Action"] = "SendMessage", ["QueueUrl"] = url, ["MessageBody"] = okBody });
        Ensure((status, Local(root)) == (200, "SendMessageResponse"),
            $"a valid Query SendMessage answered {status} '{Local(root)}'");
        var bodies = new List<string>();
        var deadline = FidelityTime.StartNew();
        while (!bodies.Contains(okBody) && deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            foreach (var m in (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 3 })).Messages ?? [])
            {
                bodies.Add(m.Body);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
            }
        }

        bodies.AddRange(((await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 })).Messages ?? []).Select(m => m.Body));
        Ensure(bodies.SequenceEqual([okBody]),
            $"the queue delivered [{string.Join(", ", bodies)}]; expected only the valid Query-sent message");
    }
}
