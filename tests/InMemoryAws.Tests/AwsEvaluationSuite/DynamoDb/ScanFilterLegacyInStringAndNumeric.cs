// Translated from behavioral/dynamodb/check_scan_filter_legacy_in_string_and_numeric.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-filter-legacy-in-string-and-numeric</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanFilterLegacyInStringAndNumeric
{
    private const string Check = "scan-filter-legacy-in-string-and-numeric";

    /// <summary>
    /// Five rows with an S column <c>status</c> and an N column <c>score</c>. The two IN lists exercised
    /// below pick disjoint row sets so the two assertions cannot be satisfied by the same bug.
    /// </summary>
    private static readonly (string Id, string Status, string Score)[] Rows =
    [
        ("r1", "active", "10"),
        ("r2", "inactive", "20.5"),
        ("r3", "pending", "30"),
        ("r4", "suspended", "40"),
        ("r5", "active", "50"),
    ];

    /// <summary>
    /// Legacy ScanFilter IN selects exactly the items whose S or N attribute equals one of the
    /// listed values.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-filter-legacy-in-string-and-numeric")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var name = await Seed(ctx);

        async Task<List<string>> ScanIds(Dictionary<string, Condition> scanFilter)
        {
            var resp = await ddb.ScanAsync(new ScanRequest { TableName = name, ScanFilter = scanFilter, ConsistentRead = true });
            return [.. resp.Items.Select(i => i["id"].S).Order(StringComparer.Ordinal)];
        }

        var inS = await ScanIds(new() { ["status"] = new Condition { AttributeValueList = [S("inactive"), S("suspended")], ComparisonOperator = ComparisonOperator.IN } });
        Ensure(inS.SequenceEqual(["r2", "r4"]), $"IN ('inactive','suspended') on status returned {Py.Repr(inS)}, expected ['r2', 'r4']");
        var inN = await ScanIds(new() { ["score"] = new Condition { AttributeValueList = [N("10"), N("50")], ComparisonOperator = ComparisonOperator.IN } });
        Ensure(inN.SequenceEqual(["r1", "r5"]), $"IN (10,50) on score returned {Py.Repr(inN)}, expected ['r1', 'r5']");
    }

    /// <summary>A fresh table under a minted name, seeded with the five rows via one BatchWriteItem.</summary>
    private static async Task<string> Seed(FidelityContext ctx)
    {
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName("sim2real-ddbin-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
            KeySchema = [new KeySchemaElement("id", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("id", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 250);
        var items = Rows.Select(r => new WriteRequest { PutRequest = new PutRequest { Item = Item(("id", S(r.Id)), ("status", S(r.Status)), ("score", N(r.Score))) } }).ToList();
        var resp = await ddb.BatchWriteItemAsync(new BatchWriteItemRequest { RequestItems = new() { [name] = items } });
        Ensure((resp.UnprocessedItems ?? []).Count == 0, $"seeding BatchWriteItem left {Py.Repr(resp.UnprocessedItems)} unprocessed");
        return name;
    }
}
