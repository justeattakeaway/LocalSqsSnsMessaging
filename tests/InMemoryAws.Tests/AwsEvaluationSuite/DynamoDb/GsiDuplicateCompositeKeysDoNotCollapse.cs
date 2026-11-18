// Translated from behavioral/dynamodb/check_gsi_duplicate_composite_keys_do_not_collapse.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.gsi-duplicate-composite-keys-do-not-collapse</summary>
[Category("AwsEvaluationSuite")]
public sealed class GsiDuplicateCompositeKeysDoNotCollapse
{
    private const string CheckId = "gsi-duplicate-composite-keys-do-not-collapse";
    private const int Rows = 3;
    private const string Index = "shared-key-index";
    private const double ConvergeSeconds = 90.0;
    private const double IntervalSeconds = 1.0;

    /// <summary>
    /// Items sharing one GSI (hash, range) pair all stay addressable through the index: a Query returns every one,
    /// deleting one leaves the others, and moving one item's index key empties only its old slot.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.gsi-duplicate-composite-keys-do-not-collapse")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-gsidup-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("gsi_pk", ScalarAttributeType.S),
                new AttributeDefinition("gsi_sk", ScalarAttributeType.S),
            ],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = Index,
                    KeySchema = [new KeySchemaElement("gsi_pk", KeyType.HASH), new KeySchemaElement("gsi_sk", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        static string Str(string format, int i) => string.Format(CultureInfo.InvariantCulture, format, i);
        for (var i = 0; i < Rows; i++)
        {
            await ddb.PutItemAsync(table, Item(
                ("pk", S(Str("base-{0}", i))),
                ("sk", S(Str("sort-{0}", i))),
                ("gsi_pk", S("SHARED")),
                ("gsi_sk", S("SAME")),
                ("body", S(Str("payload-{0}", i)))));
        }

        var scanned = await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true });
        Ensure(
            scanned.Count == Rows,
            $"the base table must hold all {Py.Repr(Rows)} items -- they have distinct primary keys -- got {Py.Repr(scanned.Count)}: {Py.Repr(scanned.Items)}");

        // --- all rows sharing the index key are present ---
        var indexed = await PollIndex(ddb, table, "SHARED", "SAME", Rows, description: "initial backfill");
        var bodies = indexed.Items.Select(item => item["body"].S).Order(StringComparer.Ordinal).ToList();
        var expectedBodies = Enumerable.Range(0, Rows).Select(i => Str("payload-{0}", i)).ToList();
        Ensure(
            bodies.SequenceEqual(expectedBodies),
            $"a Query on the shared index key must return every base item that carries it, not just the last one written: expected {Py.Repr(expectedBodies)}, got {Py.Repr(bodies)}");

        // --- deleting one leaves the others in the slot ---
        await ddb.DeleteItemAsync(table, Item(("pk", S("base-1")), ("sk", S("sort-1"))));
        var afterDelete = await PollIndex(ddb, table, "SHARED", "SAME", Rows - 1, description: "after deleting one row");
        var survivors = afterDelete.Items.Select(item => item["pk"].S).Order(StringComparer.Ordinal).ToList();
        Ensure(
            survivors.SequenceEqual(["base-0", "base-2"]),
            $"deleting one item that shares the index key must remove only that row: expected [\"base-0\", \"base-2\"] still indexed, got {Py.Repr(survivors)}");

        // --- moving one item's index key empties only its old slot ---
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("base-0")), ("sk", S("sort-0"))),
            UpdateExpression = "SET gsi_sk = :new",
            ExpressionAttributeValues = Item((":new", S("MOVED"))),
        });
        var oldSlot = await PollIndex(ddb, table, "SHARED", "SAME", 1, description: "old slot after a move");
        Ensure(
            Py.Equal(oldSlot.Items[0]["pk"], S("base-2")),
            $"the item that did not move must still be at the shared key, got {Py.Repr(oldSlot.Items)}");
        var newSlot = await PollIndex(ddb, table, "SHARED", "MOVED", 1, description: "new slot after a move");
        Ensure(
            Py.Equal(newSlot.Items[0]["pk"], S("base-0")),
            $"the moved item must appear under its new index key, got {Py.Repr(newSlot.Items)}");
    }

    /// <summary>
    /// A GSI is eventually consistent, so wait for it to reach the expected row count rather than
    /// reading once. Returns the converged response and fails with what it last saw.
    /// </summary>
    private static async Task<QueryResponse> PollIndex(IAmazonDynamoDB ddb, string table, string gsiPk, string gsiSk, int expected, string description)
    {
        var clock = FidelityTime.StartNew();
        QueryResponse? last = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(ConvergeSeconds))
        {
            last = await ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                IndexName = Index,
                KeyConditionExpression = "gsi_pk = :p AND gsi_sk = :s",
                ExpressionAttributeValues = Item((":p", S(gsiPk)), (":s", S(gsiSk))),
            });
            if (last.Count == expected)
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(IntervalSeconds));
        }

        throw Fail(
            $"{description}: the index did not reach {Py.Repr(expected)} rows within {ConvergeSeconds}s. Last response had Count={Py.Repr(last?.Count)}: {Py.Repr(last?.Items)}");
    }
}
