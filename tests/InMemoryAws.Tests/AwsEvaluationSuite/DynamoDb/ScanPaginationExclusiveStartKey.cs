// Translated from behavioral/dynamodb/check_scan_pagination_exclusive_start_key.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-pagination-exclusive-start-key</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanPaginationExclusiveStartKey
{
    /// <summary>Scan pagination with ExclusiveStartKey honors Limit and yields every item exactly once.</summary>
    [Test]
    [DisplayName("dynamodb.scan-pagination-exclusive-start-key")]
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
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        var expectedPks = new SortedSet<string>(Enumerable.Range(0, 12).Select(i => $"sp-{i:D3}"), StringComparer.Ordinal);
        foreach (var pk in expectedPks)
        {
            await ddb.PutItemAsync(table, Item(("pk", S(pk))));
        }

        var allItems = new List<Dictionary<string, AttributeValue>>();
        var pageCount = 0;
        var request = new ScanRequest { TableName = table, Limit = 4, ConsistentRead = true };
        while (true)
        {
            var resp = await ddb.ScanAsync(request);
            pageCount++;
            Ensure(resp.Items.Count <= 4, $"page {pageCount} returned {resp.Items.Count} items, exceeding Limit=4");
            allItems.AddRange(resp.Items);
            if (resp.LastEvaluatedKey is null)
            {
                break;
            }

            request.ExclusiveStartKey = resp.LastEvaluatedKey;
        }

        Ensure(pageCount > 1, $"expected pagination across multiple pages for 12 items under Limit=4, got {pageCount} page(s)");
        Ensure(allItems.Count == 12, $"expected 12 total items across all pages, got {allItems.Count}");
        var pks = new SortedSet<string>(allItems.Select(item => item["pk"].S), StringComparer.Ordinal);
        Ensure(pks.SetEquals(expectedPks), $"expected pks {Py.Repr(expectedPks)}, got {Py.Repr(pks)}");
    }
}
