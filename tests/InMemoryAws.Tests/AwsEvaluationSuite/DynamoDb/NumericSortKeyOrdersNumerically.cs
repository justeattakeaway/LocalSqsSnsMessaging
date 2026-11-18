// Translated from behavioral/dynamodb/check_numeric_sort_key_orders_numerically.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.numeric-sort-key-orders-numerically</summary>
[Category("AwsEvaluationSuite")]
public sealed class NumericSortKeyOrdersNumerically
{
    private const string CheckId = "numeric-sort-key-orders-numerically";

    // Chosen so the NUMERIC order and the LEXICOGRAPHIC order of the same strings differ:
    //   numeric:       -20, -3, 0, 2, 9.5, 10, 100, 1000
    //   lexicographic: '-20', '-3', '0', '10', '100', '1000', '2', '9.5'
    // Every literal is already in DynamoDB canonical form (no leading/trailing zeros), so the
    // stored representation round-trips byte-identically and the assertions can compare strings.
    private static readonly string[] SkNumericOrder = ["-20", "-3", "0", "2", "9.5", "10", "100", "1000"];
    private static readonly string[] InsertOrder = ["100", "-3", "1000", "9.5", "0", "10", "-20", "2"];
    private static readonly string[] Between = ["0", "2", "9.5", "10", "100"];
    private static readonly string[] Greater = ["9.5", "10", "100", "1000"];
    private static readonly string[] Negatives = ["-20", "-3"];

    /// <summary>
    /// An N-typed sort key collates numerically, not as strings: Query order both ways, BETWEEN, &gt;
    /// and &lt; follow numeric order with negative and fractional keys, and GetItem resolves 9.5 exactly.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.numeric-sort-key-orders-numerically")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.N)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        // Insert in an order that is neither numeric nor lexicographic, so a store that returns
        // insertion order also fails.
        foreach (var sk in InsertOrder)
        {
            await ddb.PutItemAsync(t, Item(("pk", S("n")), ("sk", N(sk))));
        }

        async Task<List<string>> Query(string condition, Dictionary<string, AttributeValue> values, bool? forward = null) =>
            [.. (await ddb.QueryAsync(new QueryRequest
            {
                TableName = t,
                KeyConditionExpression = condition,
                ExpressionAttributeValues = values,
                ConsistentRead = true,
                ScanIndexForward = forward,
            })).Items.Select(it => it["sk"].N)];

        var asc = await Query("pk = :p", Item((":p", S("n"))));
        Ensure(
            asc.SequenceEqual(SkNumericOrder),
            $"N sort key must ascend numerically {Py.Repr(SkNumericOrder)}, got {Py.Repr(asc)} (lexicographic order would be {Py.Repr(SkNumericOrder.Order(StringComparer.Ordinal).ToList())})");

        var desc = await Query("pk = :p", Item((":p", S("n"))), forward: false);
        Ensure(desc.SequenceEqual(SkNumericOrder.Reverse()), $"ScanIndexForward=False must reverse the NUMERIC order, got {Py.Repr(desc)}");

        var betweenSks = await Query("pk = :p AND sk BETWEEN :lo AND :hi", Item((":p", S("n")), (":lo", N("0")), (":hi", N("100"))));
        Ensure(
            betweenSks.SequenceEqual(Between),
            $"BETWEEN 0 AND 100 on an N sort key must select the numeric interval (inclusive of both bounds), got {Py.Repr(betweenSks)}");

        var greaterSks = await Query("pk = :p AND sk > :v", Item((":p", S("n")), (":v", N("9"))));
        Ensure(
            greaterSks.SequenceEqual(Greater),
            $"sk > 9 on an N sort key must select every numerically larger key, got {Py.Repr(greaterSks)} (a string comparison would yield only 9.5)");

        var negativeSks = await Query("pk = :p AND sk < :v", Item((":p", S("n")), (":v", N("0"))));
        Ensure(
            negativeSks.SequenceEqual(Negatives),
            $"negative N sort keys must ascend -20 then -3, got {Py.Repr(negativeSks)} (a string comparison would yield -20 after -3)");

        // A single point read on an N key must resolve through the same collation.
        var got = await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("n")), ("sk", N("9.5"))), ConsistentRead = true });
        Ensure(
            Py.Equal(got.Item?.GetValueOrDefault("sk"), N("9.5")),
            $"GetItem on an N sort key must resolve the fractional key exactly, got {Py.Repr(got.Item)}");
    }
}
