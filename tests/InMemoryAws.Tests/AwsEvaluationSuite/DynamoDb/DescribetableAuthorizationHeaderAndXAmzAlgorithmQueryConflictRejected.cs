// Translated from behavioral/dynamodb/check_describetable_authorization_header_and_x_amz_algorithm_query_conflict_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.describetable-authorization-header-and-x-amz-algorithm-query-conflict-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DescribetableAuthorizationHeaderAndXAmzAlgorithmQueryConflictRejected
{
    private const string CheckId = "describetable-authorization-header-and-x-amz-algorithm-query-conflict-rejected";
    private const string Rnf = "ResourceNotFoundException";
    private const string Ise = "InvalidSignatureException";

    /// <summary>
    /// A signed Authorization header together with an X-Amz-Algorithm query parameter is
    /// rejected with InvalidSignatureException and no message, before the table name is looked up.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.describetable-authorization-header-and-x-amz-algorithm-query-conflict-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var http = ctx.RawHttpClient(InMemoryAws.Http.AwsServiceType.DynamoDb);

        // POST DescribeTable, with a SigV4 Authorization header (the emulator doesn't verify
        // signatures, so any well-formed one stands in for SigV4Auth's); optionally add a literal
        // X-Amz-Algorithm query parameter to the URL.
        async Task<(int Status, JsonElement Body)> Raw(string tableName, bool tamperQuery = false)
        {
            var url = "/" + (tamperQuery ? "?X-Amz-Algorithm=AWS4-HMAC-SHA256" : string.Empty);
            var data = JsonSerializer.Serialize(new Dictionary<string, string> { ["TableName"] = tableName });
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = new StringContent(data, Encoding.UTF8);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-amz-json-1.0");
            request.Headers.TryAddWithoutValidation("X-Amz-Target", "DynamoDB_20120810.DescribeTable");
            var now = FidelityTime.UtcNow;
            var amzDate = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            request.Headers.TryAddWithoutValidation("X-Amz-Date", amzDate);
            request.Headers.TryAddWithoutValidation(
                "Authorization",
                $"AWS4-HMAC-SHA256 Credential=test/{amzDate[..8]}/{ctx.Region}/dynamodb/aws4_request, "
                + "SignedHeaders=content-type;host;x-amz-date;x-amz-target, Signature=" + new string('0', 64));
            Ensure(request.Headers.Contains("Authorization"), "SigV4Auth did not add an Authorization header");
            using var response = await http.SendAsync(request, ctx.CancellationToken);
            var text = await response.Content.ReadAsStringAsync(ctx.CancellationToken);
            using var document = JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text);
            return ((int)response.StatusCode, document.RootElement.Clone());
        }

        static string? Str(JsonElement body, params string[] path)
        {
            var current = body;
            foreach (var name in path)
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
                {
                    return null;
                }
            }

            return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
        }

        static string ErrType(JsonElement body)
        {
            var type = Str(body, "__type") ?? string.Empty;
            return type[(type.LastIndexOf('#') + 1)..];
        }

        var @base = FidelityContext.UniqueName("sim2real-behav-ddbauth-");
        var absent = @base + "-absent";
        var table = @base + "-t";

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

        // ---- 1. positive control: the raw signed request, untampered, against the real table.
        var (status, body) = await Raw(table);
        Ensure(status == 200, $"a plain signed DescribeTable gave {Py.Repr(status)}: {Py.Repr(body)}");
        Ensure(Str(body, "Table", "TableName") == table, $"the describe answered about {Py.Repr(Str(body, "Table", "TableName"))}, not {Py.Repr(table)}");
        Ensure(Str(body, "Table", "TableStatus") == "ACTIVE", $"the real table read back as {Py.Repr(Str(body, "Table", "TableStatus"))}, not ACTIVE");

        // ---- 2. control: the same raw signing path against an absent table gives a distinct,
        //         well-known refusal -- the baseline this check's refusal must differ from.
        (status, var bodyAbsent) = await Raw(absent);
        Ensure(status == 400, $"a plain signed DescribeTable on an absent table gave {Py.Repr(status)}");
        Ensure(ErrType(bodyAbsent) == Rnf, $"an absent table gave __type {Py.Repr(Str(bodyAbsent, "__type"))}, expected {Py.Repr(Rnf)}");

        // ---- 3. the refusal under test: the same request, plus a literal X-Amz-Algorithm query
        //         parameter alongside the Authorization header SigV4Auth still adds.
        (status, var bodyTampered) = await Raw(absent, tamperQuery: true);
        Ensure(status == 400, $"a request carrying both Authorization and X-Amz-Algorithm gave {Py.Repr(status)}: {Py.Repr(bodyTampered)}");
        var errType = ErrType(bodyTampered);
        Ensure(
            errType == Ise,
            $"a request carrying both Authorization and X-Amz-Algorithm gave __type {Py.Repr(Str(bodyTampered, "__type"))}, expected {Py.Repr(Ise)}");
        Ensure(
            !bodyTampered.TryGetProperty("message", out _),
            $"a request carrying both Authorization and X-Amz-Algorithm carried a message {Py.Repr(Str(bodyTampered, "message"))} -- measured, DynamoDB sends none for this refusal");
        Ensure(
            Py.Equal(bodyTampered, JsonDocument.Parse($"{{\"__type\": \"com.amazon.coral.service#{Ise}\"}}").RootElement),
            $"the refusal body was {Py.Repr(bodyTampered)}, expected only __type with no other member");
        Ensure(errType != ErrType(bodyAbsent), "the header/query conflict collapsed into the same code as a plain missing-table refusal");

        // ---- 4. the conflict is checked before the table lookup: the SAME refusal appears for a
        //         table that exists, proving it does not depend on -- or wait for -- resolving the name.
        (status, var bodyTamperedExisting) = await Raw(table, tamperQuery: true);
        Ensure(status == 400, $"the same conflict against an existing table gave {Py.Repr(status)}: {Py.Repr(bodyTamperedExisting)}");
        var errTypeExisting = ErrType(bodyTamperedExisting);
        Ensure(
            errTypeExisting == Ise,
            $"the conflict against an existing table gave __type {Py.Repr(Str(bodyTamperedExisting, "__type"))}, expected {Py.Repr(Ise)}");
        Ensure(
            Py.Equal(bodyTamperedExisting, bodyTampered),
            $"the conflict's body changed with the table name: {Py.Repr(bodyTamperedExisting)} vs {Py.Repr(bodyTampered)}");

        // ---- 5. effect-absent: none of the refused calls created anything, and the real table is
        //         unaffected by them.
        var names = new List<string>();
        string? token = null;
        while (true)
        {
            var page = await ddb.ListTablesAsync(new ListTablesRequest { ExclusiveStartTableName = token });
            names.AddRange((page.TableNames ?? []).Where(n => n.StartsWith(@base, StringComparison.Ordinal)));
            token = page.LastEvaluatedTableName;
            if (string.IsNullOrEmpty(token))
            {
                break;
            }
        }

        Ensure(
            names.SequenceEqual([table]),
            $"the refused calls left {Py.Repr(names.Where(n => n != table).Distinct().Order(StringComparer.Ordinal).ToList())} behind under {Py.Repr(@base)}");
        Ensure((await ddb.DescribeTableAsync(table)).Table.TableStatus == TableStatus.ACTIVE, "the real table is no longer ACTIVE after the refused calls");

        // ---- 6. verified teardown.
        await ddb.DeleteTableAsync(table);
        var clock = FidelityTime.StartNew();
        var budget = TimeSpan.FromSeconds(BudgetSeconds(ctx, 300.0));
        string? gone = null;
        while (clock.Elapsed < budget)
        {
            var error = await FidelityContext.CaptureError(() => ddb.DescribeTableAsync(table));
            if (error is not null)
            {
                gone = error.Code;
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2.0));
        }

        if (gone is null && BudgetSpent(ctx))
        {
            throw new TimeoutException($"table {Py.Repr(table)} deleted: the run budget ended first; not graded");
        }

        Ensure(gone == Rnf, $"the table was still describable after delete_table (code {Py.Repr(gone)})");
    }

    /// <summary>A poll's timeout: capSeconds, bounded by the runner's remaining time less 5 s.</summary>
    private static double BudgetSeconds(FidelityContext ctx, double capSeconds) => Math.Max(1.0, Math.Min(capSeconds, ctx.TimeLeftSeconds - 5.0));

    /// <summary>True when the runner's remaining time, not the wait's own deadline, ended a capped wait.</summary>
    private static bool BudgetSpent(FidelityContext ctx) => ctx.TimeLeftSeconds <= 6.0;

    /// <summary>Poll DescribeTable every second until the table is ACTIVE, bounded by the budget.</summary>
    private static async Task AwaitTableActive(FidelityContext ctx, IAmazonDynamoDB ddb, string table)
    {
        var clock = FidelityTime.StartNew();
        var budget = TimeSpan.FromSeconds(BudgetSeconds(ctx, 300.0));
        while (true)
        {
            var status = (await ddb.DescribeTableAsync(table)).Table.TableStatus?.Value;
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

    /// <summary>Failure-path teardown for a resource the success path already deleted.</summary>
    private static async Task AbsentOk(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is not null && error.Code != "ResourceNotFoundException")
        {
            throw error.Exception;
        }
    }
}
