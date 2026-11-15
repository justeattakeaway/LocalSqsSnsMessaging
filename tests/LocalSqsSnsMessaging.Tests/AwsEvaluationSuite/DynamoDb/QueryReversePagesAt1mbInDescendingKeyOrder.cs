// Translated from behavioral/dynamodb/check_query_reverse_pages_at_1mb_in_descending_key_order.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-reverse-pages-at-1mb-in-descending-key-order</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryReversePagesAt1mbInDescendingKeyOrder
{
    private const int OneMb = 1024 * 1024;
    private const int Count = 45;
    private const int Payload = 50 * 1024;
    private const int MaxPages = 20;

    /// <summary>A cleanup running undo() where a failed delete raises; in-use is retried every 2 s.</summary>
    private static Func<Task> Teardown(Func<Task> undo, string[] notFound, double timeoutS = 300.0) => async () =>
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(undo);
            if (error is null || notFound.Contains(error.Code))
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed.TotalSeconds >= timeoutS)
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    };

    /// <summary>Poll DescribeTable every 2 s until the table and its indexes are ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var left = ctx.TimeLeftSeconds;
        var clock = FidelityTime.StartNew();
        var deadline = TimeSpan.FromSeconds(Math.Max(0.0, (left == 0 ? 300.0 : left) - 30.0));
        TableDescription? desc = null;
        while (clock.Elapsed < deadline)
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            if (desc.TableStatus == TableStatus.ACTIVE && (desc.GlobalSecondaryIndexes ?? []).All(g => g.IndexStatus == IndexStatus.ACTIVE))
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {Py.Repr(desc)}");
    }

    /// <summary>BatchWriteItem in chunks of 25, re-sending UnprocessedItems every 2 s until none remain.</summary>
    private static async Task BatchPut(IAmazonDynamoDB ddb, FidelityContext ctx, string table, List<Dictionary<string, AttributeValue>> items)
    {
        var left = ctx.TimeLeftSeconds;
        var clock = FidelityTime.StartNew();
        var deadline = TimeSpan.FromSeconds(Math.Max(0.0, (left == 0 ? 300.0 : left) - 60.0));
        for (var start = 0; start < items.Count; start += 25)
        {
            Dictionary<string, List<WriteRequest>> pending = new()
            {
                [table] = [.. items.Skip(start).Take(25).Select(it => new WriteRequest { PutRequest = new PutRequest { Item = it } })],
            };
            while (true)
            {
                pending = (await ddb.BatchWriteItemAsync(new BatchWriteItemRequest { RequestItems = pending })).UnprocessedItems ?? [];
                if (pending.Count == 0)
                {
                    break;
                }

                Ensure(clock.Elapsed < deadline, $"BatchWriteItem left {Py.Repr(pending.Values.Sum(v => v.Count))} items unprocessed");
                await FidelityTime.Delay(TimeSpan.FromSeconds(2));
            }
        }
    }

    /// <summary>Item size for an all-string item: UTF-8 length of each name plus its value.</summary>
    private static int ItemBytes(Dictionary<string, AttributeValue> item) =>
        item.Sum(e => Encoding.UTF8.GetByteCount(e.Key) + Encoding.UTF8.GetByteCount(e.Value.S));

    /// <summary>Every Query page of partition 'big' with no Limit, following LastEvaluatedKey.</summary>
    private static async Task<List<QueryResponse>> Walk(IAmazonDynamoDB ddb, string table, bool forward)
    {
        var request = new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :p",
            ConsistentRead = true,
            ExpressionAttributeValues = Item((":p", S("big"))),
            ScanIndexForward = forward,
        };
        var pages = new List<QueryResponse>();
        while (true)
        {
            var resp = await ddb.QueryAsync(request);
            pages.Add(resp);
            if (resp.LastEvaluatedKey is null)
            {
                return pages;
            }

            Ensure(pages.Count < MaxPages, $"Query did not end after {MaxPages} pages");
            request.ExclusiveStartKey = resp.LastEvaluatedKey;
        }
    }

    /// <summary>
    /// A ScanIndexForward=False Query with no Limit over ~2.3 MB pages at 1 MB (within one item),
    /// each LastEvaluatedKey is its page's last key, and the walk yields every key once, descending.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Query.Pagination.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-reverse-pages-at-1mb-in-descending-key-order")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-qrev1mb-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), ["ResourceNotFoundException"]));
        await WaitActive(ddb, ctx, table);

        var payload = new string('x', Payload);
        var items = Enumerable.Range(0, Count)
            .Select(i => Item(("pk", S("big")), ("sk", S(i.ToString("D3", CultureInfo.InvariantCulture))), ("payload", S(payload))))
            .ToList();
        await BatchPut(ddb, ctx, table, items);
        var ascending = items.Select(it => it["sk"].S).ToList();
        var largest = items.Max(ItemBytes);
        foreach (var (forward, want) in new[] { (false, Enumerable.Reverse(ascending).ToList()), (true, ascending) })
        {
            var forwardRepr = forward ? "True" : "False";
            var pages = await Walk(ddb, table, forward);
            var sizes = pages.Select(p => p.Items.Sum(ItemBytes)).ToList();
            Ensure(pages.Count >= 2, $"forward={forwardRepr}: ~{Count * Payload} bytes came back in one page: {Py.Repr(sizes)}");
            for (var n = 1; n <= pages.Count; n++)
            {
                var page = pages[n - 1];
                Ensure(
                    sizes[n - 1] <= OneMb + largest,
                    $"forward={forwardRepr} page {n} holds {sizes[n - 1]} bytes, over 1 MB plus one item: {Py.Repr(sizes)}");
                var lek = page.LastEvaluatedKey;
                if (n < pages.Count)
                {
                    var last = page.Items[^1];
                    Ensure(
                        Py.Equal(lek, Item(("pk", last["pk"]), ("sk", last["sk"]))),
                        $"forward={forwardRepr} page {n} LastEvaluatedKey {Py.Repr(lek)} is not its last item's key");
                }
            }

            var got = pages.SelectMany(p => p.Items).Select(it => it["sk"].S).ToList();
            Ensure(
                got.SequenceEqual(want),
                $"forward={forwardRepr} paged Query returned {Py.Repr(got)}, expected every key once in order {Py.Repr(want)}");
        }
    }
}
