// Translated from behavioral/dynamodb/check_all_projected_attributes_select_serves_include_index_projection.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.all-projected-attributes-select-serves-include-index-projection</summary>
[Category("AwsEvaluationSuite")]
public sealed class AllProjectedAttributesSelectServesIncludeIndexProjection
{
    private const string Check = "all-projected-attributes-select-serves-include-index-projection";
    private const string Lsi = "include-lsi";
    private const string Gsi = "include-gsi";

    /// <summary>
    /// Select=ALL_PROJECTED_ATTRIBUTES returns the table key, the index's own key and the index's
    /// NonKeyAttributes, and nothing else, on Query and Scan of an INCLUDE LSI and on Query of an
    /// INCLUDE GSI.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.all-projected-attributes-select-serves-include-index-projection")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbproj-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [.. new[] { "pk", "sk", "li", "gi" }.Select(n => new AttributeDefinition(n, ScalarAttributeType.S))],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = Lsi,
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("li", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.INCLUDE, NonKeyAttributes = ["kept_l"] },
                },
            ],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = Gsi,
                    KeySchema = [new KeySchemaElement("gi", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.INCLUDE, NonKeyAttributes = ["kept_g"] },
                },
            ],
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        await ddb.PutItemAsync(table, Item(
            ("pk", S("p")), ("sk", S("s")), ("li", S("l")), ("gi", S("g")),
            ("kept_l", S("KL")), ("kept_g", S("KG")), ("excluded", S("X"))));
        var baseItem = (await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("p")), ("sk", S("s"))),
            ConsistentRead = true,
        })).Item;
        Ensure(
            Py.Equal(baseItem.GetValueOrDefault("excluded"), S("X")),
            "the base item must really carry 'excluded', or the absences asserted below are vacuous: " +
            $"{Py.Repr(baseItem)}");

        // --- Query, INCLUDE LSI: the LSI shares the base table's partition, so a consistent read
        // needs no propagation wait. ---
        var lsiQuery = (await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = Lsi,
            KeyConditionExpression = "pk = :p",
            ExpressionAttributeValues = Item((":p", S("p"))),
            Select = Select.ALL_PROJECTED_ATTRIBUTES,
            ConsistentRead = true,
        })).Items[0];
        Ensure(
            Py.Equal(SortedKeys(lsiQuery), new[] { "kept_l", "li", "pk", "sk" }),
            "Query Select=ALL_PROJECTED_ATTRIBUTES on an INCLUDE LSI must return the table key, the " +
            $"index's own key and its NonKeyAttributes and nothing else, got {Py.Repr(SortedKeys(lsiQuery))}");
        Ensure(
            Py.Equal(lsiQuery.GetValueOrDefault("kept_l"), S("KL")),
            $"the INCLUDEd attribute must be served with its value, got {Py.Repr(lsiQuery.GetValueOrDefault("kept_l"))}");
        Ensure(
            !lsiQuery.ContainsKey("excluded"),
            $"an attribute outside the INCLUDE list must not be served, got {Py.Repr(lsiQuery)}");

        // --- Scan, INCLUDE LSI: same projection rule, the other operation. ---
        var lsiScan = (await ddb.ScanAsync(new ScanRequest
        {
            TableName = table,
            IndexName = Lsi,
            Select = Select.ALL_PROJECTED_ATTRIBUTES,
            ConsistentRead = true,
        })).Items[0];
        Ensure(
            Py.Equal(SortedKeys(lsiScan), new[] { "kept_l", "li", "pk", "sk" }),
            "Scan Select=ALL_PROJECTED_ATTRIBUTES on an INCLUDE LSI must return the table key, the " +
            $"index's own key and its NonKeyAttributes and nothing else, got {Py.Repr(SortedKeys(lsiScan))}");
        Ensure(
            Py.Equal(lsiScan.GetValueOrDefault("kept_l"), S("KL")),
            $"the INCLUDEd attribute must be served with its value, got {Py.Repr(lsiScan.GetValueOrDefault("kept_l"))}");
        Ensure(
            !lsiScan.ContainsKey("excluded"),
            $"an attribute outside the INCLUDE list must not be served, got {Py.Repr(lsiScan)}");

        // --- Query, INCLUDE GSI: eventually consistent, so poll until the write propagates. ---
        var gsiResp = await PollCount(
            () => ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                IndexName = Gsi,
                KeyConditionExpression = "gi = :g",
                ExpressionAttributeValues = Item((":g", S("g"))),
                Select = Select.ALL_PROJECTED_ATTRIBUTES,
            }),
            1);
        var gsiItem = gsiResp.Items[0];
        Ensure(
            Py.Equal(SortedKeys(gsiItem), new[] { "gi", "kept_g", "pk", "sk" }),
            "Query Select=ALL_PROJECTED_ATTRIBUTES on an INCLUDE GSI must return the table key, the " +
            $"index's own key and its NonKeyAttributes and nothing else, got {Py.Repr(SortedKeys(gsiItem))}");
        Ensure(
            Py.Equal(gsiItem.GetValueOrDefault("kept_g"), S("KG")),
            $"the INCLUDEd attribute must be served with its value, got {Py.Repr(gsiItem.GetValueOrDefault("kept_g"))}");
        Ensure(
            !gsiItem.ContainsKey("excluded"),
            $"an attribute outside the INCLUDE list must not be served, got {Py.Repr(gsiItem)}");
    }

    private static List<string> SortedKeys(Dictionary<string, AttributeValue> item) => [.. item.Keys.Order(StringComparer.Ordinal)];

    /// <summary><paramref name="fn"/>'s result once its Count reaches <paramref name="want"/>; a GSI write is eventually consistent.</summary>
    private static async Task<QueryResponse> PollCount(Func<Task<QueryResponse>> fn, int want, double timeoutS = 120.0, double intervalS = 2.0)
    {
        var clock = FidelityTime.StartNew();
        var result = await fn();
        while ((result.Count ?? 0) != want)
        {
            if (clock.Elapsed.TotalSeconds >= timeoutS)
            {
                throw Fail($"Count never reached {want} within {timeoutS.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}s: {Py.Repr(result.Items)}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
            result = await fn();
        }

        return result;
    }
}
