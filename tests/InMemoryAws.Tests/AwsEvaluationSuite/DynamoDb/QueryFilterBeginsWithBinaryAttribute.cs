// Translated from behavioral/dynamodb/check_query_filter_begins_with_binary_attribute.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-filter-begins-with-binary-attribute</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryFilterBeginsWithBinaryAttribute
{
    private const string Check = "query-filter-begins-with-binary-attribute";

    /// <summary>Items in the queried partition: (sk, blob). Expected sets below are derived from the bytes.</summary>
    private static readonly (string Sk, byte[] Blob)[] Rows =
    [
        ("i1", [0x01, 0xaa]),
        ("i2", [0x01, 0xbb, 0x02]),
        ("i3", [0x02, 0xaa]),
        ("i4", [0x02, 0xbb]),
        ("i5", [0x03]),
    ];

    /// <summary>
    /// Query's FilterExpression begins_with() on a non-key Binary attribute selects items whose
    /// value shares the given byte prefix, over the whole partition it scans.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-filter-begins-with-binary-attribute")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-ddbqfbw-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        foreach (var (sk, blob) in Rows)
        {
            await ddb.PutItemAsync(t, Item(("pk", S("p1")), ("sk", S(sk)), ("blob", B(blob))));
        }

        Task<QueryResponse> QueryPrefix(byte[] prefix) => ddb.QueryAsync(new QueryRequest
        {
            TableName = t,
            KeyConditionExpression = "#pk = :pk",
            FilterExpression = "begins_with(#b, :p)",
            ExpressionAttributeNames = Names(("#pk", "pk"), ("#b", "blob")),
            ExpressionAttributeValues = Item((":pk", S("p1")), (":p", B(prefix))),
            ConsistentRead = true,
        });

        (byte[] Prefix, string[] Expected)[] cases =
        [
            ([0x01], ["i1", "i2"]),
            ([0x02], ["i3", "i4"]),
            ([0x01, 0xaa], ["i1"]),
            ([0x09], []),
        ];
        foreach (var (prefix, expected) in cases)
        {
            var resp = await QueryPrefix(prefix);
            var got = resp.Items.Select(i => i["sk"].S).ToHashSet(StringComparer.Ordinal);
            Ensure(
                got.SetEquals(expected),
                $"begins_with(blob, {Py.Repr(prefix)}) returned {Py.Repr(got.Order(StringComparer.Ordinal).ToList())}, " +
                $"expected {Py.Repr(expected.Order(StringComparer.Ordinal).ToList())}");
            Ensure(
                resp.Count == expected.Length,
                $"begins_with(blob, {Py.Repr(prefix)}) Count was {Py.Repr(resp.Count)}, expected {Py.Repr(expected.Length)}");
            Ensure(
                resp.ScannedCount == Rows.Length,
                $"begins_with(blob, {Py.Repr(prefix)}) ScannedCount was {Py.Repr(resp.ScannedCount)}, expected " +
                $"the partition's {Rows.Length} items regardless of the filter");
        }
    }
}
