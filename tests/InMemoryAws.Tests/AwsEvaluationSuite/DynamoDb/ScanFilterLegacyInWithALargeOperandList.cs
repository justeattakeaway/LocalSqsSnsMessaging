// Translated from behavioral/dynamodb/check_scan_filter_legacy_in_with_a_large_operand_list.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-filter-legacy-in-with-a-large-operand-list</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanFilterLegacyInWithALargeOperandList
{
    private const string Check = "scan-filter-legacy-in-with-a-large-operand-list";

    /// <summary>
    /// Five seeded records: (id, timestamp, status, category, score, name, email). Every expected
    /// set is derived from these at run time, so a re-typed copy would silently weaken the check.
    /// </summary>
    private static readonly (string Id, string Stamp, string Status, string Category, string Score, string Name, string Email)[] Rows =
    [
        ("user-001", "1700000000", "active", "premium", "95", "John Doe", "john@example.com"),
        ("user-002", "1700000001", "inactive", "basic", "67", "Jane Smith", "jane@example.com"),
        ("user-003", "1700000002", "pending", "standard", "78", "Bob Wilson", "bob@example.com"),
        ("user-004", "1700000003", "suspended", "basic", "45", "Alice Brown", "alice@example.com"),
        ("user-005", "1700000004", "active", "premium", "92", "Charlie Davis", "charlie@example.com"),
    ];

    /// <summary>An IN operand list this long is far past the 100-operand cap FilterExpression IN enforces.</summary>
    private const int OperandCount = 27500;

    /// <summary>
    /// A legacy ScanFilter IN AttributeValueList of 27500 operands is accepted and matches exactly
    /// the items holding the one listed value they carry; a FilterExpression read counts the same items.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-filter-legacy-in-with-a-large-operand-list")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var name = await Seed(ctx, "biglist");
        var operands = Enumerable.Range(0, OperandCount - 1).Select(i => S($"junk{i:D5}")).ToList();
        operands.Add(S("active"));
        var res = await ddb.ScanAsync(new ScanRequest
        {
            TableName = name,
            Select = Select.COUNT,
            ConsistentRead = true,
            ScanFilter = new() { ["status"] = new Condition { AttributeValueList = operands, ComparisonOperator = ComparisonOperator.IN } },
        });
        var want = Rows.Count(r => r.Status == "active");
        Ensure(res.Count == want, $"the {OperandCount}-operand ScanFilter IN matched {Py.Repr(res.Count)}, expected {want}");
        Ensure(res.ScannedCount == Rows.Length, $"ScannedCount was {Py.Repr(res.ScannedCount)}, expected {Rows.Length}");
        var control = await ddb.ScanAsync(new ScanRequest
        {
            TableName = name,
            Select = Select.COUNT,
            ConsistentRead = true,
            FilterExpression = "#s = :v",
            ExpressionAttributeNames = Names(("#s", "status")),
            ExpressionAttributeValues = Item((":v", S("active"))),
        });
        Ensure(control.Count == want, $"the FilterExpression positive control counted {Py.Repr(control.Count)}, expected {want}");
    }

    /// <summary>One seed row as a typed DynamoDB item: score and the sort key are numbers, the rest strings.</summary>
    private static Dictionary<string, AttributeValue> RowItem((string Id, string Stamp, string Status, string Category, string Score, string Name, string Email) row) =>
        Item(
            ("id", S(row.Id)), ("timestamp", N(row.Stamp)), ("status", S(row.Status)),
            ("category", S(row.Category)), ("score", N(row.Score)), ("name", S(row.Name)),
            ("email", S(row.Email)));

    /// <summary>A fresh table under a minted name, seeded with ONE BatchWriteItem.</summary>
    private static async Task<string> Seed(FidelityContext ctx, string shortName)
    {
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName($"sim2real-ddbss-{shortName}-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
            KeySchema = [new KeySchemaElement("id", KeyType.HASH), new KeySchemaElement("timestamp", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("id", ScalarAttributeType.S), new AttributeDefinition("timestamp", ScalarAttributeType.N)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 250);
        var resp = await ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new() { [name] = [.. Rows.Select(r => new WriteRequest { PutRequest = new PutRequest { Item = RowItem(r) } })] },
        });
        Ensure((resp.UnprocessedItems ?? []).Count == 0, $"seeding BatchWriteItem left {Py.Repr(resp.UnprocessedItems)} unprocessed");
        return name;
    }
}
