// Translated from behavioral/dynamodb/check_composite_gsi_omits_item_missing_only_the_sort_key.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.composite-gsi-omits-item-missing-only-the-sort-key</summary>
[Category("AwsEvaluationSuite")]
public sealed class CompositeGsiOmitsItemMissingOnlyTheSortKey
{
    private const string Check = "composite-gsi-omits-item-missing-only-the-sort-key";

    /// <summary>
    /// An item carrying a composite GSI's partition key but not its sort key is excluded from
    /// that GSI: a Query and a Scan on the index both omit it, while a base-table Scan holds it.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.composite-gsi-omits-item-missing-only-the-sort-key")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("gpk", ScalarAttributeType.S),
                new AttributeDefinition("gsk", ScalarAttributeType.S),
            ],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = "gsi-1",
                    KeySchema = [new KeySchemaElement("gpk", KeyType.HASH), new KeySchemaElement("gsk", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        // Carries the GSI partition key but not its sort key: must never appear in the index.
        await ddb.PutItemAsync(table, Item(("pk", S("no-sort")), ("sk", S("1")), ("gpk", S("group-A"))));
        // Written afterwards with both GSI key attributes: its arrival in the index is the later
        // positive control that proves the earlier write has had time to propagate.
        await ddb.PutItemAsync(table, Item(("pk", S("full")), ("sk", S("1")), ("gpk", S("group-A")), ("gsk", S("a"))));

        var baseScan = await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true });
        var basePks = baseScan.Items.Select(item => item["pk"].S).ToHashSet(StringComparer.Ordinal);
        Ensure(
            basePks.SetEquals(["full", "no-sort"]),
            $"the base table must hold both items regardless of the GSI, got {Py.Repr(basePks.Order(StringComparer.Ordinal).ToList())}");

        async Task<(QueryResponse Q, ScanResponse S)> Probe()
        {
            var q = await ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                IndexName = "gsi-1",
                KeyConditionExpression = "gpk = :g",
                ExpressionAttributeValues = Item((":g", S("group-A"))),
            });
            var s = await ddb.ScanAsync(new ScanRequest { TableName = table, IndexName = "gsi-1" });
            return (q, s);
        }

        var (respQ, respS) = await PollUntil(Probe, qs => qs.Q.Count == 1 && qs.S.Count == 1, description: "gsi-1 propagation");

        var qPks = respQ.Items.Select(item => item["pk"].S).ToHashSet(StringComparer.Ordinal);
        Ensure(
            qPks.SetEquals(["full"]),
            $"a Query on gpk=group-A must return only the fully-keyed item, got {Py.Repr(qPks.Order(StringComparer.Ordinal).ToList())}");

        // The Scan is the real proof: it enumerates the index's own contents, so an index that
        // materialized "no-sort" under some other sort-key value cannot hide behind a targeted Query.
        var sPks = respS.Items.Select(item => item["pk"].S).ToHashSet(StringComparer.Ordinal);
        Ensure(
            sPks.SetEquals(["full"]),
            $"a Scan of gsi-1 must contain only the fully-keyed item, got {Py.Repr(sPks.Order(StringComparer.Ordinal).ToList())}");
    }

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutS = 60, double intervalS = 2.0, string description = "condition")
    {
        var clock = FidelityTime.StartNew();
        T? last = default;
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"{description} not met within {timeoutS.ToString(CultureInfo.InvariantCulture)}s. Last result: {Py.Repr(last)}");
    }
}
