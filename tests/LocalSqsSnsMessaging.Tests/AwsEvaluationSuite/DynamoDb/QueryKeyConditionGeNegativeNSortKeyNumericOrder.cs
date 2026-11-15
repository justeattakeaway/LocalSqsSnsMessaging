// Translated from behavioral/dynamodb/check_query_key_condition_ge_negative_n_sort_key_numeric_order.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-key-condition-ge-negative-n-sort-key-numeric-order</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryKeyConditionGeNegativeNSortKeyNumericOrder
{
    private const string Check = "query-key-condition-ge-negative-n-sort-key-numeric-order";

    // sk values written out of numeric order; GE_THRESHOLD excludes the first two numerically.
    private static readonly string[] Sks = ["7", "-15", "0", "-10", "-3"];
    private const string GeThreshold = "-10";
    private static readonly List<string> ExpectedAsc = ["-10", "-3", "0", "7"];
    private const string ExcludedSk = "-15";

    /// <summary>
    /// Query's sk >= :v on an N sort key selects and orders by numeric value, including a
    /// negative threshold's own boundary and excluding a more-negative key; ScanIndexForward=false
    /// reverses that order, and the excluded key is still present in the table.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-key-condition-ge-negative-n-sort-key-numeric-order")]
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
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.N)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        foreach (var sk in Sks)
        {
            await ddb.PutItemAsync(table, Item(("pk", S("row")), ("sk", N(sk))));
        }

        var ascending = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            ConsistentRead = true,
            KeyConditionExpression = "pk = :p AND sk >= :v",
            ExpressionAttributeValues = Item((":p", S("row")), (":v", N(GeThreshold))),
        });
        var ascSks = ascending.Items.Select(item => item["sk"].N).ToList();
        Ensure(
            ascSks.SequenceEqual(ExpectedAsc),
            $"sk >= {GeThreshold} on an N sort key must select {Py.Repr(ExpectedAsc)} in ascending numeric order, got {Py.Repr(ascSks)}");
        Ensure(ascending.Count == ExpectedAsc.Count, $"expected Count={ExpectedAsc.Count}, got {Py.Repr(ascending.Count)}");

        var descending = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            ConsistentRead = true,
            ScanIndexForward = false,
            KeyConditionExpression = "pk = :p AND sk >= :v",
            ExpressionAttributeValues = Item((":p", S("row")), (":v", N(GeThreshold))),
        });
        var descSks = descending.Items.Select(item => item["sk"].N).ToList();
        List<string> reversedAsc = [.. Enumerable.Reverse(ExpectedAsc)];
        Ensure(
            descSks.SequenceEqual(reversedAsc),
            $"ScanIndexForward=False must reverse the order to {Py.Repr(reversedAsc)}, got {Py.Repr(descSks)}");

        var excluded = await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("row")), ("sk", N(ExcludedSk))),
            ConsistentRead = true,
        });
        Ensure(
            Py.Equal(excluded.Item?.GetValueOrDefault("sk"), N(ExcludedSk)),
            $"the key below the GE threshold must still exist in the table, got {Py.Repr(excluded.Item)}");
    }
}
