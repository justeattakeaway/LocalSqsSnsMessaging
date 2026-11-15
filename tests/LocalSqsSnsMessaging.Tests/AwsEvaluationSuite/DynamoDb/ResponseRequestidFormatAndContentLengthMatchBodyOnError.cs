// Translated from behavioral/dynamodb/check_response_requestid_format_and_content_length_match_body_on_error.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.RegularExpressions;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.response-requestid-format-and-content-length-match-body-on-error</summary>
/// <remarks>
/// The Python check reads each raw response through a botocore <c>before-parse</c> hook; here every
/// call goes through <c>ctx.DynamoDbRaw</c> with the JSON body boto3 sends, so the raw status,
/// headers and body are captured the same way.
/// </remarks>
[Category("AwsEvaluationSuite")]
public sealed partial class ResponseRequestidFormatAndContentLengthMatchBodyOnError
{
    [GeneratedRegex("^[0-9A-Z]{52}$")]
    private static partial Regex RequestIdRe();

    private static readonly (string? Code, string? Message) ConditionalFailed = ("ConditionalCheckFailedException", "The conditional request failed");

    private static string Json(object value) => JsonSerializer.Serialize(value);

    /// <summary>
    /// A refused DynamoDB response carries the same x-amzn-RequestId format and an exact
    /// Content-Length as a successful one; its error code and message never move.
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.DynamoDbDetail, "x-amzn-RequestId is not DynamoDB's 52 upper-case alphanumerics")]
    [DisplayName("dynamodb.response-requestid-format-and-content-length-match-body-on-error")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var seen = new List<(string Operation, int Status, IReadOnlyDictionary<string, string> Headers, string Body)>();

        // ddb.meta.events.register("before-parse.dynamodb", capture): every call below records its raw response.
        async Task<RawDynamoDbResponse> Send(string operation, object body)
        {
            var response = await ctx.DynamoDbRaw(operation, Json(body));
            seen.Add((operation, (int)response.StatusCode, response.Headers, response.Body));
            return response;
        }

        // A call that must succeed: botocore raises ClientError on an error response.
        async Task<JsonElement> Call(string operation, object body)
        {
            var response = await Send(operation, body);
            if ((int)response.StatusCode >= 300)
            {
                throw Fail($"An error occurred ({response.ErrorCode}) when calling the {operation} operation: {response.ErrorMessage}");
            }

            return response.Json!.Value;
        }

        // The (code, message) the call is refused with. An unexpected success raises, naming it.
        async Task<(string? Code, string? Message)> Error(string operation, object body)
        {
            var response = await Send(operation, body);
            if ((int)response.StatusCode < 300)
            {
                throw Fail($"expected the call to be refused, but it succeeded: {response.Body}");
            }

            return (response.ErrorCode, response.ErrorMessage);
        }

        string Last(string op, int status)
        {
            Ensure(seen.Count > 0, $"{op}: no response was captured");
            var (gotOp, gotStatus, headers, body) = seen[^1];
            Ensure((gotOp, gotStatus) == (op, status), $"captured {gotOp} {gotStatus}, expected {op} {status}");
            var lowered = headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value, StringComparer.Ordinal);
            var sortedNames = lowered.Keys.Order(StringComparer.Ordinal).ToList();
            var requestId = lowered.GetValueOrDefault("x-amzn-requestid");
            Ensure(requestId is not null, $"{op} {status}: no x-amzn-requestid header among {Py.Repr(sortedNames)}");
            Ensure(RequestIdRe().IsMatch(requestId), $"{op} {status}: x-amzn-requestid {Py.Repr(requestId)} does not match {Py.Repr(RequestIdRe().ToString())}");
            var contentLength = lowered.GetValueOrDefault("content-length");
            Ensure(contentLength is not null, $"{op} {status}: no content-length header among {Py.Repr(sortedNames)}");
            Ensure(contentLength.Length > 0 && contentLength.All(char.IsAsciiDigit), $"{op} {status}: content-length {Py.Repr(contentLength)} is not an unsigned decimal");
            var bodyLength = Encoding.UTF8.GetByteCount(body);
            Ensure(
                long.Parse(contentLength, CultureInfo.InvariantCulture) == bodyLength,
                $"{op} {status}: content-length {Py.Repr(contentLength)} != literal body length {bodyLength}");
            return body;
        }

        // Positive control: DescribeTable on a table that exists answers 200 with the same header
        // shape the refusals below are held to.
        var table = FidelityContext.UniqueName("sim2real-behav-reqid-err-");
        await Call("CreateTable", new
        {
            TableName = table,
            AttributeDefinitions = new[] { new { AttributeName = "pk", AttributeType = "S" } },
            KeySchema = new[] { new { AttributeName = "pk", KeyType = "HASH" } },
            BillingMode = "PAY_PER_REQUEST",
        });
        ctx.OnCleanup(async () =>
        {
            var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(table));
            if (error is not null && error.Code != "ResourceNotFoundException")
            {
                throw error.Exception;
            }
        });
        Last("CreateTable", 200);
        var clock = FidelityTime.StartNew();
        while ((await Call("DescribeTable", new { TableName = table })).GetProperty("Table").GetProperty("TableStatus").GetString() != "ACTIVE")
        {
            Ensure(clock.Elapsed < TimeSpan.FromSeconds(300.0), "the table never reached ACTIVE");
            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }

        Last("DescribeTable", 200);
        var item = new Dictionary<string, object> { ["pk"] = new { S = "k1" } };
        await Call("PutItem", new { TableName = table, Item = item });
        Last("PutItem", 200);

        // Negative 1: a conditional write that fails leaves the item unchanged, proven by a read.
        var (code, msg) = await Error("PutItem", new
        {
            TableName = table,
            Item = new Dictionary<string, object> { ["pk"] = new { S = "k1" }, ["note"] = new { S = "clobber" } },
            ConditionExpression = "attribute_not_exists(pk)",
        });
        Ensure((code, msg) == ConditionalFailed, $"the conditional put gave {Py.Repr(code)}: {Py.Repr(msg)}");
        Last("PutItem", 400);
        var got = await Call("GetItem", new { TableName = table, Key = new Dictionary<string, object> { ["pk"] = new { S = "k1" } }, ConsistentRead = true });
        Last("GetItem", 200);
        var gotItem = got.TryGetProperty("Item", out var i) ? i : (JsonElement?)null;
        Ensure(
            gotItem is not null && Py.Equal(gotItem.Value, JsonDocument.Parse(Json(item)).RootElement),
            $"the failed conditional write mutated the item to {Py.Repr(gotItem)}");

        // Negative 2: describing a table that was never created.
        var missing = FidelityContext.UniqueName("sim2real-behav-reqid-missing-");
        (code, msg) = await Error("DescribeTable", new { TableName = missing });
        Ensure(
            (code, msg) == ("ResourceNotFoundException", $"Requested resource not found: Table: {missing} not found"),
            $"describing a missing table gave {Py.Repr(code)}: {Py.Repr(msg)}");
        Last("DescribeTable", 400);

        // Negative 3: a key whose attribute type does not match the table's schema.
        (code, msg) = await Error("GetItem", new { TableName = table, Key = new Dictionary<string, object> { ["pk"] = new { N = "1" } } });
        Ensure(
            (code, msg) == ("ValidationException", "The provided key element does not match the schema"),
            $"a key of the wrong type gave {Py.Repr(code)}: {Py.Repr(msg)}");
        Last("GetItem", 400);
    }
}
