// Translated from behavioral/dynamodb/check_lsi_scan_pagination_exclusive_start_key.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.lsi-scan-pagination-exclusive-start-key</summary>
[Category("AwsEvaluationSuite")]
public sealed class LsiScanPaginationExclusiveStartKey
{
    private static readonly string[] AllPks = ["p0", "p1", "p2", "p3", "p4"];

    /// <summary>
    /// Scanning an LSI pages the same way a Query does: LastEvaluatedKey resumes the scan and no
    /// item is returned twice or dropped.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.lsi-scan-pagination-exclusive-start-key")]
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
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("gpk", ScalarAttributeType.S),
                new AttributeDefinition("lsk", ScalarAttributeType.N),
            ],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = "gsi1",
                    KeySchema = [new KeySchemaElement("gpk", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = "lsi1",
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("lsk", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        for (var i = 0; i < 5; i++)
        {
            await ddb.PutItemAsync(table, Item(("pk", S($"p{i}")), ("sk", S("1")), ("lsk", N(i.ToString(CultureInfo.InvariantCulture)))));
        }

        var seen = new List<string>();
        Dictionary<string, AttributeValue>? page = null;
        var guard = 0;
        while (true)
        {
            var kw = new ScanRequest { TableName = table, IndexName = "lsi1", Limit = 2 };
            if (page is { Count: > 0 })
            {
                kw.ExclusiveStartKey = page;
            }

            var res = await ddb.ScanAsync(kw);
            seen.AddRange(res.Items.Select(i => i["pk"].S));
            page = res.LastEvaluatedKey;
            guard++;
            Ensure(guard < 20, "scan paging did not terminate");
            if (page is null || page.Count == 0)
            {
                break;
            }
        }

        var sorted = seen.Order(StringComparer.Ordinal).ToList();
        Ensure(sorted.SequenceEqual(AllPks), $"expected each item once, got {Py.Repr(sorted)}");
    }
}
