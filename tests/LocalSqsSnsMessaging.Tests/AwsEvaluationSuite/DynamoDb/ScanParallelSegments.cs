// Translated from behavioral/dynamodb/check_scan_parallel_segments.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-parallel-segments</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanParallelSegments
{
    /// <summary>Parallel scan with TotalSegments/Segment partitions the full table disjointly.</summary>
    [Test]
    [DisplayName("dynamodb.scan-parallel-segments")]
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
            Tags = [new Tag { Key = "sim2real", Value = "scan-parallel-segments" }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        for (var i = 0; i < 20; i++)
        {
            await ddb.PutItemAsync(table, Item(("pk", S($"par-{i:D3}"))));
        }

        const int totalSegments = 4;
        var perSegmentPks = new List<HashSet<string>>();
        for (var seg = 0; seg < totalSegments; seg++)
        {
            var segPks = new HashSet<string>(StringComparer.Ordinal);
            var request = new ScanRequest { TableName = table, TotalSegments = totalSegments, Segment = seg, ConsistentRead = true };
            while (true)
            {
                var resp = await ddb.ScanAsync(request);
                foreach (var item in resp.Items)
                {
                    segPks.Add(item["pk"].S);
                }

                if (resp.LastEvaluatedKey is null)
                {
                    break;
                }

                request.ExclusiveStartKey = resp.LastEvaluatedKey;
            }

            perSegmentPks.Add(segPks);
        }

        var allPks = perSegmentPks.SelectMany(s => s).ToHashSet(StringComparer.Ordinal);
        Ensure(allPks.Count == 20, $"expected 20 unique items across all segments, got {allPks.Count}");
        var totalReturned = perSegmentPks.Sum(s => s.Count);
        Ensure(
            totalReturned == allPks.Count,
            $"segments must partition the table disjointly: {totalReturned} items returned across " +
            $"{totalSegments} segments but only {allPks.Count} unique pks (overlap of " +
            $"{totalReturned - allPks.Count}) — a target that ignores Segment/TotalSegments and " +
            "returns the whole table on every call would still pass the count-only assertion");
    }
}
