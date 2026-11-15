// Translated from behavioral/dynamodb/check_sigv4_query_string_presigned_list_tables_accepted.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.sigv4-query-string-presigned-list-tables-accepted</summary>
[Category("AwsEvaluationSuite")]
public sealed class Sigv4QueryStringPresignedListTablesAccepted
{
    private const string CheckId = "sigv4-query-string-presigned-list-tables-accepted";

    /// <summary>
    /// Build and send one ListTables request authenticated with query-string parameters only.
    /// Returns (status, headers, body, sentHeaders, rawBody). sentHeaders lets the
    /// caller confirm no Authorization header carries any part of the signature.
    /// </summary>
    private static async Task<(int Status, Dictionary<string, string> Headers, JsonElement Body, List<string> SentHeaders, byte[] Raw)> PresignedListTables(
        FidelityContext ctx, HttpClient http, string payload)
    {
        var data = Encoding.UTF8.GetBytes(payload);
        using var req = new HttpRequestMessage(HttpMethod.Post, http.BaseAddress);
        req.Content = new ByteArrayContent(data);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-amz-json-1.0");
        req.Headers.TryAddWithoutValidation("x-amz-target", "DynamoDB_20120810.ListTables");
        req.RequestUri = SigV4.Presign(req, ctx.Region, expires: 300);
        var sentHeaders = req.Headers.Concat(req.Content.Headers).Select(h => h.Key).ToList();
        using var resp = await http.SendAsync(req, ctx.CancellationToken);
        var raw = await resp.Content.ReadAsByteArrayAsync(ctx.CancellationToken);
        JsonElement body;
        if (raw.Length > 0)
        {
            using var doc = JsonDocument.Parse(raw);
            body = doc.RootElement.Clone();
        }
        else
        {
            body = JsonDocument.Parse("{}").RootElement.Clone();
        }

        var headers = resp.Headers.Concat(resp.Content.Headers)
            .ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value), StringComparer.Ordinal);
        return ((int)resp.StatusCode, headers, body, sentHeaders, raw);
    }

    /// <summary>Poll DescribeTable until the table is ACTIVE, bounded by the run's own budget.</summary>
    private static async Task AwaitTableActive(FidelityContext ctx, IAmazonDynamoDB ddb, string table)
    {
        var budget = TimeSpan.FromSeconds(Math.Max(1.0, Math.Min(300.0, ctx.TimeLeftSeconds - 5.0)));
        var clock = FidelityTime.StartNew();
        string? status = null;
        while (clock.Elapsed < budget)
        {
            status = (await ddb.DescribeTableAsync(table)).Table.TableStatus?.Value;
            if (status == "ACTIVE")
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1.0));
        }

        throw Fail($"table {Py.Repr(table)} never reached ACTIVE (last {Py.Repr(status)})");
    }

    private static List<string> TableNames(JsonElement body) =>
        body.TryGetProperty("TableNames", out var names) ? [.. names.EnumerateArray().Select(n => n.GetString()!)] : [];

    private static string? LastEvaluated(JsonElement body) =>
        body.TryGetProperty("LastEvaluatedTableName", out var token) ? token.GetString() : null;

    /// <summary>
    /// A ListTables request signed with SigV4 query-string parameters, carrying no Authorization
    /// header, is accepted and returns the real inventory, including a table created in this run.
    /// Reference: https://docs.aws.amazon.com/IAM/latest/UserGuide/create-signed-request.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.sigv4-query-string-presigned-list-tables-accepted")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-sigvq-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            BillingMode = BillingMode.PAY_PER_REQUEST,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await AwaitTableActive(ctx, ddb, table);

        var http = ctx.RawHttpClient(LocalSqsSnsMessaging.Http.AwsServiceType.DynamoDb);
        var (status, headers, body, sentHeaders, raw) = await PresignedListTables(ctx, http, "{}");
        Ensure(
            !sentHeaders.Any(k => string.Equals(k, "authorization", StringComparison.OrdinalIgnoreCase)),
            $"the presigned request still carried an Authorization header: {Py.Repr(sentHeaders)}");
        Ensure(status == 200, $"a query-string-signed ListTables gave {status}, not 200: {Py.Repr(body)}");
        var crc = headers.GetValueOrDefault("x-amz-crc32");
        Ensure(
            crc == Crc32(raw).ToString(CultureInfo.InvariantCulture),
            $"x-amz-crc32 {Py.Repr(crc)} is not the CRC32 of the 200 response body: {Py.Repr(headers)}");
        var names = TableNames(body);
        var token = LastEvaluated(body);
        while (!string.IsNullOrEmpty(token))
        {
            var (pageStatus, _, page, _, _) = await PresignedListTables(
                ctx, http, JsonSerializer.Serialize(new Dictionary<string, string> { ["ExclusiveStartTableName"] = token }));
            Ensure(pageStatus == 200, $"a paged query-string-signed ListTables gave {pageStatus}: {Py.Repr(page)}");
            names.AddRange(TableNames(page));
            token = LastEvaluated(page);
        }

        Ensure(
            names.Contains(table),
            $"the presigned ListTables inventory omits the table this run created: {Py.Repr(names.Where(n => n.Contains(table, StringComparison.Ordinal)).ToList())}");
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
