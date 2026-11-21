// Translated from behavioral/dynamodb/check_describetable_tablename_wrong_json_type_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.describetable-tablename-wrong-json-type-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DescribetableTablenameWrongJsonTypeRejected
{
    private const string CheckId = "describetable-tablename-wrong-json-type-rejected";
    private const string Rnf = "ResourceNotFoundException";
    private const string Ser = "SerializationException";

    /// <summary>A poll's timeout: cap_s, bounded by the runner's remaining time less 5 s.</summary>
    private static double BudgetS(FidelityContext ctx, double capS) => Math.Max(1.0, Math.Min(capS, ctx.TimeLeftSeconds - 5.0));

    /// <summary>True when the runner's remaining time, not the wait's own deadline, ended a capped wait.</summary>
    private static bool BudgetSpent(FidelityContext ctx) => ctx.TimeLeftSeconds <= 6.0;

    /// <summary>Poll DescribeTable every second until the table is ACTIVE, bounded by the budget.</summary>
    private static async Task AwaitTableActive(FidelityContext ctx, IAmazonDynamoDB ddb, string table)
    {
        var clock = FidelityTime.StartNew();
        var deadline = BudgetS(ctx, 300.0);
        while (true)
        {
            var status = (await ddb.DescribeTableAsync(table)).Table.TableStatus?.Value;
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

    private static string? OptionalMember(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Code(JsonElement body) => (OptionalMember(body, "__type") ?? "").Split('#')[^1];

    private static List<string> MemberNames(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object ? [.. body.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)] : [];

    /// <summary>
    /// DescribeTable refuses a TableName whose raw JSON type is not a string with
    /// SerializationException naming the offending JSON token, before any table lookup.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.describetable-tablename-wrong-json-type-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;

        // POST a DescribeTable body carrying the raw JSON value for TableName.
        async Task<(int Status, JsonElement Body)> Raw(string tableNameJson)
        {
            var resp = await ctx.DynamoDbRaw("DescribeTable", $"{{\"TableName\": {tableNameJson}}}");
            return ((int)resp.StatusCode, resp.Json ?? JsonDocument.Parse("{}").RootElement);
        }

        var baseName = FidelityContext.UniqueName("sim2real-behav-ddbtntype-");
        var absent = baseName + "-absent";
        var table = baseName + "-t";

        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            BillingMode = BillingMode.PAY_PER_REQUEST,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(async () =>
        {
            try
            {
                await ddb.DeleteTableAsync(table);
            }
            catch (ResourceNotFoundException)
            {
            }
        });
        await AwaitTableActive(ctx, ddb, table);

        // ---- 1. positive control: a string TableName naming a real table succeeds.
        var (status, body) = await Raw(JsonSerializer.Serialize(table));
        Ensure(status == 200, $"a plain signed DescribeTable gave {status}: {Py.Repr(body)}");
        var described = body.TryGetProperty("Table", out var t) ? OptionalMember(t, "TableName") : null;
        Ensure(described == table, $"the describe answered about {Py.Repr(described)}, not {Py.Repr(table)}");

        // ---- 2. baseline: a string TableName naming nothing gives the well-known lookup refusal --
        //         the baseline the type-mismatch refusals below must differ from.
        var (statusAbsent, bodyAbsent) = await Raw(JsonSerializer.Serialize(absent));
        Ensure(statusAbsent == 400, $"a plain signed DescribeTable on an absent table gave {statusAbsent}");
        Ensure(Code(bodyAbsent) == Rnf, $"an absent table gave __type {Py.Repr(OptionalMember(bodyAbsent, "__type"))}, expected {Py.Repr(Rnf)}");

        // ---- 3. the refusal under test: TableName as each of several non-string JSON types gives
        //         SerializationException naming the JSON token it found, never the lookup refusal.
        (string Json, string Repr, string ExpectedMessage)[] matrix =
        [
            ("42", "42", "NUMBER_VALUE cannot be converted to String"),
            ("3.14", "3.14", "DECIMAL_VALUE cannot be converted to String"),
            ("true", "True", "TRUE_VALUE cannot be converted to String"),
            ("{\"a\": 1}", "{'a': 1}", "Start of structure or map found where not expected"),
        ];
        foreach (var (json, repr, expectedMessage) in matrix)
        {
            (status, body) = await Raw(json);
            Ensure(status == 400, $"TableName={repr} gave status {status}: {Py.Repr(body)}");
            var errType = Code(body);
            Ensure(errType == Ser, $"TableName={repr} gave __type {Py.Repr(OptionalMember(body, "__type"))}, expected {Py.Repr(Ser)}");
            Ensure(
                OptionalMember(body, "Message") == expectedMessage,
                $"TableName={repr} gave message {Py.Repr(OptionalMember(body, "Message"))}, expected {Py.Repr(expectedMessage)}");
            Ensure(MemberNames(body).SequenceEqual(["Message", "__type"]), $"TableName={repr} refusal body carried extra members: {Py.Repr(body)}");
        }

        // ---- 4. a JSON array also gives SerializationException, but with no Message at all --
        //         distinct enough from the scalar matrix above to be worth asserting on its own.
        var (statusArray, bodyArray) = await Raw("[\"a\", \"b\"]");
        Ensure(statusArray == 400, $"TableName=['a', 'b'] gave status {statusArray}: {Py.Repr(bodyArray)}");
        Ensure(Code(bodyArray) == Ser, $"TableName=['a', 'b'] gave __type {Py.Repr(OptionalMember(bodyArray, "__type"))}, expected {Py.Repr(Ser)}");
        Ensure(
            MemberNames(bodyArray).SequenceEqual(["__type"]),
            $"TableName=['a', 'b'] refusal body carried a Message: {Py.Repr(bodyArray)}, expected only __type");

        // ---- 5. effect-absent: none of the refused calls created a table or disturbed the real one.
        var names = new List<string>();
        string? token = null;
        while (true)
        {
            var page = await ddb.ListTablesAsync(new ListTablesRequest { ExclusiveStartTableName = token });
            names.AddRange((page.TableNames ?? []).Where(n => n.StartsWith(baseName, StringComparison.Ordinal)));
            token = page.LastEvaluatedTableName;
            if (string.IsNullOrEmpty(token))
            {
                break;
            }
        }

        Ensure(
            names.SequenceEqual([table]),
            $"the refused calls left {Py.Repr(names.Where(n => n != table).Distinct().Order(StringComparer.Ordinal).ToList())} behind under {Py.Repr(baseName)}");
        Ensure((await ddb.DescribeTableAsync(table)).Table.TableStatus == TableStatus.ACTIVE, "the real table is no longer ACTIVE after the refused calls");

        // ---- 6. verified teardown.
        await ddb.DeleteTableAsync(table);
        var clock = FidelityTime.StartNew();
        var deadline = BudgetS(ctx, 300.0);
        string? gone = null;
        while (clock.Elapsed.TotalSeconds < deadline)
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
}
