// Translated from behavioral/dynamodb/check_scan_returns_all_items.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-returns-all-items</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanReturnsAllItems
{
    private const string Check = "scan-returns-all-items";

    /// <summary>Full table scan returns every item that was written, unaltered.</summary>
    [Test]
    [DisplayName("dynamodb.scan-returns-all-items")]
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
        var expected = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < 7; i++)
        {
            expected[$"scan-{i}"] = i;
        }

        foreach (var (pk, x) in expected)
        {
            await ddb.PutItemAsync(table, Item(("pk", S(pk)), ("x", N(x.ToString(CultureInfo.InvariantCulture)))));
        }

        var items = new List<Dictionary<string, AttributeValue>>();
        var request = new ScanRequest { TableName = table, ConsistentRead = true };
        while (true)
        {
            var resp = await ddb.ScanAsync(request);
            items.AddRange(resp.Items);
            if (resp.LastEvaluatedKey is null)
            {
                break;
            }

            request.ExclusiveStartKey = resp.LastEvaluatedKey;
        }

        Ensure(items.Count == expected.Count, $"expected {expected.Count} items, scan returned {items.Count}");
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            seen[item["pk"].S] = int.Parse(item["x"].N, CultureInfo.InvariantCulture);
        }

        Ensure(Py.Equal(seen, expected), $"scan item mismatch: expected {Py.Repr(expected)}, got {Py.Repr(seen)}");
    }
}
