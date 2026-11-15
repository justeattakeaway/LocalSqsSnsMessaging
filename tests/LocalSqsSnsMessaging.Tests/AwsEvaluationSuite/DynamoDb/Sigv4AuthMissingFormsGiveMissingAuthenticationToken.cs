// Translated from behavioral/dynamodb/check_sigv4_auth_missing_forms_give_missing_authentication_token.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.sigv4-auth-missing-forms-give-missing-authentication-token</summary>
[Category("AwsEvaluationSuite")]
public sealed class Sigv4AuthMissingFormsGiveMissingAuthenticationToken
{
    private const string CheckId = "sigv4-auth-missing-forms-give-missing-authentication-token";
    private const string ErrType = "com.amazon.coral.service#MissingAuthenticationTokenException";
    private const string ErrMessage = "Request is missing Authentication Token";

    private static double BudgetS(FidelityContext ctx, double capS) => Math.Max(1.0, Math.Min(capS, ctx.TimeLeftSeconds - 5.0));

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

            Ensure(clock.Elapsed < budget, $"table {Py.Repr(table)} never reached ACTIVE (last {Py.Repr(status)})");
            await FidelityTime.Delay(TimeSpan.FromSeconds(1.0));
        }
    }

    /// <summary>
    /// A PutItem with no Authorization header, a truncated one, or SigV4 query parameters
    /// missing X-Amz-Algorithm is refused MissingAuthenticationTokenException and lands nothing.
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.DynamoDbAuthentication, "requests are not authenticated: a PutItem with no or a truncated Authorization header, or query auth without X-Amz-Algorithm, is not refused MissingAuthenticationTokenException")]
    [DisplayName("dynamodb.sigv4-auth-missing-forms-give-missing-authentication-token")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await AwaitTableActive(ctx, ddb, table);

        var http = ctx.RawHttpClient(LocalSqsSnsMessaging.Http.AwsServiceType.DynamoDb);
        var endpoint = http.BaseAddress!;

        // Send a hand-built PutItem; mode picks the auth shape under test.
        async Task<(int Status, Dictionary<string, string> Headers, byte[] Body)> RawPut(string pk, string mode)
        {
            var bodyDict = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["TableName"] = table,
                ["Item"] = new Dictionary<string, object> { ["pk"] = new Dictionary<string, string> { ["S"] = pk } },
            };
            var data = JsonSerializer.SerializeToUtf8Bytes(bodyDict);
            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
            req.Content = new ByteArrayContent(data);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-amz-json-1.0");
            req.Headers.TryAddWithoutValidation("x-amz-target", "DynamoDB_20120810.PutItem");
            switch (mode)
            {
                case "signed":
                    SigV4.SignHeaders(req, data, ctx.Region);
                    break;
                case "no_auth_header":
                    SigV4.SignHeaders(req, data, ctx.Region);
                    req.Headers.Remove("Authorization");
                    break;
                case "truncated_auth_header":
                    SigV4.SignHeaders(req, data, ctx.Region);
                    req.Headers.Remove("Authorization");
                    req.Headers.TryAddWithoutValidation("Authorization", "AWS4");
                    break;
                case "query_auth_missing_algorithm":
                {
                    // SigV4QueryAuth moves the JSON body's parameters into the query string too.
                    var presigned = SigV4.Presign(req, ctx.Region, expires: 3600,
                    [
                        new("TableName", table),
                        new("Item", $"{{'pk': {{'S': '{pk}'}}}}"),
                    ]);
                    var kept = presigned.Query.TrimStart('?').Split('&')
                        .Where(p => !p.StartsWith("X-Amz-Algorithm=", StringComparison.Ordinal));
                    req.RequestUri = new UriBuilder(presigned) { Query = string.Join("&", kept) }.Uri;
                    break;
                }

                default:
                    throw Fail($"unknown mode {Py.Repr(mode)}");
            }

            using var resp = await http.SendAsync(req, ctx.CancellationToken);
            var raw = await resp.Content.ReadAsByteArrayAsync(ctx.CancellationToken);
            var headers = resp.Headers.Concat(resp.Content.Headers)
                .ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value), StringComparer.Ordinal);
            return ((int)resp.StatusCode, headers, raw);
        }

        async Task<Dictionary<string, AttributeValue>?> Landed(string pk) =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;

        // ---- positive control: a correctly signed raw PutItem over the same socket path lands.
        var (status, _, rawBody) = await RawPut("control-before", "signed");
        Ensure(status == 200, $"a correctly signed raw PutItem gave {status}: {Py.Repr(rawBody)}");
        Ensure(Py.Equal(await Landed("control-before"), Item(("pk", S("control-before")))), "the correctly signed control PutItem did not land");

        (string Mode, string Pk)[] modes =
        [
            ("no_auth_header", "no-auth-header"),
            ("truncated_auth_header", "truncated-auth-header"),
            ("query_auth_missing_algorithm", "query-auth-missing-algorithm"),
        ];
        foreach (var (mode, pk) in modes)
        {
            var (modeStatus, headers, body) = await RawPut(pk, mode);
            var label = mode;
            Ensure(modeStatus == 400, $"{label}: expected status 400, got {modeStatus}: {Py.Repr(body)}");
            using var parsed = JsonDocument.Parse(body);
            var type = parsed.RootElement.TryGetProperty("__type", out var t) ? t.GetString() : null;
            Ensure(type == ErrType, $"{label}: __type was {Py.Repr(type)}, expected {Py.Repr(ErrType)}");
            var message = parsed.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
            Ensure(message == ErrMessage, $"{label}: message was {Py.Repr(message)}, expected {Py.Repr(ErrMessage)}");
            var crc = headers.GetValueOrDefault("x-amz-crc32");
            Ensure(!string.IsNullOrEmpty(crc), $"{label}: no x-amz-crc32 header on the error response: {Py.Repr(headers)}");
            Ensure(
                uint.Parse(crc, CultureInfo.InvariantCulture) == Crc32(body),
                $"{label}: x-amz-crc32 {Py.Repr(crc)} does not match the body's own checksum");
            Ensure(await Landed(pk) is null, $"{label}: a refused PutItem created {Py.Repr(pk)} anyway");
        }

        // ---- positive control: the table and the raw channel are still healthy afterwards.
        (status, _, rawBody) = await RawPut("control-after", "signed");
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
