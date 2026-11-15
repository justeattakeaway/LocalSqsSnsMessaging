// Translated from behavioral/dynamodb/check_query_pagination_covers_every_item_exactly_once_in_key_order.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-pagination-covers-every-item-exactly-once-in-key-order</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryPaginationCoversEveryItemExactlyOnceInKeyOrder
{
    private const string Check = "query-pagination-covers-every-item-exactly-once-in-key-order";
    private const int NItems = 200;
    private const int Limit = 7;

    /// <summary>
    /// Paging a Query with Limit=7 returns all 200 numeric sort keys exactly once in ascending
    /// numeric order, and ScanIndexForward=False returns the exact reverse.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-pagination-covers-every-item-exactly-once-in-key-order")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-page-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("seq", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("seq", ScalarAttributeType.N)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        const string part = "P";
        var written = Enumerable.Range(0, NItems).ToList();
        for (var start = 0; start < NItems; start += 25)
        {
            Dictionary<string, List<WriteRequest>> pending = new()
            {
                [table] = [.. written.Skip(start).Take(25).Select(i => new WriteRequest
                {
                    PutRequest = new PutRequest { Item = Item(("pk", S(part)), ("seq", N(i.ToString(CultureInfo.InvariantCulture)))) },
                })],
            };
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

        async Task<(List<int> Keys, int Pages)> PageThrough(bool forward)
        {
            var keys = new List<int>();
            var pages = 0;
            Dictionary<string, AttributeValue>? startKey = null;
            while (true)
            {
                var resp = await ddb.QueryAsync(new QueryRequest
                {
                    TableName = table,
                    Limit = Limit,
                    ConsistentRead = true,
                    ScanIndexForward = forward,
                    KeyConditionExpression = "pk = :p",
                    ExpressionAttributeValues = Item((":p", S(part))),
                    ExclusiveStartKey = startKey,
                });
                pages++;
                keys.AddRange(resp.Items.Select(i => int.Parse(i["seq"].N, CultureInfo.InvariantCulture)));
                if (resp.LastEvaluatedKey is null)
                {
                    return (keys, pages);
                }

                startKey = resp.LastEvaluatedKey;
                Ensure(pages <= NItems, $"pagination did not terminate: {Py.Repr(pages)} pages for {Py.Repr(NItems)} items");
            }
        }

        var (forwardKeys, forwardPages) = await PageThrough(true);
        var (backward, _) = await PageThrough(false);
        int? divergence = null;
        for (var i = 0; i < forwardKeys.Count; i++)
        {
            if (i >= written.Count || forwardKeys[i] != written[i])
            {
                divergence = i;
                break;
            }
        }

        Ensure(
            forwardKeys.SequenceEqual(written),
            $"forward pagination dropped, duplicated or misordered items: first divergence at index {Py.Repr(divergence)} of {Py.Repr(forwardKeys.Count)} returned");
        var distinct = forwardKeys.Distinct().Count();
        Ensure(distinct == NItems, $"pagination returned a duplicate item: {Py.Repr(forwardKeys.Count - distinct)} duplicates");
        Ensure(
            backward.SequenceEqual(Enumerable.Reverse(written)),
            $"ScanIndexForward=False is not the exact reverse order: it began {Py.Repr(backward.Take(5).ToList())}");

        // ceil(200 / 7) = 29 pages; fewer means the service ignored Limit.
        const int expectedMinPages = (NItems + Limit - 1) / Limit;
        Ensure(
            forwardPages >= expectedMinPages,
            $"the service returned {Py.Repr(forwardPages)} pages for {Py.Repr(NItems)} items at Limit={Py.Repr(Limit)}, which " +
            $"is fewer than the {Py.Repr(expectedMinPages)} the limit allows -- Limit was not honoured");
    }
}
