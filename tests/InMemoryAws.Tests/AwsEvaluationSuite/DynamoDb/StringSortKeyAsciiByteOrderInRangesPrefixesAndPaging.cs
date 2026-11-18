// Translated from behavioral/dynamodb/check_string_sort_key_ascii_byte_order_in_ranges_prefixes_and_paging.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.string-sort-key-ascii-byte-order-in-ranges-prefixes-and-paging</summary>
[Category("AwsEvaluationSuite")]
public sealed class StringSortKeyAsciiByteOrderInRangesPrefixesAndPaging
{
    // UTF-8 byte order, which disagrees with case-insensitive or locale collation.
    private static readonly string[] ByteOrder = ["A", "B", "Z", "_", "a", "a b", "a-b", "aZ", "a_z", "ab", "b", "é", "中"];
    private static readonly string[] BetweenUnderscoreAndB = ["_", "a", "a b", "a-b", "aZ", "a_z", "ab", "b"];
    private static readonly string[] BeginsWithA = ["a", "a b", "a-b", "aZ", "a_z", "ab"];

    /// <summary>A cleanup running undo() where a failed delete raises; in-use is retried every 2 s.</summary>
    private static Func<Task> Teardown(Func<Task> undo, string notFound, double timeoutSeconds = 300.0) => async () =>
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(undo);
            if (error is null)
            {
                return;
            }

            if (error.Code == notFound)
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed.TotalSeconds >= timeoutSeconds)
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    };

    /// <summary>Poll DescribeTable every 2 s until the table and its indexes are ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var timeLeft = ctx.TimeLeftSeconds;
        var budget = Math.Max(0.0, (timeLeft > 0 ? timeLeft : 300.0) - 30.0);
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed.TotalSeconds < budget)
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            if (desc.TableStatus == TableStatus.ACTIVE
                && (desc.GlobalSecondaryIndexes ?? []).All(g => g.IndexStatus == IndexStatus.ACTIVE))
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {desc}");
    }

    private static List<string> Sks(QueryResponse resp) => [.. resp.Items.Select(it => it["sk"].S)];

    /// <summary>
    /// String sort keys order by UTF-8 bytes (A &lt; Z &lt; _ &lt; a, a-b &lt; aZ &lt; a_z &lt; ab) both directions;
    /// BETWEEN and begins_with select by bytes, and a Limit=1 walk visits each key once in that order.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.string-sort-key-ascii-byte-order-in-ranges-prefixes-and-paging")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-strord-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await WaitActive(ddb, ctx, table);

        foreach (var sk in Enumerable.Reverse(ByteOrder))
        {
            await ddb.PutItemAsync(table, Item(("pk", S("p")), ("sk", S(sk))));
        }

        QueryRequest Part() => new()
        {
            TableName = table,
            ConsistentRead = true,
            KeyConditionExpression = "pk = :p",
            ExpressionAttributeValues = Item((":p", S("p"))),
        };

        var got = Sks(await ddb.QueryAsync(Part()));
        Ensure(got.SequenceEqual(ByteOrder), $"forward Query order {Py.Repr(got)}, expected UTF-8 byte order {Py.Repr(ByteOrder)}");
        var reverse = Part();
        reverse.ScanIndexForward = false;
        got = Sks(await ddb.QueryAsync(reverse));
        Ensure(got.SequenceEqual(Enumerable.Reverse(ByteOrder)), $"reverse Query order {Py.Repr(got)} is not the mirror");

        got = Sks(await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            ConsistentRead = true,
            KeyConditionExpression = "pk = :p AND sk BETWEEN :lo AND :hi",
            ExpressionAttributeValues = Item((":p", S("p")), (":lo", S("_")), (":hi", S("b"))),
        }));
        Ensure(got.SequenceEqual(BetweenUnderscoreAndB), $"sk BETWEEN '_' AND 'b' selected {Py.Repr(got)}");

        got = Sks(await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            ConsistentRead = true,
            KeyConditionExpression = "pk = :p AND begins_with(sk, :pre)",
            ExpressionAttributeValues = Item((":p", S("p")), (":pre", S("a"))),
        }));
        Ensure(got.SequenceEqual(BeginsWithA), $"begins_with(sk, 'a') selected {Py.Repr(got)}");

        foreach (var (forward, want) in new[] { (true, ByteOrder.ToList()), (false, Enumerable.Reverse(ByteOrder).ToList()) })
        {
            var walk = new List<string>();
            var kwargs = Part();
            kwargs.Limit = 1;
            kwargs.ScanIndexForward = forward;
            while (true)
            {
                var resp = await ddb.QueryAsync(kwargs);
                walk.AddRange(Sks(resp));
                if (resp.LastEvaluatedKey is null || resp.LastEvaluatedKey.Count == 0)
                {
                    break;
                }

                Ensure(walk.Count <= want.Count, $"Limit=1 walk forward={Py.Repr(forward)} overran: {Py.Repr(walk)}");
                kwargs.ExclusiveStartKey = resp.LastEvaluatedKey;
            }

            Ensure(walk.SequenceEqual(want), $"Limit=1 walk forward={Py.Repr(forward)} visited {Py.Repr(walk)}, expected {Py.Repr(want)}");
        }
    }
}
