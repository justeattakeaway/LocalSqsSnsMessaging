// Translated from behavioral/dynamodb/check_gsi_added_to_a_populated_table_excludes_a_type_mismatched_item.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.gsi-added-to-a-populated-table-excludes-a-type-mismatched-item</summary>
[Category("AwsEvaluationSuite")]
public sealed class GsiAddedToAPopulatedTableExcludesATypeMismatchedItem
{
    private const string CheckId = "gsi-added-to-a-populated-table-excludes-a-type-mismatched-item";
    private const int BackfillBudgetSeconds = 1200;
    private const int PollIntervalSeconds = 2;

    // By the time cleanup runs, Run() has already confirmed the index is ACTIVE and not backfilling,
    // so this only guards against a race with the describe just read; keep it well under the suite's
    // cleanup budget.
    private const int CleanupWaitSeconds = 60;

    /// <summary>
    /// A GSI added by UpdateTable to a populated table skips a pre-existing item whose index-key
    /// attribute has the wrong type during backfill: once ACTIVE, a Query and a Scan of the index
    /// return every correctly-typed item and omit that one.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.gsi-added-to-a-populated-table-excludes-a-type-mismatched-item")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-gsi-backfill-typemismatch-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => SafeDeleteTable(ddb, table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        var validPks = Enumerable.Range(0, 3).Select(i => string.Create(CultureInfo.InvariantCulture, $"valid-{i}")).ToList();
        foreach (var pk in validPks)
        {
            await ddb.PutItemAsync(table, Item(("pk", S(pk)), ("gsi1pk", S("group-A"))));
        }

        // The violating item carries the index key with the WRONG type (N, not S): a real write the
        // table itself accepts because the type only becomes an index-key violation once the index
        // exists.
        await ddb.PutItemAsync(table, Item(("pk", S("violator")), ("gsi1pk", N("42"))));

        // The index does not exist yet, so everything the query below finds was backfilled.
        await ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = table,
            AttributeDefinitions = [new AttributeDefinition("gsi1pk", ScalarAttributeType.S)],
            GlobalSecondaryIndexUpdates =
            [
                new GlobalSecondaryIndexUpdate
                {
                    Create = new CreateGlobalSecondaryIndexAction
                    {
                        IndexName = "gsi1",
                        KeySchema = [new KeySchemaElement("gsi1pk", KeyType.HASH)],
                        Projection = new Projection { ProjectionType = ProjectionType.ALL },
                    },
                },
            ],
        });
        await WaitGsiActive(ddb, table, "gsi1");

        var query = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = "gsi1",
            KeyConditionExpression = "gsi1pk = :g",
            ExpressionAttributeValues = Item((":g", S("group-A"))),
        });
        var queryPks = query.Items.Select(item => item["pk"].S).ToHashSet(StringComparer.Ordinal);
        Ensure(queryPks.SetEquals(validPks), $"gsi1 query under gsi1pk=group-A returned {Py.Repr(queryPks.ToList())}, expected exactly {Py.Repr(validPks)}");

        // The Scan is the real proof: it enumerates the index's own contents, so an emulator that
        // materializes the violator into the index under some other key value cannot hide behind a
        // query that simply never asked for that value.
        var scan = await ddb.ScanAsync(new ScanRequest { TableName = table, IndexName = "gsi1" });
        var scanPks = scan.Items.Select(item => item["pk"].S).ToHashSet(StringComparer.Ordinal);
        Ensure(scanPks.SetEquals(validPks), $"gsi1 scan returned {Py.Repr(scanPks.ToList())}, expected exactly the correctly-typed items {Py.Repr(validPks)}");

        // The violator is still a live item on the base table -- untouched, just unindexed.
        var @base = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("violator"))), ConsistentRead = true });
        Ensure(@base.Item is not null, "the type-mismatched item must still exist on the base table");
    }

    /// <summary>Poll until the index reaches ACTIVE with backfilling cleared; never a transitional read.</summary>
    private static async Task WaitGsiActive(IAmazonDynamoDB ddb, string tableName, string indexName)
    {
        var clock = FidelityTime.StartNew();
        string? status = null;
        bool? backfilling = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(BackfillBudgetSeconds))
        {
            var described = (await ddb.DescribeTableAsync(tableName)).Table;
            var index = (described.GlobalSecondaryIndexes ?? []).FirstOrDefault(i => i.IndexName == indexName);
            if (index is not null)
            {
                status = index.IndexStatus?.Value;
                backfilling = index.Backfilling;
                if (status == "ACTIVE" && backfilling is null or false)
                {
                    return;
                }
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(PollIntervalSeconds));
        }

        throw Fail($"{indexName} did not reach ACTIVE within {BackfillBudgetSeconds}s: status {Py.Repr(status)}, Backfilling {Py.Repr(backfilling)}");
    }

    /// <summary>Delete once no index is still building; DeleteTable refuses it before then.</summary>
    private static async Task SafeDeleteTable(IAmazonDynamoDB ddb, string tableName)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var described = (await ddb.DescribeTableAsync(tableName)).Table;
            var busy = (described.GlobalSecondaryIndexes ?? []).Where(i => i.IndexStatus != IndexStatus.ACTIVE).ToList();
            if (described.TableStatus == TableStatus.ACTIVE && busy.Count == 0)
            {
                break;
            }

            if (clock.Elapsed >= TimeSpan.FromSeconds(CleanupWaitSeconds))
            {
                throw Fail($"table {tableName} indexes never settled for cleanup");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(PollIntervalSeconds));
        }

        var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(tableName));
        if (error is not null && error.Code != "ResourceNotFoundException")
        {
            throw error.Exception;
        }
    }
}
