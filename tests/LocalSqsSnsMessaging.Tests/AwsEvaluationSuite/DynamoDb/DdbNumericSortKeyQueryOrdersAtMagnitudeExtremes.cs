// Translated from behavioral/dynamodb/check_ddb_numeric_sort_key_query_orders_at_magnitude_extremes.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.ddb-numeric-sort-key-query-orders-at-magnitude-extremes</summary>
[Category("AwsEvaluationSuite")]
public sealed class DdbNumericSortKeyQueryOrdersAtMagnitudeExtremes
{
    private const string Check = "ddb-numeric-sort-key-query-orders-at-magnitude-extremes";

    // Four sort-key values spanning the documented N range from its negative ceiling down past
    // zero to its smallest positive magnitude, then up to a large ordinary integer, IN ASCENDING
    // numeric order:
    //   -9.9999999999999999999999999999999999999E+125 < 0 < 1E-130 < 99999999999999999999999999999999999999
    // A store that orders lexicographically on the stored string, or treats the extremes as equal
    // to zero, disagrees with this sequence.
    private static readonly (string Label, string Sk)[] Rows =
    [
        ("neg-ceiling", "-9.9999999999999999999999999999999999999E+125"),
        ("zero", "0"),
        ("tiny-positive", "1E-130"),
        ("large-integer", "99999999999999999999999999999999999999"),
    ];

    /// <summary>
    /// Query orders an N sort key by numeric value at the documented magnitude extremes: the
    /// negative ceiling sorts first, then zero, then the smallest positive magnitude, then an
    /// ordinary large integer; ScanIndexForward=false reverses that order.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.ddb-numeric-sort-key-query-orders-at-magnitude-extremes")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.N),
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        // Write in an order that is neither ascending nor descending, so a store that merely
        // echoes insertion order also fails.
        foreach (var (label, sk) in new[] { Rows[2], Rows[0], Rows[3], Rows[1] })
        {
            await ddb.PutItemAsync(table, Item(("pk", S("mag")), ("sk", N(sk)), ("label", S(label))));
        }

        // The canonical stored string for each sk, read back independently of Query order: the
        // order assertion below compares against these, never against a numeric value.
        var canonical = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (label, sk) in Rows)
        {
            var item = (await ddb.GetItemAsync(new GetItemRequest
            {
                TableName = table,
                Key = Item(("pk", S("mag")), ("sk", N(sk))),
                ConsistentRead = true,
            })).Item;
            Ensure(
                Py.Equal(item["label"], S(label)),
                $"GetItem for {Py.Repr(label)} resolved the wrong item, got label {Py.Repr(item["label"])}");
            canonical[label] = item["sk"].N;
        }

        var expectedAsc = Rows.Select(r => canonical[r.Label]).ToList();
        var ascending = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            ConsistentRead = true,
            ScanIndexForward = true,
            KeyConditionExpression = "pk = :p",
            ExpressionAttributeValues = Item((":p", S("mag"))),
        });
        var ascSks = ascending.Items.Select(it => it["sk"].N).ToList();
        Ensure(
            ascSks.SequenceEqual(expectedAsc),
            $"ascending Query must order the N sort key {Py.Repr(expectedAsc)} from the negative ceiling " +
            $"up through zero and the smallest positive magnitude to the large integer, got {Py.Repr(ascSks)}");

        var descending = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            ConsistentRead = true,
            ScanIndexForward = false,
            KeyConditionExpression = "pk = :p",
            ExpressionAttributeValues = Item((":p", S("mag"))),
        });
        var descSks = descending.Items.Select(it => it["sk"].N).ToList();
        var reversed = Enumerable.Reverse(expectedAsc).ToList();
        Ensure(
            descSks.SequenceEqual(reversed),
            $"ScanIndexForward=False must reverse the ascending order to {Py.Repr(reversed)}, " +
            $"got {Py.Repr(descSks)}");
    }
}
