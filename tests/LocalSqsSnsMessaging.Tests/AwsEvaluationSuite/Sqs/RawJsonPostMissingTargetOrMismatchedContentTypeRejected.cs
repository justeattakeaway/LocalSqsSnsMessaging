// Translated from behavioral/sqs/check_raw_json_post_missing_target_or_mismatched_content_type_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.raw-json-post-missing-target-or-mismatched-content-type-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class RawJsonPostMissingTargetOrMismatchedContentTypeRejected
{
    /// <summary>
    /// A signed CreateQueue POST with no X-Amz-Target, and one with Content-Type
    /// application/json instead of application/x-amz-json-1.0, are both refused as an
    /// unknown operation and create no queue; a request carrying both headers succeeds.
    ///
    /// Reference: https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/sqs-making-api-requests-json.html
    /// </summary>
    [Test]
    [DisplayName("sqs.raw-json-post-missing-target-or-mismatched-content-type-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.Protocol, "a JSON POST missing X-Amz-Target is not refused with 400 UnknownOperationException")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var targetName = FidelityContext.UniqueName("sim2real-behav-rawpost-");
        var body = Encoding.UTF8.GetBytes(new JsonObject { ["QueueName"] = targetName }.ToJsonString());

        var (statusA, textA) = await RawPost(ctx, body, contentType: "application/x-amz-json-1.0", target: null);
        Ensure(statusA == 400, $"missing X-Amz-Target gave status {statusA}, body '{textA}'");
        var payloadA = JsonNode.Parse(textA)!.AsObject();
        Ensure(JsonNode.DeepEquals(payloadA, new JsonObject { ["__type"] = "com.amazon.coral.service#UnknownOperationException" }),
            $"missing X-Amz-Target gave body {payloadA.ToJsonString()}");

        var (statusB, textB) = await RawPost(ctx, body, contentType: "application/json", target: "AmazonSQS.CreateQueue");
        Ensure(statusB == 404, $"Content-Type application/json gave status {statusB}, body '{textB}'");
        Ensure(textB == "<UnknownOperationException/>\n", $"Content-Type application/json gave body '{textB}'");

        Ensure(CodeOf(payloadA) == CodeOf(textB) && CodeOf(textB) == "UnknownOperationException", "assert");

        var notCreated = await ErrorCode(() => sqs.GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = targetName }));
        Ensure(notCreated == "AWS.SimpleQueueService.NonExistentQueue",
            $"a queue named after the rejected requests' body exists: '{notCreated}'");

        var controlName = FidelityContext.UniqueName("sim2real-behav-rawpost-");
        var controlBody = Encoding.UTF8.GetBytes(new JsonObject { ["QueueName"] = controlName }.ToJsonString());
        var (statusOk, textOk) = await RawPost(ctx, controlBody, contentType: "application/x-amz-json-1.0", target: "AmazonSQS.CreateQueue");
        Ensure(statusOk == 200, $"a correctly-headered CreateQueue gave status {statusOk}, body '{textOk}'");
        var controlUrl = JsonNode.Parse(textOk)!["QueueUrl"]!.GetValue<string>();
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(controlUrl));
        Ensure((await sqs.GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = controlName })).QueueUrl == controlUrl, "assert");
    }

    private static string CodeOf(JsonObject body) => body["__type"]!.GetValue<string>().Split('#')[^1];

    private static string CodeOf(string body)
    {
        var match = Regex.Match(body, @"\A<(\w+)/>\n\z");
        // Python's match.group(1) on a failed fullmatch raises AttributeError.
        return match.Success ? match.Groups[1].Value : throw new InvalidOperationException("'NoneType' object has no attribute 'group'");
    }

    /// <summary>The Error.Code <paramref name="fn"/> raises. An unexpected success raises too, naming what came back.</summary>
    private static async Task<string?> ErrorCode<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error is not null ? error.Code : throw Fail($"expected the call to be refused, but it succeeded: {result}");
    }

    /// <summary>
    /// POSTs <paramref name="body"/> to the SQS endpoint with exactly the given headers. The Python
    /// SigV4-signs the request; the in-memory handler ignores signatures, so signing is skipped.
    /// </summary>
    private static async Task<(int Status, string Text)> RawPost(FidelityContext ctx, byte[] body, string contentType, string? target)
    {
        var http = ctx.RawHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/");
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        if (target is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Amz-Target", target);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        using var resp = await http.SendAsync(request, cts.Token);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(cts.Token));
    }
}
