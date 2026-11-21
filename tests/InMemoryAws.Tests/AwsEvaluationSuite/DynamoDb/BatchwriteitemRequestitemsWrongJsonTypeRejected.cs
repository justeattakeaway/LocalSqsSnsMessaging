// Translated from behavioral/dynamodb/check_batchwriteitem_requestitems_wrong_json_type_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batchwriteitem-requestitems-wrong-json-type-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchwriteitemRequestitemsWrongJsonTypeRejected
{
    private const string Check = "batchwriteitem-requestitems-wrong-json-type-rejected";
    private const string Rnf = "ResourceNotFoundException";
    private const string Ser = "SerializationException";

    /// <summary>
    /// BatchWriteItem refuses a RequestItems that is a JSON string or number with
    /// SerializationException "Unexpected field type", and a JSON array with a SerializationException
    /// carrying no Message; none of them writes an item.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batchwriteitem-requestitems-wrong-json-type-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;

        // POST a BatchWriteItem body carrying requestItemsValue for RequestItems.
        async Task<(int Status, JsonElement Body)> Raw(object requestItemsValue)
        {
            var raw = await ctx.DynamoDbRaw("BatchWriteItem", JsonSerializer.Serialize(new { RequestItems = requestItemsValue }));
            using var document = JsonDocument.Parse(string.IsNullOrEmpty(raw.Body) ? "{}" : raw.Body);
            return ((int)raw.StatusCode, document.RootElement.Clone());
        }

        var baseName = FidelityContext.UniqueName("sim2real-behav-ddbbwitype-");
        var table = baseName + "-t";

        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            BillingMode = BillingMode.PAY_PER_REQUEST,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(async () =>
        {
            var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(table));
            if (error is not null && error.Code != "ResourceNotFoundException")
            {
                throw error.Exception;
            }
        });
        await AwaitTableActive(ctx, ddb, table);

        // ---- 1. positive control: a well-formed RequestItems map against the real table succeeds.
        var (status, body) = await Raw(new Dictionary<string, object>
        {
            [table] = new[] { new { PutRequest = new { Item = new { pk = new { S = "control" } } } } },
        });
        Ensure(status == 200, $"a plain signed BatchWriteItem gave {status}: {Py.Repr(body)}");
        Ensure(
            body.ValueKind == JsonValueKind.Object && body.EnumerateObject().Count() == 1
                && body.TryGetProperty("UnprocessedItems", out var ui) && ui.ValueKind == JsonValueKind.Object && !ui.EnumerateObject().Any(),
            $"unexpected success body: {Py.Repr(body)}");
        var got = await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true });
        Ensure(Py.Equal(got.Items, new[] { Item(("pk", S("control"))) }), $"positive control not visible: {Py.Repr(got.Items)}");

        // ---- 2. the refusal under test: RequestItems as a JSON string or number gives
        //         SerializationException with the same "Unexpected field type" message.
        var matrix = new (string Label, object Value, string ExpectedMessage)[]
        {
            ("a string", "oops", "Unexpected field type"),
            ("a number", 42, "Unexpected field type"),
        };
        foreach (var (label, value, expectedMessage) in matrix)
        {
            (status, body) = await Raw(value);
            Ensure(status == 400, $"RequestItems={Py.Repr(value)} ({label}) gave status {status}: {Py.Repr(body)}");
            var errType = TypeOf(body);
            Ensure(
                errType == Ser,
                $"RequestItems={Py.Repr(value)} ({label}) gave __type {Py.Repr(Member(body, "__type"))}, expected {Py.Repr(Ser)}");
            Ensure(
                Member(body, "Message")?.GetString() == expectedMessage,
                $"RequestItems={Py.Repr(value)} ({label}) gave message {Py.Repr(Member(body, "Message"))}, expected {Py.Repr(expectedMessage)}");
            Ensure(
                body.EnumerateObject().Select(p => p.Name).ToHashSet().SetEquals(["__type", "Message"]),
                $"RequestItems={Py.Repr(value)} ({label}) refusal body carried extra members: {Py.Repr(body)}");
        }

        // ---- 3. a JSON array also gives SerializationException, kept separate from the scalar
        //         matrix above since the array's refusal body carries no Message at all.
        var (arrayStatus, bodyArray) = await Raw(new[] { "a", "b" });
        Ensure(arrayStatus == 400, $"RequestItems=['a', 'b'] gave status {arrayStatus}: {Py.Repr(bodyArray)}");
        Ensure(
            TypeOf(bodyArray) == Ser,
            $"RequestItems=['a', 'b'] gave __type {Py.Repr(Member(bodyArray, "__type"))}, expected {Py.Repr(Ser)}");
        Ensure(
            bodyArray.EnumerateObject().Select(p => p.Name).ToHashSet().SetEquals(["__type"]),
            $"RequestItems=['a', 'b'] refusal body carried a Message: {Py.Repr(bodyArray)}, expected only __type");

        // ---- 4. effect-absent: none of the refused calls wrote anything to the real table.
        var after = await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true });
        Ensure(Py.Equal(after.Items, new[] { Item(("pk", S("control"))) }), $"the refused calls disturbed the table, got {Py.Repr(after.Items)}");

        // ---- 5. verified teardown.
        await ddb.DeleteTableAsync(table);
        var bound = BudgetS(ctx, 300.0);
        var clock = FidelityTime.StartNew();
        string? gone = null;
        while (clock.Elapsed.TotalSeconds < bound)
        {
            var error = await FidelityContext.CaptureError(() => ddb.DescribeTableAsync(new DescribeTableRequest { TableName = table }));
            if (error is not null)
            {
                gone = error.Code;
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        if (gone is null && BudgetSpent(ctx))
        {
            throw new TimeoutException($"table {Py.Repr(table)} deleted: the run budget ended first; not graded");
        }

        Ensure(gone == Rnf, $"the table was still describable after delete_table (code {Py.Repr(gone)})");
    }

    private static JsonElement? Member(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : null;

    /// <summary><c>body.get("__type", "").rsplit("#", 1)[-1]</c>.</summary>
    private static string TypeOf(JsonElement body)
    {
        var type = Member(body, "__type")?.GetString() ?? "";
        return type[(type.LastIndexOf('#') + 1)..];
    }

    /// <summary>A poll's timeout: capS, bounded by the runner's remaining time less 5 s.</summary>
    private static double BudgetS(FidelityContext ctx, double capS) => Math.Max(1.0, Math.Min(capS, ctx.TimeLeftSeconds - 5.0));

    /// <summary>True when the runner's remaining time, not the wait's own deadline, ended a capped wait.</summary>
    private static bool BudgetSpent(FidelityContext ctx) => ctx.TimeLeftSeconds <= 6.0;

    /// <summary>Poll DescribeTable every second until the table is ACTIVE, bounded by the budget.</summary>
    private static async Task AwaitTableActive(FidelityContext ctx, IAmazonDynamoDB ddb, string table)
    {
        var bound = BudgetS(ctx, 300.0);
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var status = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = table })).Table.TableStatus;
            if (status == TableStatus.ACTIVE)
            {
                return;
            }

            if (clock.Elapsed.TotalSeconds >= bound && BudgetSpent(ctx))
            {
                throw new TimeoutException($"table {Py.Repr(table)} ACTIVE: the run budget ended first; not graded");
            }

            Ensure(clock.Elapsed.TotalSeconds < bound, $"table {Py.Repr(table)} never reached ACTIVE (last {Py.Repr(status)})");
            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }
    }
}
