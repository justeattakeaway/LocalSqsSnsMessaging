// Translated from behavioral/dynamodb/check_deletetable_tablename_wrong_json_type_serialization_exception.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.deletetable-tablename-wrong-json-type-serialization-exception</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeletetableTablenameWrongJsonTypeSerializationException
{
    private const string CheckId = "deletetable-tablename-wrong-json-type-serialization-exception";
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

    private static string Member(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";

    private static string? OptionalMember(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value) ? value.GetString() : null;

    private static string Code(JsonElement body) => Member(body, "__type").Split('#')[^1];

    /// <summary>
    /// DeleteTable with TableName sent as a non-string JSON value is rejected with
    /// SerializationException, naming the offending token, regardless of which tables exist.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.deletetable-tablename-wrong-json-type-serialization-exception")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;

        // POST DeleteTable with the given raw JSON value as TableName.
        async Task<(int Status, JsonElement Body)> Raw(string tableNameJson)
        {
            var resp = await ctx.DynamoDbRaw("DeleteTable", $"{{\"TableName\": {tableNameJson}}}");
            return ((int)resp.StatusCode, resp.Json ?? JsonDocument.Parse("{}").RootElement);
        }

        var baseName = FidelityContext.UniqueName("sim2real-behav-ddbdel-");
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

        // ---- 1. control: a correctly typed TableName against an absent table gives the
        //         well-known resource-not-found refusal -- the baseline the type error must differ from.
        var (status, bodyAbsent) = await Raw(JsonSerializer.Serialize(absent));
        Ensure(status == 400, $"a correctly typed DeleteTable on an absent table gave {status}");
        Ensure(
            Code(bodyAbsent) == Rnf,
            $"a correctly typed DeleteTable on an absent table gave __type {Py.Repr(OptionalMember(bodyAbsent, "__type"))}, expected {Py.Repr(Rnf)}");

        // ---- 2. the refusal under test: a JSON number where TableName (a string) is expected.
        var (statusNumber, bodyNumber) = await Raw("12345");
        Ensure(statusNumber == 400, $"TableName as a JSON number gave {statusNumber}: {Py.Repr(bodyNumber)}");
        var errTypeNumber = Code(bodyNumber);
        Ensure(
            errTypeNumber == Ser,
            $"TableName as a JSON number gave __type {Py.Repr(OptionalMember(bodyNumber, "__type"))}, expected {Py.Repr(Ser)}");
        Ensure(
            OptionalMember(bodyNumber, "Message") == "NUMBER_VALUE cannot be converted to String",
            $"TableName as a JSON number gave message {Py.Repr(OptionalMember(bodyNumber, "Message"))}, expected "
            + "'NUMBER_VALUE cannot be converted to String'");
        Ensure(errTypeNumber != Code(bodyAbsent), "the type error collapsed into the same code as the missing-table refusal");

        // ---- 3. a second non-string JSON type on the same member: a list, which carries no message.
        var (statusList, bodyList) = await Raw("[\"x\"]");
        Ensure(statusList == 400, $"TableName as a JSON list gave {statusList}: {Py.Repr(bodyList)}");
        Ensure(Code(bodyList) == Ser, $"TableName as a JSON list gave __type {Py.Repr(OptionalMember(bodyList, "__type"))}, expected {Py.Repr(Ser)}");
        Ensure(
            !bodyList.TryGetProperty("Message", out _),
            $"TableName as a JSON list carried a message {Py.Repr(OptionalMember(bodyList, "Message"))} -- measured, "
            + "DynamoDB sends none for this refusal");
        var numberType = Member(bodyNumber, "__type");
        var expectedListType = numberType[..numberType.LastIndexOf('#')] + "#" + Ser;
        Ensure(
            bodyList.EnumerateObject().Count() == 1 && OptionalMember(bodyList, "__type") == expectedListType,
            $"the list refusal body was {Py.Repr(bodyList)}, expected only __type with no other member");

        // ---- 4. effect-absent: none of the refused calls deleted or created anything. Both type
        //         refusals above already ran while this real table coexisted in the account, so the
        //         type check is shown not to depend on which names would otherwise be looked up.
        Ensure((await ddb.DescribeTableAsync(table)).Table.TableStatus == TableStatus.ACTIVE, "the real table is no longer ACTIVE after the refused calls");
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

        // ---- 5. positive control: the same raw signed path, with TableName correctly typed as a
        //         string, actually deletes the real table.
        var (statusDeleted, bodyDeleted) = await Raw(JsonSerializer.Serialize(table));
        Ensure(statusDeleted == 200, $"a correctly typed DeleteTable against the real table gave {statusDeleted}: {Py.Repr(bodyDeleted)}");
        var deletedName = bodyDeleted.TryGetProperty("TableDescription", out var description) ? OptionalMember(description, "TableName") : null;
        Ensure(deletedName == table, $"the delete answered about {Py.Repr(deletedName)}, not {Py.Repr(table)}");

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
