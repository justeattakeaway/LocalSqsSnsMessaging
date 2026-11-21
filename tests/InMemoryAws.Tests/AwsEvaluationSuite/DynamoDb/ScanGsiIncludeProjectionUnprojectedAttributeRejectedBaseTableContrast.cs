// Translated from behavioral/dynamodb/check_scan_gsi_include_projection_unprojected_attribute_rejected_base_table_contrast.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-gsi-include-projection-unprojected-attribute-rejected-base-table-contrast</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanGsiIncludeProjectionUnprojectedAttributeRejectedBaseTableContrast
{
    private const string Check = "scan-gsi-include-projection-unprojected-attribute-rejected-base-table-contrast";
    private const string Index = "status-index";

    private static readonly (string Code, string Message) NotProjected = (
        "ValidationException",
        $"One or more parameter values were invalid: Global secondary index {Index} does not project [details]");

    private static readonly SortedSet<string> ProjectedSet = new(["pk", "sk", "status", "createdAt", "summary"], StringComparer.Ordinal);

    /// <summary>A cleanup running undo() where a failed delete raises; in-use is retried for a while.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(undo);
            if (error is null || notFound.Contains(error.Code))
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed.TotalSeconds >= 300.0)
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    };

    /// <summary>Poll DescribeTable until the table and every index are ACTIVE.</summary>
    private static async Task WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var left = ctx.TimeLeftSeconds;
        var bound = Math.Max(0.0, (left == 0 ? 300.0 : left) - 30.0);
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed.TotalSeconds < bound)
        {
            desc = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = table })).Table;
            if (desc.TableStatus == TableStatus.ACTIVE && (desc.GlobalSecondaryIndexes ?? []).All(g => g.IndexStatus == IndexStatus.ACTIVE))
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {Py.Repr(desc)}");
    }

    /// <summary>(Error.Code, Error.Message) of the error fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }

    private static async Task ExpectError<T>(Func<Task<T>> fn, (string Code, string Message) expected)
        where T : class
    {
        var (gotCode, gotMessage) = await Error(fn);
        Ensure(gotCode == expected.Code, $"expected {Py.Repr(expected.Code)}, got {Py.Repr(gotCode)} ({Py.Repr(gotMessage)})");
        Ensure(gotMessage == expected.Message, $"unexpected message {Py.Repr(gotMessage)}");
    }

    private static Dictionary<string, Dictionary<string, AttributeValue>> Items() => new(StringComparer.Ordinal)
    {
        ["ITEM#1"] = Item(
            ("pk", S("ITEM#1")), ("sk", S("META")), ("status", S("available")),
            ("createdAt", S("2024-01-01T00:00:00Z")), ("summary", S("Summary one")),
            ("details", S("Detail one"))),
        ["ITEM#2"] = Item(
            ("pk", S("ITEM#2")), ("sk", S("META")), ("status", S("pending")),
            ("createdAt", S("2024-01-02T00:00:00Z")), ("summary", S("Summary two")),
            ("details", S("Detail two"))),
    };

    /// <summary>
    /// A Scan of an INCLUDE GSI refuses a ProjectionExpression naming a non-projected attribute,
    /// with or without Select=SPECIFIC_ATTRIBUTES, while one naming only projected attributes
    /// returns exactly that set for every indexed item, and a base table scan returns what it refused.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-gsi-include-projection-unprojected-attribute-rejected-base-table-contrast")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbgsiproj-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("status", ScalarAttributeType.S),
                new AttributeDefinition("createdAt", ScalarAttributeType.S),
            ],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = Index,
                    KeySchema = [new KeySchemaElement("status", KeyType.HASH), new KeySchemaElement("createdAt", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.INCLUDE, NonKeyAttributes = ["summary"] },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await WaitActive(ddb, ctx, table);

        var items = Items();
        foreach (var item in Items().Values)
        {
            await ddb.PutItemAsync(table, item);
        }

        await ExpectError(
            () => ddb.ScanAsync(new ScanRequest { TableName = table, IndexName = Index, ProjectionExpression = "details" }),
            NotProjected);
        await ExpectError(
            () => ddb.ScanAsync(new ScanRequest
            {
                TableName = table,
                IndexName = Index,
                ProjectionExpression = "details",
                Select = Select.SPECIFIC_ATTRIBUTES,
            }),
            NotProjected);

        // The index is eventually consistent: poll until it serves both items.
        var left = ctx.TimeLeftSeconds;
        var bound = Math.Max(0.0, (left == 0 ? 300.0 : left) - 60.0);
        var clock = FidelityTime.StartNew();
        List<Dictionary<string, AttributeValue>> idxItems;
        while (true)
        {
            idxItems = (await ddb.ScanAsync(new ScanRequest
            {
                TableName = table,
                IndexName = Index,
                ProjectionExpression = "pk, sk, #st, createdAt, summary",
                ExpressionAttributeNames = Names(("#st", "status")),
                Select = Select.SPECIFIC_ATTRIBUTES,
            })).Items;
            if (idxItems.Count >= 2)
            {
                break;
            }

            Ensure(clock.Elapsed.TotalSeconds < bound, $"index scan stayed at {idxItems.Count} item(s): {Py.Repr(idxItems)}");
            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        Ensure(idxItems.Count == 2, $"index scan returned {idxItems.Count} item(s), expected 2: {Py.Repr(idxItems)}");
        var byPk = idxItems.ToDictionary(i => i["pk"].S, StringComparer.Ordinal);
        var expectedKeys = new SortedSet<string>(items.Keys, StringComparer.Ordinal);
        Ensure(
            expectedKeys.SetEquals(byPk.Keys),
            $"index scan returned keys {Py.Repr(byPk.Keys.Order(StringComparer.Ordinal))}, expected {Py.Repr(expectedKeys)}");
        foreach (var (pk, idxItem) in byPk)
        {
            Ensure(
                ProjectedSet.SetEquals(idxItem.Keys),
                $"item {Py.Repr(pk)} from the index scan carried {Py.Repr(idxItem.Keys.Order(StringComparer.Ordinal))}, expected {Py.Repr(ProjectedSet)}");
            Ensure(
                Py.Equal(idxItem["summary"], items[pk]["summary"]),
                $"item {Py.Repr(pk)}'s projected summary was {Py.Repr(idxItem["summary"])}, expected {Py.Repr(items[pk]["summary"])}");
        }

        var baseItems = (await ddb.ScanAsync(new ScanRequest
        {
            TableName = table,
            ProjectionExpression = "pk, sk, details",
            ConsistentRead = true,
        })).Items;
        Ensure(baseItems.Count == 2, $"base table scan returned {baseItems.Count} item(s), expected 2: {Py.Repr(baseItems)}");
        var baseByPk = baseItems.ToDictionary(i => i["pk"].S, StringComparer.Ordinal);
        Ensure(
            expectedKeys.SetEquals(baseByPk.Keys),
            $"base table scan returned keys {Py.Repr(baseByPk.Keys.Order(StringComparer.Ordinal))}, expected {Py.Repr(expectedKeys)}");
        foreach (var (pk, baseItem) in baseByPk)
        {
            Ensure(
                new SortedSet<string>(baseItem.Keys, StringComparer.Ordinal).SetEquals(["pk", "sk", "details"]),
                $"item {Py.Repr(pk)} from the base table scan carried {Py.Repr(baseItem.Keys.Order(StringComparer.Ordinal))}, expected pk, sk, details");
            Ensure(
                Py.Equal(baseItem["details"], items[pk]["details"]),
                $"item {Py.Repr(pk)}'s base table details were {Py.Repr(baseItem["details"])}, expected {Py.Repr(items[pk]["details"])}");
        }
    }
}
