// Translated from behavioral/dynamodb/check_e2e_paginated_export.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.e2e-paginated-export</summary>
[Category("AwsEvaluationSuite")]
public sealed class E2ePaginatedExport
{
    private const string CheckId = "e2e-paginated-export";

    /// <summary>Paginated scan: write 30 items, scan with Limit=10, collect all pages, no duplicates.</summary>
    [Test]
    [DisplayName("dynamodb.e2e-paginated-export")]
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
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        for (var batchStart = 0; batchStart < 30; batchStart += 25)
        {
            var batchEnd = Math.Min(batchStart + 25, 30);
            var requests = Enumerable.Range(batchStart, batchEnd - batchStart)
                .Select(i => new WriteRequest
                {
                    PutRequest = new PutRequest
                    {
                        Item = Item(
                            ("pk", S(string.Create(CultureInfo.InvariantCulture, $"export-{i:D3}"))),
                            ("seq", N(i.ToString(CultureInfo.InvariantCulture)))),
                    },
                })
                .ToList();
            await ddb.BatchWriteItemAsync(new BatchWriteItemRequest { RequestItems = new() { [table] = requests } });
        }

        var allItems = new List<Dictionary<string, AttributeValue>>();
        var request = new ScanRequest { TableName = table, Limit = 10, ConsistentRead = true };
        var pages = 0;
        while (true)
        {
            var resp = await ddb.ScanAsync(request);
            Ensure(resp.Items.Count <= 10, $"Page had {resp.Items.Count} items, Limit=10 was not honoured");
            allItems.AddRange(resp.Items);
            pages++;
            if (resp.LastEvaluatedKey is null)
            {
                break;
            }

            request.ExclusiveStartKey = resp.LastEvaluatedKey;
        }

        Ensure(pages >= 3, $"30 items at Limit=10 requires at least 3 pages, got {pages} -- the service ignored Limit");
        var pks = allItems.Select(item => item["pk"].S).ToList();
        var unique = pks.ToHashSet(StringComparer.Ordinal);
        Ensure(pks.Count == 30, $"Expected 30 items, got {pks.Count}");
        Ensure(unique.Count == 30, $"Duplicate items found: {pks.Count} total, {unique.Count} unique");
        var expected = Enumerable.Range(0, 30).Select(i => string.Create(CultureInfo.InvariantCulture, $"export-{i:D3}")).ToHashSet(StringComparer.Ordinal);
        Ensure(
            unique.SetEquals(expected),
            $"Expected pks {Py.Repr(expected.Order(StringComparer.Ordinal).ToList())}, got {Py.Repr(unique.Order(StringComparer.Ordinal).ToList())}");
    }
}
