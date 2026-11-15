// Translated from behavioral/dynamodb/check_gsi_added_to_a_populated_table_backfills_every_preexisting_item.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.gsi-added-to-a-populated-table-backfills-every-preexisting-item</summary>
[Category("AwsEvaluationSuite")]
public sealed class GsiAddedToAPopulatedTableBackfillsEveryPreexistingItem
{
    private const string CheckId = "gsi-added-to-a-populated-table-backfills-every-preexisting-item";
    private static readonly string[] Keys = ["A", "B", "C", "D", "E"];
    private const int PerKey = 10;
    private const int BackfillBudgetSeconds = 600;

    /// <summary>
    /// A GSI added by UpdateTable to a table already holding 50 items reaches ACTIVE with
    /// Backfilling cleared, and a query of it returns every pre-existing item under its own index
    /// partition key.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.gsi-added-to-a-populated-table-backfills-every-preexisting-item")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-backfill-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        var writtenPks = new List<string>();
        var expectedPerKey = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var key in Keys)
        {
            expectedPerKey[key] = PerKey;
            var batch = new List<WriteRequest>();
            for (var i = 0; i < PerKey; i++)
            {
                var itemPk = string.Create(CultureInfo.InvariantCulture, $"ITEM#{key}#{i:D2}");
                writtenPks.Add(itemPk);
                batch.Add(new WriteRequest { PutRequest = new PutRequest { Item = Item(("pk", S(itemPk)), ("gsi1pk", S(key))) } });
            }

            var pending = new Dictionary<string, List<WriteRequest>>(StringComparer.Ordinal) { [table] = batch };
            var clock = FidelityTime.StartNew();
            while (pending.Count > 0 && clock.Elapsed < TimeSpan.FromSeconds(120))
            {
                pending = (await ddb.BatchWriteItemAsync(new BatchWriteItemRequest { RequestItems = pending })).UnprocessedItems ?? [];
                if (pending.Count > 0)
                {
                    await FidelityTime.Delay(TimeSpan.FromSeconds(1));
                }
            }

            Ensure(pending.Count == 0, $"BatchWriteItem never drained UnprocessedItems: {Py.Repr(pending)}");
        }

        Ensure(
            writtenPks.Count == Keys.Length * PerKey,
            $"the fixture wrote {Py.Repr(writtenPks.Count)} items, expected {Py.Repr(Keys.Length * PerKey)}");

        // The index does not exist yet, so everything it returns below was backfilled.
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

        var backfillClock = FidelityTime.StartNew();
        string? status = null;
        bool? backfilling = null;
        while (backfillClock.Elapsed < TimeSpan.FromSeconds(BackfillBudgetSeconds))
        {
            var described = (await ddb.DescribeTableAsync(table)).Table;
            var index = (described.GlobalSecondaryIndexes ?? []).FirstOrDefault(i => i.IndexName == "gsi1");
            if (index is not null)
            {
                status = index.IndexStatus?.Value;
                backfilling = index.Backfilling;
                if (status == "ACTIVE" && backfilling is null or false)
                {
                    break;
                }
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(5));
        }

        Ensure(status == "ACTIVE", $"gsi1 did not reach ACTIVE within {Py.Repr(BackfillBudgetSeconds)}s: status {Py.Repr(status)}, Backfilling {Py.Repr(backfilling)}");
        Ensure(backfilling is null or false, $"the index reported ACTIVE while still backfilling: {Py.Repr(backfilling)}");

        var returnedPks = new List<string>();
        var perKey = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var key in Keys)
        {
            var got = new List<string>();
            Dictionary<string, AttributeValue>? startKey = null;
            while (true)
            {
                var page = await ddb.QueryAsync(new QueryRequest
                {
                    TableName = table,
                    IndexName = "gsi1",
                    KeyConditionExpression = "gsi1pk = :g",
                    ExpressionAttributeValues = Item((":g", S(key))),
                    ExclusiveStartKey = startKey,
                });
                got.AddRange(page.Items.Select(i => i["pk"].S));
                if (page.LastEvaluatedKey is null)
                {
                    break;
                }

                startKey = page.LastEvaluatedKey;
            }

            perKey[key] = got.Count;
            returnedPks.AddRange(got);
        }

        Ensure(
            returnedPks.Count == writtenPks.Count,
            $"the backfill missed pre-existing items: the index returned {Py.Repr(returnedPks.Count)} of {Py.Repr(writtenPks.Count)}");
        Ensure(
            Py.Equal(perKey, expectedPerKey),
            $"the backfill distributed items across the wrong index partitions: {Py.Repr(perKey)}, expected {Py.Repr(expectedPerKey)}");
        Ensure(
            returnedPks.Order(StringComparer.Ordinal).SequenceEqual(writtenPks.Order(StringComparer.Ordinal)),
            $"the index contents do not match the base table: symmetric difference {Py.Repr(writtenPks.ToHashSet(StringComparer.Ordinal).Except(returnedPks).Concat(returnedPks.ToHashSet(StringComparer.Ordinal).Except(writtenPks)).Order(StringComparer.Ordinal).ToList())}");
    }
}
