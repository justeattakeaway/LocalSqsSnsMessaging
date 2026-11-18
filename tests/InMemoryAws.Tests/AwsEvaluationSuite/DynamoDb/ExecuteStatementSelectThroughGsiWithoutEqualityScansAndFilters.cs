// Translated from behavioral/dynamodb/check_execute_statement_select_through_gsi_without_equality_scans_and_filters.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.execute-statement-select-through-gsi-without-equality-scans-and-filters</summary>
[Category("AwsEvaluationSuite")]
public sealed class ExecuteStatementSelectThroughGsiWithoutEqualityScansAndFilters
{
    private const string CheckId = "execute-statement-select-through-gsi-without-equality-scans-and-filters";

    /// <summary>
    /// A PartiQL SELECT on a GSI with an index-key equality returns both indexed items; one without
    /// it scans and filters to the single matching item, not a base-table item outside the sparse GSI.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.execute-statement-select-through-gsi-without-equality-scans-and-filters")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-pqlgsiscan-");
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
                    IndexName = "status-index",
                    KeySchema = [new KeySchemaElement("status", KeyType.HASH), new KeySchemaElement("createdAt", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        var clock = FidelityTime.StartNew();
        Dictionary<string, string?> gsiStatus = [];
        while (clock.Elapsed < TimeSpan.FromSeconds(300))
        {
            var described = (await ddb.DescribeTableAsync(table)).Table;
            gsiStatus = (described.GlobalSecondaryIndexes ?? []).ToDictionary(i => i.IndexName, i => i.IndexStatus?.Value, StringComparer.Ordinal);
            if (gsiStatus.GetValueOrDefault("status-index") == "ACTIVE")
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        Ensure(gsiStatus.GetValueOrDefault("status-index") == "ACTIVE", $"status-index never became ACTIVE: {Py.Repr(gsiStatus)}");

        await ddb.PutItemAsync(table, Item(("pk", S("P1")), ("sk", S("s1")), ("status", S("active")), ("createdAt", S("2024-01-01"))));
        await ddb.PutItemAsync(table, Item(("pk", S("P1")), ("sk", S("s2")), ("status", S("active")), ("createdAt", S("2024-01-02"))));
        // In the base table only: it lacks the index keys, so the sparse GSI never holds it.
        await ddb.PutItemAsync(table, Item(("pk", S("P2")), ("sk", S("s2"))));

        // Positive control: an equality predicate on the index partition key routes through the
        // index and sees BOTH items, establishing that the index genuinely holds more than the one
        // item the filtered statement below must narrow down to.
        clock = FidelityTime.StartNew();
        List<Dictionary<string, AttributeValue>> allItems = [];
        while (clock.Elapsed < TimeSpan.FromSeconds(120))
        {
            allItems = (await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
            {
                Statement = $"SELECT * FROM \"{table}\".\"status-index\" WHERE status = 'active'",
            })).Items ?? [];
            if (allItems.Count == 2)
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }

        Ensure(
            allItems.Select(i => i["sk"].S).Order(StringComparer.Ordinal).SequenceEqual(["s1", "s2"]),
            $"the index must hold both items before the filtered statement below can prove it scanned rather than returning everything by coincidence: {Py.Repr(allItems)}");

        // Break the equality: the same index, with a WHERE clause on an attribute that is not part
        // of the index's key schema. Real AWS scans the index and applies this as a filter,
        // returning only the matching item.
        var filtered = await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"SELECT * FROM \"{table}\".\"status-index\" WHERE sk = 's2'",
        });
        var filteredItems = filtered.Items ?? [];
        Ensure(
            filteredItems.Count == 1,
            $"a non-key filter against the GSI must narrow the 2-item index to the 1 matching sk, got {filteredItems.Count} items: {Py.Repr(filteredItems)}");
        Ensure(
            Py.Equal(filteredItems[0]["sk"], S("s2")),
            $"expected the surviving item's sk to be 's2', got {Py.Repr(filteredItems[0].GetValueOrDefault("sk"))}");
        Ensure(
            Py.Equal(filteredItems[0]["pk"], S("P1")),
            $"the index scan must not return the base-table-only item, got {Py.Repr(filteredItems[0])}");
    }
}
