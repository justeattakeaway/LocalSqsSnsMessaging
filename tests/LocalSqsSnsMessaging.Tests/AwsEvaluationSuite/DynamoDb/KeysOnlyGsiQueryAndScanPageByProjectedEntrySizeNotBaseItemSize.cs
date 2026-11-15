// Translated from behavioral/dynamodb/check_keys_only_gsi_query_and_scan_page_by_projected_entry_size_not_base_item_size.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Text;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.keys-only-gsi-query-and-scan-page-by-projected-entry-size-not-base-item-size</summary>
[Category("AwsEvaluationSuite")]
public sealed class KeysOnlyGsiQueryAndScanPageByProjectedEntrySizeNotBaseItemSize
{
    private const string CheckId = "keys-only-gsi-query-and-scan-page-by-projected-entry-size-not-base-item-size";
    private const string Index = "idx-keys-only";
    private const int OneMb = 1024 * 1024;
    private const int Items = 23;
    private const int PayloadBytes = 48_000;
    private const string GroupKey = "G#1";
    private static readonly string[] KeyMembers = ["gsi1pk", "pk", "sk"];

    /// <summary>DynamoDB item size for an all-string item: UTF-8 length of each name plus its value.</summary>
    private static int ItemBytes(Dictionary<string, AttributeValue> item) =>
        item.Sum(e => Encoding.UTF8.GetByteCount(e.Key) + Encoding.UTF8.GetByteCount(e.Value.S));

