// Translated from behavioral/dynamodb/check_sigv4_non_aws4_header_and_query_missing_signedheaders_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.sigv4-non-aws4-header-and-query-missing-signedheaders-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class Sigv4NonAws4HeaderAndQueryMissingSignedheadersRejected
{
    private const string CheckId = "sigv4-non-aws4-header-and-query-missing-signedheaders-rejected";

    private static readonly (string Code, string Message) MissingToken = (
        "com.amazon.coral.service#MissingAuthenticationTokenException",
        "Request is missing Authentication Token");

    private static readonly (string Code, string Message) IncompleteSignedHeaders = (
        "com.amazon.coral.service#IncompleteSignatureException",
        "AWS query-string parameters must include 'X-Amz-SignedHeaders'. " +
        "Re-examine the query-string parameters.");

    /// <summary>
    /// A poll's timeout: capS, bounded by the runner's remaining time less 5 s so the poll fails
    /// with its own message.
    /// </summary>
    private static double BudgetS(FidelityContext ctx, double capS) => Math.Max(1.0, Math.Min(capS, ctx.TimeLeftSeconds - 5.0));

    /// <summary>True when the runner's remaining time, not the wait's own deadline, ended a capped wait.</summary>
    private static bool BudgetSpent(FidelityContext ctx) => ctx.TimeLeftSeconds <= 6.0;

    /// <summary>Poll DescribeTable every second until the table is ACTIVE, bounded by the budget.</summary>
    private static async Task AwaitTableActive(FidelityContext ctx, IAmazonDynamoDB ddb, string table)
    {
        var clock = FidelityTime.StartNew();
        var budget = TimeSpan.FromSeconds(BudgetS(ctx, 300.0));
        string? status = null;
        while (true)
        {
            var (response, error) = await FidelityContext.Capture(() => ddb.DescribeTableAsync(table));
            if (error is not null && error.Code != "ResourceNotFoundException")
            {
                throw error.Exception;
            }

            if (response is not null)
            {
                status = response.Table.TableStatus?.Value;
            }

            if (status == "ACTIVE")
            {
                return;
            }

            if (clock.Elapsed >= budget && BudgetSpent(ctx))
            {
                throw new TimeoutException($"table {Py.Repr(table)} ACTIVE: the run budget ended first; not graded");
            }

            Ensure(clock.Elapsed < budget, $"table {Py.Repr(table)} never reached ACTIVE (last {Py.Repr(status)})");
            await FidelityTime.Delay(TimeSpan.FromSeconds(1.0));
        }
    }

    /// <summary>
    /// Failure-path teardown for a resource the success path deletes itself: only an
    /// already-absent resource is tolerated, any other failure still surfaces.
    /// </summary>
    private static async Task AbsentOk(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is not null && error.Code != "ResourceNotFoundException")
        {
            throw error.Exception;
        }
    }

    private static void ExpectError(int status, JsonElement body, byte[] rawBody, Dictionary<string, string> headers, (string Code, string Message) x)
    {
        var (code, message) = x;
        Ensure(status == 400, $"expected status 400, got {status}: {Py.Repr(body)}");
        Ensure(Str(body, "__type") == code, $"__type was {Py.Repr(Str(body, "__type"))}, expected {Py.Repr(code)}: {Py.Repr(body)}");
        Ensure(Str(body, "message") == message, $"message was {Py.Repr(Str(body, "message"))}, expected {Py.Repr(message)}");
        var crc = headers.GetValueOrDefault("x-amz-crc32");
        Ensure(!string.IsNullOrEmpty(crc), $"no x-amz-crc32 header on the error response: {Py.Repr(headers)}");
        Ensure(
            uint.Parse(crc, CultureInfo.InvariantCulture) == Crc32(rawBody),
            $"x-amz-crc32 {Py.Repr(crc)} does not match the body's own checksum");
    }

    private static string? Str(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>
    /// A non-'AWS4-' Authorization header is refused MissingAuthenticationToken; a complete
    /// query-string SigV4 signature missing only X-Amz-SignedHeaders is refused
    /// IncompleteSignatureException naming that one parameter. Neither lands an item.
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.DynamoDbAuthentication, "requests are not authenticated: a non-AWS4 Authorization header, or query auth without X-Amz-SignedHeaders, is served instead of refused")]
    [DisplayName("dynamodb.sigv4-non-aws4-header-and-query-missing-signedheaders-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-sigv4np-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            BillingMode = BillingMode.PAY_PER_REQUEST,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => AbsentOk(() => ddb.DeleteTableAsync(table)));
        await AwaitTableActive(ctx, ddb, table);

        var http = ctx.RawHttpClient(InMemoryAws.Http.AwsServiceType.DynamoDb);
        var endpoint = http.BaseAddress!;

        async Task<Dictionary<string, AttributeValue>?> Landed(string pk) =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;

        byte[] Body(string pk) => JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["TableName"] = table,
            ["Item"] = new Dictionary<string, object> { ["pk"] = new Dictionary<string, string> { ["S"] = pk } },
        });

        HttpRequestMessage NewRequest(Uri url, byte[] data)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(data) };
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-amz-json-1.0");
            req.Headers.TryAddWithoutValidation("x-amz-target", "DynamoDB_20120810.PutItem");
            return req;
        }

        async Task<(int Status, Dictionary<string, string> Headers, byte[] Body)> Send(HttpRequestMessage req)
        {
            using var resp = await http.SendAsync(req, ctx.CancellationToken);
            var raw = await resp.Content.ReadAsByteArrayAsync(ctx.CancellationToken);
            var headers = resp.Headers.Concat(resp.Content.Headers)
                .ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value), StringComparer.Ordinal);
            return ((int)resp.StatusCode, headers, raw);
        }

        static JsonElement Parse(byte[] raw)
        {
            using var doc = JsonDocument.Parse(raw.Length > 0 ? raw : "{}"u8.ToArray());
            return doc.RootElement.Clone();
        }

        // A raw signed PutItem over the same socket path; returns (status, headers, raw body).
        async Task<(int Status, Dictionary<string, string> Headers, byte[] Body)> RawPut(string pk)
        {
            var data = Body(pk);
            using var req = NewRequest(endpoint, data);
            SigV4.SignHeaders(req, data, ctx.Region);
            return await Send(req);
        }

        // ---- positive control: a correctly signed raw PutItem lands.
        var (status, _, rawBody) = await RawPut("control-before");
        Ensure(status == 200, $"a correctly signed raw PutItem gave {status}: {Py.Repr(rawBody)}");
        Ensure(Py.Equal(await Landed("control-before"), Item(("pk", S("control-before")))), "the correctly signed control PutItem did not land");

        // ---- 1. an Authorization header that does not start with 'AWS4-' at all, with no
        //         SigV4 query parameters either: DynamoDB treats it as no authentication attempt.
        const string pk1 = "non-aws4-header";
        var data1 = Body(pk1);
        using (var req1 = NewRequest(endpoint, data1))
        {
            req1.Headers.TryAddWithoutValidation("Authorization", "X");
            var (status1, headers1, raw1) = await Send(req1);
            ExpectError(status1, Parse(raw1), raw1, headers1, MissingToken);
        }

        Ensure(await Landed(pk1) is null, $"a non-AWS4 Authorization header PutItem created {Py.Repr(pk1)} anyway");

        // ---- 2. a query-string SigV4 signature with every parameter present except
        //         X-Amz-SignedHeaders: only that one parameter is named missing, not the full list.
        const string pk2 = "query-missing-signedheaders";
        var data2 = Body(pk2);
        using (var req2 = NewRequest(endpoint, data2))
        {
            // SigV4QueryAuth moves the JSON body's parameters into the query string too.
            var presigned = SigV4.Presign(req2, ctx.Region, expires: 3600,
            [
                new("TableName", table),
                new("Item", $"{{'pk': {{'S': '{pk2}'}}}}"),
            ]);
            var allParams2 = presigned.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).Select(p => (Key: p[0], Value: p.Length > 1 ? p[1] : "")).ToList();
            Ensure(
                allParams2.Any(p => p.Key == "X-Amz-SignedHeaders"),
                $"the complete query-string signature never carried X-Amz-SignedHeaders to remove: {Py.Repr(allParams2)}");
            var kept2 = allParams2.Where(p => p.Key != "X-Amz-SignedHeaders").ToList();
            Ensure(
                kept2.Select(p => p.Key).ToHashSet().SetEquals([
                    "TableName", "Item", "X-Amz-Algorithm", "X-Amz-Credential", "X-Amz-Date",
                    "X-Amz-Expires", "X-Amz-Signature"]),
                $"unexpected query parameters after removing only X-Amz-SignedHeaders: {Py.Repr(kept2)}");
            req2.RequestUri = new UriBuilder(presigned) { Query = string.Join("&", kept2.Select(p => p.Key + "=" + p.Value)) }.Uri;
            var (status2, headers2, raw2) = await Send(req2);
            ExpectError(status2, Parse(raw2), raw2, headers2, IncompleteSignedHeaders);
        }

        Ensure(await Landed(pk2) is null, $"a query-string signature missing only X-Amz-SignedHeaders created {Py.Repr(pk2)} anyway");

        // ---- positive control: the table and the raw channel are still healthy afterwards.
        (status, _, rawBody) = await RawPut("control-after");
        Ensure(status == 200, $"a correctly signed raw PutItem after the refusals gave {status}: {Py.Repr(rawBody)}");
        Ensure(Py.Equal(await Landed("control-after"), Item(("pk", S("control-after")))), "the trailing control PutItem did not land");
    }

    /// <summary><c>binascii.crc32(data) &amp; 0xFFFFFFFF</c>.</summary>
    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }

    /// <summary>
    /// SigV4 signing as botocore's <c>SigV4Auth</c> (Authorization header) and <c>SigV4QueryAuth</c>
    /// (query-string parameters) do it, with the runner's client credentials.
    /// </summary>
    private static class SigV4
    {
        private const string AccessKey = "test";
        private const string SecretKey = "test";
        private const string Service = "dynamodb";

        /// <summary><c>SigV4Auth.add_auth</c>: adds X-Amz-Date and an Authorization header signing every header.</summary>
        public static void SignHeaders(HttpRequestMessage request, byte[] body, string region)
        {
            var now = FidelityTime.UtcNow;
            var amzDate = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            request.Headers.TryAddWithoutValidation("X-Amz-Date", amzDate);
            var headers = HeadersToSign(request);
            var signedHeaders = string.Join(";", headers.Keys);
            var signature = Signature(request.Method.Method, request.RequestUri!, CanonicalQuery(request.RequestUri!), headers, Sha256Hex(body), amzDate, region);
            request.Headers.TryAddWithoutValidation(
                "Authorization",
                $"AWS4-HMAC-SHA256 Credential={AccessKey}/{Scope(amzDate, region)}, SignedHeaders={signedHeaders}, Signature={signature}");
        }

        /// <summary>
        /// <c>SigV4QueryAuth.add_auth</c>: the URL with X-Amz-Algorithm, X-Amz-Credential, X-Amz-Date,
        /// X-Amz-Expires and X-Amz-SignedHeaders (after <paramref name="extraQuery"/>) and the X-Amz-Signature
        /// over them, with an unsigned payload.
        /// </summary>
        public static Uri Presign(HttpRequestMessage request, string region, int expires, IEnumerable<KeyValuePair<string, string>>? extraQuery = null)
        {
            var now = FidelityTime.UtcNow;
            var amzDate = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            var headers = HeadersToSign(request);
            var signedHeaders = string.Join(";", headers.Keys);
            var query = new List<KeyValuePair<string, string>>(extraQuery ?? []);
            query.Add(new("X-Amz-Algorithm", "AWS4-HMAC-SHA256"));
            query.Add(new("X-Amz-Credential", $"{AccessKey}/{Scope(amzDate, region)}"));
            query.Add(new("X-Amz-Date", amzDate));
            query.Add(new("X-Amz-Expires", expires.ToString(CultureInfo.InvariantCulture)));
            query.Add(new("X-Amz-SignedHeaders", signedHeaders));
            var queryString = string.Join("&", query.Select(p => Encode(p.Key) + "=" + Encode(p.Value)));
            var unsigned = new UriBuilder(request.RequestUri!) { Query = queryString }.Uri;
            var signature = Signature(request.Method.Method, unsigned, CanonicalQuery(unsigned), headers, "UNSIGNED-PAYLOAD", amzDate, region);
            return new UriBuilder(unsigned) { Query = queryString + "&X-Amz-Signature=" + signature }.Uri;
        }

        private static string Scope(string amzDate, string region) => $"{amzDate[..8]}/{region}/{Service}/aws4_request";

        private static SortedDictionary<string, string> HeadersToSign(HttpRequestMessage request)
        {
            var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["host"] = request.RequestUri!.Host,
            };
            foreach (var header in request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>()))
            {
                var name = header.Key.ToLowerInvariant();
                if (name is "authorization" or "user-agent" or "expect" or "content-length")
                {
                    continue;
                }

                headers[name] = string.Join(",", header.Value).Trim();
            }

            return headers;
        }

        private static string CanonicalQuery(Uri uri) =>
            string.Join("&", uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2))
                .Select(p => (Key: p[0], Value: p.Length > 1 ? p[1] : ""))
                .OrderBy(p => p.Key, StringComparer.Ordinal)
                .ThenBy(p => p.Value, StringComparer.Ordinal)
                .Select(p => p.Key + "=" + p.Value));

        private static string Signature(string method, Uri uri, string canonicalQuery, SortedDictionary<string, string> headers, string payloadHash, string amzDate, string region)
        {
            var canonicalRequest = string.Join(
                "\n",
                method,
                uri.AbsolutePath,
                canonicalQuery,
                string.Concat(headers.Select(h => h.Key + ":" + h.Value + "\n")),
                string.Join(";", headers.Keys),
                payloadHash);
            var stringToSign = string.Join(
                "\n",
                "AWS4-HMAC-SHA256",
                amzDate,
                Scope(amzDate, region),
                Sha256Hex(Encoding.UTF8.GetBytes(canonicalRequest)));
            var key = Hmac(Encoding.UTF8.GetBytes("AWS4" + SecretKey), amzDate[..8]);
            key = Hmac(key, region);
            key = Hmac(key, Service);
            key = Hmac(key, "aws4_request");
            return Convert.ToHexStringLower(Hmac(key, stringToSign));
        }

        private static string Encode(string value) => Uri.EscapeDataString(value);

        private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

        private static string Sha256Hex(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
    }
}
