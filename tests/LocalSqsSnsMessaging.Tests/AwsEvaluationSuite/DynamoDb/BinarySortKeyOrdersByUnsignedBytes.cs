// Translated from behavioral/dynamodb/check_binary_sort_key_orders_by_unsigned_bytes.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.binary-sort-key-orders-by-unsigned-bytes</summary>
[Category("AwsEvaluationSuite")]
public sealed class BinarySortKeyOrdersByUnsignedBytes
{
    private const string Check = "binary-sort-key-orders-by-unsigned-bytes";

    // Four one-byte sort keys spanning the signed boundary. Compared as UNSIGNED bytes this is the
    // ascending order; compared as signed bytes the last two are negative and sort BELOW the first two,
    // which inverts the top half of the range.
    private static readonly byte[][] UnsignedOrder = [[0x01], [0x7f], [0x80], [0xff]];

    // A longer key is also included: a prefix sorts below any extension of itself.
    private static readonly byte[] Prefix = [0x80];
    private static readonly byte[] Extended = [0x80, 0x00];

    /// <summary>
    /// A Binary sort key orders by its bytes read as unsigned values (0x80 and 0xFF above 0x01 and 0x7F),
    /// ascending and descending, in a bounded range, and a key that is a prefix of another sorts below it:
    /// a signed comparison would invert the upper half of the range.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.binary-sort-key-orders-by-unsigned-bytes")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-binsort-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.B)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        async Task<List<byte[]>> Query(string partition, string condition = "pk = :p", Dictionary<string, AttributeValue>? values = null, bool? scanIndexForward = null)
        {
            var expressionValues = Item((":p", S(partition)));
            foreach (var (name, value) in values ?? [])
            {
                expressionValues[name] = value;
            }

            var resp = await ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                KeyConditionExpression = condition,
                ExpressionAttributeValues = expressionValues,
                ConsistentRead = true,
                ScanIndexForward = scanIndexForward,
            });
            return [.. resp.Items.Select(item => Bytes(item["sk"].B))];
        }

        static List<string> Hex(IEnumerable<byte[]> keys) => [.. keys.Select(b => Convert.ToHexStringLower(b))];

        // Written high-to-low so insertion order is the reverse of the expected answer.
        foreach (var sk in UnsignedOrder.Reverse())
        {
            await ddb.PutItemAsync(table, Item(("pk", S("SPAN")), ("sk", B(sk))));
        }

        var got = await Query("SPAN");
        Ensure(
            Hex(got).SequenceEqual(Hex(UnsignedOrder)),
            "a Binary sort key orders by unsigned bytes, so the ascending order is " +
            $"{Py.Repr(Hex(UnsignedOrder))}; got {Py.Repr(Hex(got))}. Leading 0x80 or " +
            "0xff indicates a signed byte comparison");

        var descending = await Query("SPAN", scanIndexForward: false);
        Ensure(
            Hex(descending).SequenceEqual(Hex(UnsignedOrder.Reverse())),
            "ScanIndexForward=False must return exactly the reverse: expected " +
            $"{Py.Repr(Hex(UnsignedOrder.Reverse()))}, got " +
            $"{Py.Repr(Hex(descending))}");

        // A range whose upper bound is 0x7f admits only the two keys below the signed boundary.
        var bounded = await Query("SPAN", condition: "pk = :p AND sk <= :hi", values: Item((":hi", B([0x7f]))));
        Ensure(
            Hex(bounded).SequenceEqual(["01", "7f"]),
            "a sort-key range bounded at 0x7f must admit 01 and 7f and exclude 80 and ff: got " +
            $"{Py.Repr(Hex(bounded))}");

        // --- a prefix sorts below its own extension ---
        foreach (var sk in new[] { Extended, Prefix })
        {
            await ddb.PutItemAsync(table, Item(("pk", S("PREFIX")), ("sk", B(sk))));
        }

        var prefixOrder = await Query("PREFIX");
        Ensure(
            Hex(prefixOrder).SequenceEqual(Hex([Prefix, Extended])),
            "a Binary key that is a prefix of another sorts below it: expected " +
            $"{Py.Repr(Hex([Prefix, Extended]))}, got {Py.Repr(Hex(prefixOrder))}");
    }
}
