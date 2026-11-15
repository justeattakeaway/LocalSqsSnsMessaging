// Translated from behavioral/dynamodb/check_sigv4_query_string_presigned_list_tables_tampered_signature_rejected.py
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

/// <summary>dynamodb.sigv4-query-string-presigned-list-tables-tampered-signature-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class Sigv4QueryStringPresignedListTablesTamperedSignatureRejected
{
    private const string CheckId = "sigv4-query-string-presigned-list-tables-tampered-signature-rejected";

    // Observed, named pair: DynamoDB's exact code and message for an invalid SigV4 signature.
    private static readonly (string Code, string Message) InvalidSignature = (
        "InvalidSignatureException",
        "The request signature we calculated does not match the signature you provided. "
        + "Check your AWS Secret Access Key and signing method. Consult the service documentation "
        + "for details.");

    private const string Namespace = "com.amazon.coral.service";

    /// <summary>A valid, query-string-signed ListTables request: its URL, body and headers.</summary>
    private static (Uri Url, byte[] Data, List<KeyValuePair<string, string>> Headers) PresignedUrl(FidelityContext ctx, Uri endpoint, string payload)
    {
        var data = Encoding.UTF8.GetBytes(payload);
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
        req.Content = new ByteArrayContent(data);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-amz-json-1.0");
        req.Headers.TryAddWithoutValidation("x-amz-target", "DynamoDB_20120810.ListTables");
        var url = SigV4.Presign(req, ctx.Region, expires: 300);
        var headers = req.Headers.Concat(req.Content.Headers)
            .Select(h => new KeyValuePair<string, string>(h.Key, string.Join(",", h.Value)))
            .ToList();
        return (url, data, headers);
    }

    private static async Task<(int Status, JsonElement Body)> Send(HttpClient http, Uri url, byte[] data, List<KeyValuePair<string, string>> headers, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(data) };
        foreach (var (name, value) in headers)
        {
            if (!req.Headers.TryAddWithoutValidation(name, value))
            {
                req.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }

        using var resp = await http.SendAsync(req, cancellationToken);
        var raw = await resp.Content.ReadAsByteArrayAsync(cancellationToken);
        using var doc = JsonDocument.Parse(raw.Length > 0 ? raw : "{}"u8.ToArray());
        return ((int)resp.StatusCode, doc.RootElement.Clone());
    }

    /// <summary>One freshly-signed, query-string-authenticated ListTables page, over its own connection.</summary>
    private static async Task<JsonElement> PresignedPage(FidelityContext ctx, HttpClient http, string payload)
    {
        var (url, data, headers) = PresignedUrl(ctx, http.BaseAddress!, payload);
        var (status, body) = await Send(http, url, data, headers, ctx.CancellationToken);
        Ensure(status == 200, $"a query-string-signed ListTables page gave {status}: {Py.Repr(body)}");
        return body;
    }

    /// <summary>The same presigned URL with the trailing hex digits of X-Amz-Signature flipped.</summary>
    private static Uri WithTamperedSignature(Uri url)
    {
        var text = url.AbsoluteUri;
        Ensure("abcdef0123456789".Contains(text[^1], StringComparison.Ordinal), $"presigned URL does not end in the signature's hex digits: {Py.Repr(text)}");
        return new Uri(text[..^4] + (text[^4..] != "0000" ? "0000" : "1111"));
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

    private static string? Str(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value) ? value.GetString() : null;

    private static List<string> TableNames(JsonElement body) =>
        body.TryGetProperty("TableNames", out var names) ? [.. names.EnumerateArray().Select(n => n.GetString()!)] : [];

    /// <summary>
    /// A ListTables request presigned with SigV4 query-string parameters and no Authorization
    /// header is rejected with InvalidSignatureException when its X-Amz-Signature is altered; the
    /// same request with an unaltered signature succeeds.
    /// Reference: https://docs.aws.amazon.com/IAM/latest/UserGuide/create-signed-request.html
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.DynamoDbAuthentication, "signatures are not verified: a tampered query-string X-Amz-Signature is accepted (200) instead of InvalidSignatureException")]
    [DisplayName("dynamodb.sigv4-query-string-presigned-list-tables-tampered-signature-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-sigvqbad-");
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
        // Each send below opens its own connection (a fresh pool per request): a connection that has
        // carried one rejected signature keeps rejecting every later request sent over it, correctly
        // signed or not, so reusing a connection across these assertions would make the positive
        // control fail for a reason that has nothing to do with the signature it carries.
        var (badUrl, badData, badHeaders) = PresignedUrl(ctx, http.BaseAddress!, "{}");
        Ensure(
            !badHeaders.Any(h => string.Equals(h.Key, "authorization", StringComparison.OrdinalIgnoreCase)),
            $"the presigned request still carried an Authorization header: {Py.Repr(badHeaders.ToDictionary(h => h.Key, h => h.Value))}");
        var tamperedUrl = WithTamperedSignature(badUrl);
        Ensure(tamperedUrl != badUrl, "tampering produced the same URL; the probe asserts nothing");
        var (status, body) = await Send(http, tamperedUrl, badData, badHeaders, ctx.CancellationToken);
        Ensure(status == 400, $"a tampered query-string signature gave {status}: {Py.Repr(body)}");
        var (code, message) = InvalidSignature;
        Ensure(
            Str(body, "__type") == $"{Namespace}#{code}",
            $"a tampered signature gave __type {Py.Repr(Str(body, "__type"))}, expected {Py.Repr(Namespace + "#" + code)}");
        Ensure(
            Str(body, "message") == message,
            $"a tampered signature's message was {Py.Repr(Str(body, "message"))}, expected {Py.Repr(message)}");

        // ---- positive control: an untampered presigned request, over its own connection, succeeds
        //      and lists the table this check created, so the rejection above is attributable to the
        //      tampered signature rather than to the endpoint or the account.
        var (goodUrl, goodData, goodHeaders) = PresignedUrl(ctx, http.BaseAddress!, "{}");
        var (goodStatus, goodBody) = await Send(http, goodUrl, goodData, goodHeaders, ctx.CancellationToken);
        Ensure(
            goodStatus == 200,
            $"the untampered presigned ListTables this probe tampered gave {goodStatus}, not 200: {Py.Repr(goodBody)}");
        var names = TableNames(goodBody);
        var token = Str(goodBody, "LastEvaluatedTableName");
        while (!string.IsNullOrEmpty(token))
        {
            var page = await PresignedPage(ctx, http, JsonSerializer.Serialize(new Dictionary<string, string> { ["ExclusiveStartTableName"] = token }));
            names.AddRange(TableNames(page));
            token = Str(page, "LastEvaluatedTableName");
        }

        Ensure(
            names.Contains(table),
            $"the positive control's presigned ListTables omits the table this check created: {Py.Repr(names.Where(n => n.Contains(table, StringComparison.Ordinal)).ToList())}");
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
