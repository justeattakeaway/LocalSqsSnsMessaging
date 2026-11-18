// Translated from behavioral/dynamodb/check_incomplete_sigv4_signature_lists_missing_components.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.incomplete-sigv4-signature-lists-missing-components</summary>
[Category("AwsEvaluationSuite")]
public sealed class IncompleteSigv4SignatureListsMissingComponents
{
    private const string CheckId = "incomplete-sigv4-signature-lists-missing-components";
    private const string IncompleteSignature = "com.amazon.coral.service#IncompleteSignatureException";

    /// <summary>
    /// A poll's timeout: capSeconds, bounded by the runner's remaining time less 5 s so the poll fails
    /// with its own message.
    /// </summary>
    private static double BudgetSeconds(FidelityContext ctx, double capSeconds) => Math.Max(1.0, Math.Min(capSeconds, ctx.TimeLeftSeconds - 5.0));

    /// <summary>True when the runner's remaining time, not the wait's own deadline, ended a capped wait.</summary>
    private static bool BudgetSpent(FidelityContext ctx) => ctx.TimeLeftSeconds <= 6.0;

    /// <summary>Poll DescribeTable every second until the table is ACTIVE, bounded by the budget.</summary>
    private static async Task AwaitTableActive(FidelityContext ctx, IAmazonDynamoDB ddb, string table)
    {
        var clock = FidelityTime.StartNew();
        var deadline = BudgetSeconds(ctx, 300.0);
        string? status = null;
        while (true)
        {
            try
            {
                status = (await ddb.DescribeTableAsync(table)).Table.TableStatus.Value;
            }
            catch (ResourceNotFoundException)
            {
            }

            if (status == "ACTIVE")
            {
                return;
            }

            if (clock.Elapsed.TotalSeconds >= deadline && BudgetSpent(ctx))
            {
                throw new TimeoutException($"table {Py.Repr(table)} ACTIVE: the run budget ended first; not graded");
            }

            Ensure(clock.Elapsed.TotalSeconds < deadline, $"table {Py.Repr(table)} never reached ACTIVE (last {Py.Repr(status)})");
            await FidelityTime.Delay(TimeSpan.FromSeconds(1.0));
        }
    }

    /// <summary>
    /// Failure-path teardown for a resource the success path deletes itself: only an
    /// already-absent resource is tolerated, any other failure still surfaces.
    /// </summary>
    private static async Task AbsentOk(Func<Task> fn)
    {
        try
        {
            await fn();
        }
        catch (ResourceNotFoundException)
        {
        }
    }

    /// <summary>Every table name in this account and region, paginated.</summary>
    private static async Task<List<string>> ListTableNames(IAmazonDynamoDB ddb)
    {
        var names = new List<string>();
        string? start = null;
        do
        {
            var page = await ddb.ListTablesAsync(new ListTablesRequest { ExclusiveStartTableName = start });
            names.AddRange(page.TableNames ?? []);
            start = page.LastEvaluatedTableName;
        }
        while (start is not null);
        return names;
    }

