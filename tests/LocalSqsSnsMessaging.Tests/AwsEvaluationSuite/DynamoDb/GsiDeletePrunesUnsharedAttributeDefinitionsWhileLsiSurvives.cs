// Translated from behavioral/dynamodb/check_gsi_delete_prunes_unshared_attribute_definitions_while_lsi_survives.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.gsi-delete-prunes-unshared-attribute-definitions-while-lsi-survives</summary>
[Category("AwsEvaluationSuite")]
public sealed class GsiDeletePrunesUnsharedAttributeDefinitionsWhileLsiSurvives
{
    /// <summary>
    /// Deleting a GSI drops only the AttributeDefinitions no surviving key uses: an
    /// attribute that is also an LSI sort key stays declared, and base-table PutItem,
    /// GetItem and Query keep serving the item afterward.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_UpdateTable.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.gsi-delete-prunes-unshared-attribute-definitions-while-lsi-survives")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-gsilsi-");

        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("shared_attr", ScalarAttributeType.S),
                new AttributeDefinition("gsi_only_sort", ScalarAttributeType.S),
            ],
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = "lsi-main",
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("shared_attr", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = "gsi-main",
                    KeySchema = [new KeySchemaElement("shared_attr", KeyType.HASH), new KeySchemaElement("gsi_only_sort", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await WaitTableSettled(ddb, table);

        var item = Item(("pk", S("item-a")), ("sk", S("sort-a")), ("shared_attr", S("shared-value")), ("gsi_only_sort", S("gsi-sort-value")));
        await ddb.PutItemAsync(table, item);

        // Positive control before the delete: the GSI serves the item on its own key, once the
        // eventually-consistent index catches up with the write.
        var before = await WaitGsiQueryMatches(
            ddb,
            table,
            "gsi-main",
            "shared_attr = :s AND gsi_only_sort = :g",
            Item((":s", S("shared-value")), (":g", S("gsi-sort-value"))));
        Ensure(
            before.Count == 1 && Py.Equal(before.Items[0]["pk"], S("item-a")),
            $"expected the GSI to serve item-a before deletion, got {Py.Repr(before.Items)}");

        await ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = table,
            GlobalSecondaryIndexUpdates = [new GlobalSecondaryIndexUpdate { Delete = new DeleteGlobalSecondaryIndexAction { IndexName = "gsi-main" } }],
        });
        var deleted = await WaitGsiGone(ddb, table, "gsi-main");

        var deletedAttrs = deleted.AttributeDefinitions.ToDictionary(a => a.AttributeName, a => a.AttributeType.Value, StringComparer.Ordinal);
        var expectedAttrs = new Dictionary<string, string>(StringComparer.Ordinal) { ["pk"] = "S", ["sk"] = "S", ["shared_attr"] = "S" };
        Ensure(
            Py.Equal(deletedAttrs, expectedAttrs),
            $"expected AttributeDefinitions to keep the table keys plus the LSI's sort key ({{'pk': 'S', 'sk': 'S', 'shared_attr': 'S'}}) and drop gsi_only_sort, got {Py.Repr(deletedAttrs)}");
        var lsiNames = (deleted.LocalSecondaryIndexes ?? []).Select(i => i.IndexName).ToList();
        Ensure(lsiNames.SequenceEqual(["lsi-main"]), $"expected lsi-main to survive the GSI delete, got {Py.Repr(lsiNames)}");

        // Base-table PutItem still works after the index removal.
        var secondItem = Item(("pk", S("item-b")), ("sk", S("sort-b")), ("shared_attr", S("shared-value-2")));
        await ddb.PutItemAsync(table, secondItem);

        var got = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("item-b")), ("sk", S("sort-b"))), ConsistentRead = true });
        Ensure(Py.Equal(got.Item, secondItem), $"expected GetItem to return {Py.Repr(secondItem)}, got {Py.Repr(got.Item)}");

        var queried = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :p AND sk = :s",
            ExpressionAttributeValues = Item((":p", S("item-b")), (":s", S("sort-b"))),
            ConsistentRead = true,
        });
        Ensure(
            queried.Count == 1 && Py.Equal(queried.Items[0], secondItem),
            $"expected base-table Query to return {Py.Repr(secondItem)}, got {Py.Repr(queried.Items)}");

        // The surviving LSI still serves the attribute it shared with the deleted GSI.
        var lsiHit = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = "lsi-main",
            KeyConditionExpression = "pk = :p AND shared_attr = :s",
            ExpressionAttributeValues = Item((":p", S("item-a")), (":s", S("shared-value"))),
            ConsistentRead = true,
        });
        Ensure(
            lsiHit.Count == 1 && Py.Equal(lsiHit.Items[0]["pk"], S("item-a")),
            $"expected lsi-main to still serve item-a by shared_attr, got {Py.Repr(lsiHit.Items)}");
    }

    /// <summary>Poll DescribeTable until the table and every index are ACTIVE.</summary>
    private static async Task<TableDescription> WaitTableSettled(IAmazonDynamoDB ddb, string name, double timeoutSeconds = 1500.0, double intervalSeconds = 5.0)
    {
        var clock = FidelityTime.StartNew();
        TableDescription? table = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            table = (await ddb.DescribeTableAsync(name)).Table;
            var busy = (table.GlobalSecondaryIndexes ?? []).Where(i => i.IndexStatus != IndexStatus.ACTIVE).ToList();
            if (table.TableStatus == TableStatus.ACTIVE && busy.Count == 0)
            {
                return table;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"{name} never settled to ACTIVE within {timeoutSeconds}s; last={table?.TableStatus}");
    }

    /// <summary>Poll a GSI Query until it reflects a write (GSIs are eventually consistent).</summary>
    private static async Task<QueryResponse> WaitGsiQueryMatches(
        IAmazonDynamoDB ddb, string table, string indexName, string keyCondition, Dictionary<string, AttributeValue> values, double timeoutSeconds = 60.0, double intervalSeconds = 1.0)
    {
        var clock = FidelityTime.StartNew();
        QueryResponse? result = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            result = await ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                IndexName = indexName,
                KeyConditionExpression = keyCondition,
                ExpressionAttributeValues = values,
            });
            if (result.Count >= 1)
            {
                return result;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"GSI {indexName} never reflected the write within {timeoutSeconds}s; last={Py.Repr(result?.Items)}");
    }

    /// <summary>Poll DescribeTable until <c>indexName</c> no longer appears among the GSIs.</summary>
    private static async Task<TableDescription> WaitGsiGone(IAmazonDynamoDB ddb, string name, string indexName, double timeoutSeconds = 1500.0, double intervalSeconds = 5.0)
    {
        var clock = FidelityTime.StartNew();
        TableDescription? table = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            table = (await ddb.DescribeTableAsync(name)).Table;
            var names = (table.GlobalSecondaryIndexes ?? []).Select(g => g.IndexName).ToList();
            if (table.TableStatus == TableStatus.ACTIVE && !names.Contains(indexName))
            {
                return table;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"{indexName} still present on {name} after {timeoutSeconds}s; last={table?.TableStatus}");
    }
}
