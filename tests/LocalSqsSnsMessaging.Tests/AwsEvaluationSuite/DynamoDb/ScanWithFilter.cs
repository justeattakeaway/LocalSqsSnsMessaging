// Translated from behavioral/dynamodb/check_scan_with_filter.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-with-filter</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanWithFilter
{
    private const string Check = "scan-with-filter";

    /// <summary>Scan with FilterExpression returns only matching items.</summary>
    [Test]
    [DisplayName("dynamodb.scan-with-filter")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        for (var i = 0; i < 6; i++)
        {
            await ddb.PutItemAsync(table, Item(("pk", S($"sf-{i}")), ("even", Bool(i % 2 == 0))));
        }

        var resp = await ddb.ScanAsync(new ScanRequest
        {
            TableName = table,
            FilterExpression = "even = :t",
            ExpressionAttributeValues = Item((":t", Bool(true))),
            ConsistentRead = true,
        });
        Ensure(resp.ScannedCount == 6, $"expected Scan to examine all 6 items before filtering, got ScannedCount={resp.ScannedCount}");
        Ensure(resp.Count == 3, $"expected 3 items matching even=True, got Count={resp.Count}");
        var returnedPks = new SortedSet<string>(resp.Items.Select(item => item["pk"].S), StringComparer.Ordinal);
        Ensure(returnedPks.SetEquals(["sf-0", "sf-2", "sf-4"]), $"expected only even-indexed items, got {Py.Repr(returnedPks)}");
        Ensure(resp.Items.All(item => item["even"].BOOL == true), $"a returned item had even != True: {Py.Repr(resp.Items)}");
    }
}