    /// <summary>The SHA-256/base64 digest DynamoDB echoes back for an unparseable Authorization value.</summary>
    private static string HashedAuthTrailer(string authValue) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(authValue)));

    /// <summary>POST raw bytes to the DynamoDB endpoint; return (status, parsed awsJson body).</summary>
    private static async Task<(int Status, JsonElement Body)> Send(HttpClient pool, Dictionary<string, string> headers, string body, string query = "")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, query.Length > 0 ? "/?" + query : "/");
        request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        foreach (var (name, value) in headers)
        {
            if (!request.Headers.TryAddWithoutValidation(name, value))
            {
                request.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }

        using var response = await pool.SendAsync(request);
        var data = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(data.Length > 0 ? data : "{}");
        return ((int)response.StatusCode, document.RootElement.Clone());
    }

    private static string? Get(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value) ? value.ToString() : null;

    /// <summary>
    /// An Authorization header with no SigV4 parameters, or with exactly one still missing, is
    /// refused with IncompleteSignatureException naming each missing component; an incomplete
    /// query-string signature is refused the same way, naming each missing query parameter.
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.DynamoDbAuthentication, "Signatures are not checked, so an incomplete SigV4 Authorization header or query-string signature is served instead of refused with IncompleteSignatureException")]
    [DisplayName("dynamodb.incomplete-sigv4-signature-lists-missing-components")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var pool = ctx.RawHttpClient(InMemoryAws.Http.AwsServiceType.DynamoDb);

        var baseName = FidelityContext.UniqueName("sim2real-behav-sigv4-");
        var table = baseName + "-t";
        var probeTable = baseName + "-create-probe";

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

        // ---- 1. a header-form Authorization with no SigV4 parameters at all and no X-Amz-Date or
        //         Date header either: every missing component is named, in order, and the trailer
        //         echoes the hashed literal value this check sent -- not a signature DynamoDB
        //         could not have computed, since nothing here was ever signed.
        ctx.OnCleanup(() => AbsentOk(() => ddb.DeleteTableAsync(probeTable)));
        const string authBare = "AWS4-";
        var createBody = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["TableName"] = probeTable,
            ["BillingMode"] = "PAY_PER_REQUEST",
            ["KeySchema"] = new[] { new Dictionary<string, string> { ["AttributeName"] = "pk", ["KeyType"] = "HASH" } },
            ["AttributeDefinitions"] = new[] { new Dictionary<string, string> { ["AttributeName"] = "pk", ["AttributeType"] = "S" } },
        });
        var (status, body) = await Send(pool, body: createBody, headers: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["content-type"] = "application/x-amz-json-1.0",
            ["x-amz-target"] = "DynamoDB_20120810.CreateTable",
            ["Authorization"] = authBare,
        });
        var expectedBare =
            "Authorization header requires 'Credential' parameter. " +
            "Authorization header requires 'Signature' parameter. " +
            "Authorization header requires 'SignedHeaders' parameter. " +
            "Authorization header requires existence of either a 'X-Amz-Date' or a 'Date' header. " +
            $"(Hashed with SHA-256 and encoded with Base64) Authorization={HashedAuthTrailer(authBare)}";
        Ensure(status == 400, $"a bare 'AWS4-' Authorization header gave status {Py.Repr(status)}: {Py.Repr(body)}");
        Ensure(
            Get(body, "__type") == IncompleteSignature,
            $"a bare 'AWS4-' Authorization header gave __type {Py.Repr(Get(body, "__type"))}, expected {Py.Repr(IncompleteSignature)}");
        Ensure(Get(body, "message") == expectedBare, $"message was {Py.Repr(Get(body, "message"))}, expected {Py.Repr(expectedBare)}");

        // effect-absent: the refused CreateTable created nothing.
        var names = await ListTableNames(ddb);
        Ensure(!names.Contains(probeTable), $"a CreateTable refused for an incomplete signature created {Py.Repr(probeTable)} anyway");

        // ---- 2. a header-form Authorization missing only SignedHeaders, with a Date header
        //         present: only the one still-missing component is named.
        const string authPartial = "AWS4- Signature=b, Credential=a";
        var describeBody = JsonSerializer.Serialize(new Dictionary<string, string> { ["TableName"] = table });
        (status, body) = await Send(pool, body: describeBody, headers: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["content-type"] = "application/x-amz-json-1.0",
            ["x-amz-target"] = "DynamoDB_20120810.DescribeTable",
            ["Authorization"] = authPartial,
            ["date"] = FidelityTime.UtcNow.ToString("r", CultureInfo.InvariantCulture),
        });
        var expectedPartial =
            "Authorization header requires 'SignedHeaders' parameter. " +
            $"(Hashed with SHA-256 and encoded with Base64) Authorization={HashedAuthTrailer(authPartial)}";
        Ensure(status == 400, $"an Authorization header missing only SignedHeaders gave status {Py.Repr(status)}: {Py.Repr(body)}");
        Ensure(
            Get(body, "__type") == IncompleteSignature,
            $"an Authorization header missing only SignedHeaders gave __type {Py.Repr(Get(body, "__type"))}, expected {Py.Repr(IncompleteSignature)}");
        Ensure(Get(body, "message") == expectedPartial, $"message was {Py.Repr(Get(body, "message"))}, expected {Py.Repr(expectedPartial)}");

        // ---- 3. a query-string-form signature with an empty X-Amz-Algorithm and no other SigV4
        //         query parameter: every missing query-string parameter is named, ending in the
        //         re-examine sentence, and there is no Authorization header to name instead.
        (status, body) = await Send(pool, body: describeBody, query: "X-Amz-Algorithm=", headers: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["content-type"] = "application/x-amz-json-1.0",
            ["x-amz-target"] = "DynamoDB_20120810.DescribeTable",
        });
        const string expectedQuery =
            "AWS query-string parameters must include 'X-Amz-Algorithm'. " +
            "AWS query-string parameters must include 'X-Amz-Credential'. " +
            "AWS query-string parameters must include 'X-Amz-Signature'. " +
            "AWS query-string parameters must include 'X-Amz-SignedHeaders'. " +
            "AWS query-string parameters must include 'X-Amz-Date'. " +
            "Re-examine the query-string parameters.";
        Ensure(status == 400, $"an empty X-Amz-Algorithm query parameter gave status {Py.Repr(status)}: {Py.Repr(body)}");
        Ensure(
            Get(body, "__type") == IncompleteSignature,
            $"an empty X-Amz-Algorithm query parameter gave __type {Py.Repr(Get(body, "__type"))}, expected {Py.Repr(IncompleteSignature)}");
        Ensure(Get(body, "message") == expectedQuery, $"message was {Py.Repr(Get(body, "message"))}, expected {Py.Repr(expectedQuery)}");

        // ---- 4. positive control: the same operation, properly signed through the SDK, still
        //         succeeds against the same table none of the refusals above touched.
        var control = (await ddb.DescribeTableAsync(table)).Table;
        Ensure(control.TableStatus == TableStatus.ACTIVE, $"a correctly signed DescribeTable did not see the table ACTIVE: {Py.Repr(control.TableStatus)}");

        var namesAfter = await ListTableNames(ddb);
        Ensure(namesAfter.Contains(table), $"the control table is missing from ListTables: {Py.Repr(namesAfter)}");
        Ensure(!namesAfter.Contains(probeTable), "the refused CreateTable's table appeared in ListTables by the end of the run");
    }
}
