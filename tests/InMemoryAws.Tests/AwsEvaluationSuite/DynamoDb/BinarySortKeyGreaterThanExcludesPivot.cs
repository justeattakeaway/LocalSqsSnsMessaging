// Translated from behavioral/dynamodb/check_binary_sort_key_greater_than_excludes_pivot.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.binary-sort-key-greater-than-excludes-pivot</summary>
[Category("AwsEvaluationSuite")]
public sealed class BinarySortKeyGreaterThanExcludesPivot
{
    private const string Check = "binary-sort-key-greater-than-excludes-pivot";

    // Four one-byte sort keys spanning the signed/unsigned boundary.
    private static readonly byte[][] UnsignedOrder = [[0x01], [0x7f], [0x80], [0xff]];
    private static readonly byte[] Pivot = [0x7f];
    private static readonly byte[][] AbovePivot = [[0x80], [0xff]];
    private static readonly byte[] BelowAll = [0x00];
    private static readonly byte[] Prefix = [0x80];
    private static readonly byte[] Extended = [0x80, 0x00];

    /// <summary>
    /// A strict '>' key condition on a Binary sort key compares bytes as unsigned values,
    /// excludes an item equal to the pivot, and a key that is a prefix of another is strictly
    /// below its own extension.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.binary-sort-key-greater-than-excludes-pivot")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-binsortgt-");
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

        async Task<List<byte[]>> Query(string partition, byte[] pivot, bool? scanIndexForward = null)
        {
            var resp = await ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                KeyConditionExpression = "pk = :p AND sk > :piv",
                ExpressionAttributeValues = Item((":p", S(partition)), (":piv", B(pivot))),
                ConsistentRead = true,
                ScanIndexForward = scanIndexForward,
            });
            return [.. resp.Items.Select(item => Bytes(item["sk"].B))];
        }

        static List<string> Hex(IEnumerable<byte[]> keys) => [.. keys.Select(b => Convert.ToHexStringLower(b))];

        // Written high-to-low so insertion order cannot be mistaken for the query's order.
        foreach (var sk in UnsignedOrder.Reverse())
        {
            await ddb.PutItemAsync(table, Item(("pk", S("SPAN")), ("sk", B(sk))));
        }

        var above = await Query("SPAN", Pivot);
        Ensure(
            Hex(above).SequenceEqual(Hex(AbovePivot)),
            $"sk > {Py.Repr(Convert.ToHexStringLower(Pivot))} must exclude the pivot itself and everything below it: expected " +
            $"{Py.Repr(Hex(AbovePivot))}, got {Py.Repr(Hex(above))}");

        var descending = await Query("SPAN", Pivot, scanIndexForward: false);
        Ensure(
            Hex(descending).SequenceEqual(Hex(AbovePivot.Reverse())),
            "ScanIndexForward=False on the same '>' condition must return exactly the reverse: " +
            $"expected {Py.Repr(Hex(AbovePivot.Reverse()))}, got " +
            $"{Py.Repr(Hex(descending))}");

        // Positive control: a pivot below every stored key must admit all four, in ascending order,
        // proving the operator is applied rather than silently ignored.
        var allAbove = await Query("SPAN", BelowAll);
        Ensure(
            Hex(allAbove).SequenceEqual(Hex(UnsignedOrder)),
            $"sk > {Py.Repr(Convert.ToHexStringLower(BelowAll))} must admit every stored key in ascending unsigned-byte order: " +
            $"expected {Py.Repr(Hex(UnsignedOrder))}, got {Py.Repr(Hex(allAbove))}");

        // --- a prefix is strictly below its own extension under '>' too ---
        foreach (var sk in new[] { Extended, Prefix })
        {
            await ddb.PutItemAsync(table, Item(("pk", S("PREFIX")), ("sk", B(sk))));
        }

        var extensionOnly = await Query("PREFIX", Prefix);
        Ensure(
            Hex(extensionOnly).SequenceEqual(Hex([Extended])),
            $"sk > {Py.Repr(Convert.ToHexStringLower(Prefix))} must exclude the prefix itself and admit only its extension: " +
            $"expected {Py.Repr(Hex([Extended]))}, got {Py.Repr(Hex(extensionOnly))}");
    }
}
