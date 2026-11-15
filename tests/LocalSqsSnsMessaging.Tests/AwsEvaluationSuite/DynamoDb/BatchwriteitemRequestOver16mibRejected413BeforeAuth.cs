// Translated from behavioral/dynamodb/check_batchwriteitem_request_over_16mib_rejected_413_before_auth.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batchwriteitem-request-over-16mib-rejected-413-before-auth</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchwriteitemRequestOver16mibRejected413BeforeAuth
{
    private const string Check = "batchwriteitem-request-over-16mib-rejected-413-before-auth";
    private const int LimitBytes = 16 * 1024 * 1024; // 16777216
    private const string Target = "DynamoDB_20120810.BatchWriteItem";
    private static readonly (string Type, string Message) MissingAuth = (
        "com.amazon.coral.service#MissingAuthenticationTokenException",
        "Request is missing Authentication Token");

    private static readonly (string Type, string Message) RequestItemsRequired = (
        "com.amazon.coral.validate#ValidationException",
        "The requestItems parameter is required for BatchWriteItem");

    /// <summary>
    /// An unsigned raw BatchWriteItem request one byte over 16 MiB is refused with an empty-bodied
    /// HTTP 413 before authentication, while the same request at a small size is refused for its
    /// missing authentication token, and signed is judged on its empty RequestItems.
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.DynamoDbAuthentication, "requests are not authenticated, so an unsigned request is judged on its body (ValidationException) instead of refused MissingAuthenticationTokenException")]
    [DisplayName("dynamodb.batchwriteitem-request-over-16mib-rejected-413-before-auth")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            BillingMode = BillingMode.PAY_PER_REQUEST,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        // ---- positive control: a normal, authenticated BatchWriteItem writes and reads back,
        //      so the raw-body legs below are compared against a table that is known to work.
        var controlKey = FidelityContext.UniqueName("sim2real-item-");
        await ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new() { [table] = [new WriteRequest { PutRequest = new PutRequest { Item = Item(("pk", S(controlKey))) } }] },
        });
        var controlItem = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(controlKey))), ConsistentRead = true });
        Ensure(Py.Equal(controlItem.Item, Item(("pk", S(controlKey)))), $"the control write did not read back: {Py.Repr(controlItem.Item)}");

        var http = ctx.RawHttpClient(LocalSqsSnsMessaging.Http.AwsServiceType.DynamoDb);

        var small = SizedEmptyBatchBody(1024);
        var overLimit = SizedEmptyBatchBody(LimitBytes + 1);

        // ---- positive controls at a small size: the size gate lets the body through, so the
        //      unsigned request is judged on authentication and the signed one on validation.
        var (status, _, _, body) = await Post(ctx, http, small, signed: false);
        Ensure(status == 400, $"a small unsigned body gave {Py.Repr(status)}, not 400: {Py.Repr(body)}");
        var parsed = Parse(body);
        Ensure(
            (parsed.Type, parsed.Message) == MissingAuth,
            $"a small unsigned body gave {Py.Repr(Encoding.UTF8.GetString(body))}, expected __type/message {Py.Repr(MissingAuth)}");

        (status, _, _, body) = await Post(ctx, http, small, signed: true);
        Ensure(status == 400, $"a small signed body gave {Py.Repr(status)}, not 400: {Py.Repr(body)}");
        parsed = Parse(body);
        Ensure(
            (parsed.Type, parsed.Message) == RequestItemsRequired,
            $"a small signed body gave {Py.Repr(Encoding.UTF8.GetString(body))}, expected __type/message {Py.Repr(RequestItemsRequired)}");

        // ---- one byte over 16 MiB, unsigned: refused with 413 instead of the missing-token 400
        //      above, so the size gate runs before authentication.
        (status, var reason, var headers, body) = await Post(ctx, http, overLimit, signed: false);
        Ensure(status == 413, $"a 16MiB+1-byte unsigned body gave {Py.Repr(status)}, not 413: {Py.Repr(body)}");
        Ensure(reason == "Request Entity Too Large", $"the 413's reason phrase was {Py.Repr(reason)}, not 'Request Entity Too Large'");
        Ensure(body.Length == 0, $"the 413 response carried a body: {Py.Repr(body)}");
        Ensure(
            headers.GetValueOrDefault("content-length") == "0",
            $"the 413 response Content-Length was {Py.Repr(headers.GetValueOrDefault("content-length"))}, not '0'");
        Ensure(
            headers.GetValueOrDefault("connection") == "close",
            $"the 413 response Connection header was {Py.Repr(headers.GetValueOrDefault("connection"))}, not 'close'");
        Ensure(
            !headers.ContainsKey("content-type"),
            $"the 413 response unexpectedly carried a Content-Type: {Py.Repr(headers.GetValueOrDefault("content-type"))}");

        // ---- effect-absent: every raw body above carried an empty RequestItems map, so the
        //      table still holds only the control item -- none of the three requests wrote anything.
        var scan = await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true });
        Ensure(
            Py.Equal(scan.Items, new[] { Item(("pk", S(controlKey))) }),
            $"the table holds more than the control item after the raw requests: {Py.Repr(scan.Items)}");
    }

    /// <summary>
    /// A syntactically valid JSON body carrying an empty RequestItems map plus a filler
    /// attribute, sized to EXACTLY total_bytes so any downstream rejection is about
    /// RequestItems, never about the padding.
    /// </summary>
    private static byte[] SizedEmptyBatchBody(int totalBytes)
    {
        // json.dumps({"RequestItems": {}, "pad": "a" * n})
        const string prefix = "{\"RequestItems\": {}, \"pad\": \"";
        const string suffix = "\"}";
        var padLen = totalBytes - prefix.Length - suffix.Length;
        return Encoding.UTF8.GetBytes(prefix + new string('a', padLen) + suffix);
    }

    /// <summary>
    /// POST body as a BatchWriteItem call, signed or left with no Authorization header at
    /// all; returns (status, reason phrase, lowercased headers, raw body bytes).
    /// </summary>
    private static async Task<(int Status, string? Reason, Dictionary<string, string> Headers, byte[] Body)> Post(
        FidelityContext ctx, HttpClient http, byte[] body, bool signed)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/");
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/x-amz-json-1.0");
        request.Headers.TryAddWithoutValidation("X-Amz-Target", Target);
        if (signed)
        {
            // The emulator doesn't verify signatures: any well-formed SigV4 header stands in for SigV4Auth.
            var now = FidelityTime.UtcNow;
            var date = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            request.Headers.TryAddWithoutValidation("X-Amz-Date", now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
            request.Headers.TryAddWithoutValidation(
                "Authorization",
                $"AWS4-HMAC-SHA256 Credential=test/{date}/{ctx.Region}/dynamodb/aws4_request, " +
                "SignedHeaders=content-type;host;x-amz-date;x-amz-target, Signature=" + new string('0', 64));
        }

        using var response = await http.SendAsync(request, ctx.CancellationToken);
        var data = await response.Content.ReadAsByteArrayAsync(ctx.CancellationToken);
        var headers = response.Headers.Concat(response.Content.Headers)
            .ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value), StringComparer.Ordinal);
        if (response.Headers.ConnectionClose == true && !headers.ContainsKey("connection"))
        {
            headers["connection"] = "close";
        }

        return ((int)response.StatusCode, response.ReasonPhrase, headers, data);
    }

    private static (string? Type, string? Message) Parse(byte[] body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        return (
            root.TryGetProperty("__type", out var t) ? t.GetString() : null,
            root.TryGetProperty("message", out var m) ? m.GetString() : null);
    }
}