    private static async Task WaitActive(IAmazonDynamoDB ddb, string table, double timeoutSeconds = 120.0)
    {
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            var indexes = desc.GlobalSecondaryIndexes ?? [];
            if (desc.TableStatus == TableStatus.ACTIVE && indexes.Count > 0 && indexes.All(g => g.IndexStatus == IndexStatus.ACTIVE))
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} or its GSI did not become ACTIVE: {Py.Repr(desc)}");
    }

    /// <summary>
    /// Poll <paramref name="fn"/> (a zero-arg call returning a DynamoDB response) until its Count reaches
    /// <paramref name="want"/> or the deadline passes; a GSI is eventually consistent so the first read may be short.
    /// Returns the response the caller asserts against -- the same call that reached the count.
    /// </summary>
    private static async Task<T> PollFull<T>(Func<Task<T>> fn, Func<T, int> count, int want, double timeoutSeconds = 60.0, double intervalSeconds = 2.0, string description = "condition")
    {
        var clock = FidelityTime.StartNew();
        var resp = await fn();
        while (count(resp) < want)
        {
            if (clock.Elapsed >= TimeSpan.FromSeconds(timeoutSeconds))
            {
                throw Fail($"{description} not met within {timeoutSeconds}s; last Count={count(resp)}, want {want}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
            resp = await fn();
        }

        return resp;
    }

    /// <summary>
    /// A KEYS_ONLY GSI's Query and Scan each page against the size of their own projected entries,
    /// not the base items, so both return every item in one page with no LastEvaluatedKey even when
    /// the base items alone exceed the 1 MB page limit that truncates the equivalent base-table Query.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.keys-only-gsi-query-and-scan-page-by-projected-entry-size-not-base-item-size")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-gsipage-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("gsi1pk", ScalarAttributeType.S),
            ],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = Index,
                    KeySchema = [new KeySchemaElement("gsi1pk", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await WaitActive(ddb, table);

        var payload = new string('x', PayloadBytes);
        var expectedSks = Enumerable.Range(0, Items).Select(i => i.ToString("000", CultureInfo.InvariantCulture)).Order(StringComparer.Ordinal).ToList();
        var items = expectedSks.Select(sk => Item(("pk", S("base")), ("sk", S(sk)), ("gsi1pk", S(GroupKey)), ("payload", S(payload)))).ToList();
        foreach (var item in items)
        {
            await ddb.PutItemAsync(table, item);
        }

        var totalBaseBytes = items.Sum(ItemBytes);
        Ensure(
            totalBaseBytes > OneMb,
            $"this check's premise requires the base items to exceed the 1 MB page limit; {Items} items totalled {totalBaseBytes} bytes");
        var keysOnlyEntryBytes = ItemBytes(Item(("pk", S("base")), ("sk", S("000")), ("gsi1pk", S(GroupKey))));
        var totalKeysOnlyBytes = keysOnlyEntryBytes * Items;
        Ensure(
            totalKeysOnlyBytes < OneMb,
            $"this check's premise requires the KEYS_ONLY index entries to total under 1 MB; {Items} entries totalled {totalKeysOnlyBytes} bytes");

        // Positive control: the base-table Query over the SAME items, by the base table's own key,
        // does truncate at 1 MB -- proving the premise that the underlying data really exceeds it.
        var baseResp = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :p",
            ExpressionAttributeValues = Item((":p", S("base"))),
            ConsistentRead = true,
        });
        Ensure(
            baseResp.LastEvaluatedKey is not null,
            $"a base-table Query over {totalBaseBytes} bytes must truncate at the 1 MB page limit and return a LastEvaluatedKey; got Count={baseResp.Count} and no LastEvaluatedKey");
        Ensure(baseResp.Count < Items, $"the truncated base-table page must be a strict subset of the {Items} items, got Count={baseResp.Count}");

        // The index entries must really have converged to all Items before the no-truncation
        // assertion means anything; the eventual-consistency wait and the final read are one call.
        var queryResp = await PollFull(
            () => ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                IndexName = Index,
                KeyConditionExpression = "gsi1pk = :g",
                ExpressionAttributeValues = Item((":g", S(GroupKey))),
            }),
            r => r.Count ?? 0,
            Items,
            description: $"the {Py.Repr(Index)} GSI holding all {Items} entries");
        Ensure(queryResp.Count == Items, $"Query on {Py.Repr(Index)} must return all {Items} index entries once converged, got Count={queryResp.Count}");
        Ensure(
            queryResp.LastEvaluatedKey is null,
            $"a KEYS_ONLY GSI Query bills and pages on its own projected entry size ({totalKeysOnlyBytes} bytes), not the {totalBaseBytes}-byte base items, so it must return all {Items} entries in one page; got a LastEvaluatedKey after {queryResp.Count} items");
        var queryKeys = queryResp.Items.Select(it => it["sk"].S).Order(StringComparer.Ordinal).ToList();
        Ensure(queryKeys.SequenceEqual(expectedSks), $"the GSI Query page must carry every sort key once, got {Py.Repr(queryKeys)}");
        var queryMembers = queryResp.Items.SelectMany(it => it.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        Ensure(queryMembers.SequenceEqual(KeyMembers), $"a KEYS_ONLY GSI entry carries only the base and index key attributes, got {Py.Repr(queryMembers)}");

        var scanResp = await PollFull(
            () => ddb.ScanAsync(new ScanRequest { TableName = table, IndexName = Index }),
            r => r.Count ?? 0,
            Items,
            description: $"a Scan of {Py.Repr(Index)} holding all {Items} entries");
        Ensure(scanResp.Count == Items, $"Scan of {Py.Repr(Index)} must return all {Items} index entries once converged, got Count={scanResp.Count}");
        Ensure(
            scanResp.LastEvaluatedKey is null,
            $"a KEYS_ONLY GSI Scan bills and pages on its own projected entry size ({totalKeysOnlyBytes} bytes), not the {totalBaseBytes}-byte base items, so it must return all {Items} entries in one page; got a LastEvaluatedKey after {scanResp.Count} items");
        var scanKeys = scanResp.Items.Select(it => it["sk"].S).Order(StringComparer.Ordinal).ToList();
        Ensure(scanKeys.SequenceEqual(expectedSks), $"the GSI Scan page must carry every sort key once, got {Py.Repr(scanKeys)}");
        Ensure(
            scanResp.ScannedCount == scanResp.Count,
            $"a Scan with no FilterExpression must have ScannedCount == Count, got ScannedCount={scanResp.ScannedCount} Count={scanResp.Count}");
    }
}
