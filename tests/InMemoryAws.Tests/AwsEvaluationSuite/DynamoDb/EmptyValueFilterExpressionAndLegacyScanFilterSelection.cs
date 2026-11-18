// Translated from behavioral/dynamodb/check_empty_value_filter_expression_and_legacy_scan_filter_selection.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.empty-value-filter-expression-and-legacy-scan-filter-selection</summary>
[Category("AwsEvaluationSuite")]
public sealed class EmptyValueFilterExpressionAndLegacyScanFilterSelection
{
    private const string CheckId = "empty-value-filter-expression-and-legacy-scan-filter-selection";

    /// <summary>
    /// An empty String/Binary non-key attribute is stored and selectable: FilterExpression
    /// '= ""', begins_with(a, ''), size(a) = 0, and legacy ScanFilter EQ on an empty value each
    /// select exactly the items carrying that empty value, identically on the table and on a GSI.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.empty-value-filter-expression-and-legacy-scan-filter-selection")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var (table, index) = await CreateTable(ctx);

        var items = new Dictionary<string, Dictionary<string, AttributeValue>>(StringComparer.Ordinal)
        {
            ["both-empty"] = Item(("pk", S("both-empty")), ("gpk", S("g1")), ("s", S("")), ("b", B(""))),
            ["s-empty"] = Item(("pk", S("s-empty")), ("gpk", S("g2")), ("s", S("")), ("b", B("z"))),
            ["b-empty"] = Item(("pk", S("b-empty")), ("gpk", S("g3")), ("s", S("w")), ("b", B(""))),
            ["neither-empty"] = Item(("pk", S("neither-empty")), ("gpk", S("g4")), ("s", S("x")), ("b", B("y"))),
        };
        var resp = await ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new() { [table] = items.Values.Select(item => new WriteRequest { PutRequest = new PutRequest { Item = item } }).ToList() },
        });
        Ensure(resp.UnprocessedItems is { Count: 0 }, $"seeding left {Py.Repr(resp.UnprocessedItems)} unprocessed");

        var sEmptyIds = items.Where(kv => kv.Value["s"].S == "").Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
        var bEmptyIds = items.Where(kv => Bytes(kv.Value["b"].B).Length == 0).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
        var allIds = items.Keys.ToHashSet(StringComparer.Ordinal);

        // GSI scans are eventually consistent: poll until the index has caught up with the seed,
        // bounded by the check's own remaining time budget rather than a fixed sleep.
        var clock = FidelityTime.StartNew();
        var budget = TimeSpan.FromSeconds(Math.Max(1.0, Math.Min(60.0, ctx.TimeLeftSeconds - 10.0)));
        var seen = await Scan(ddb, table, index, new ScanRequest());
        while (!seen.SetEquals(allIds) && clock.Elapsed < budget)
        {
            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
            seen = await Scan(ddb, table, index, new ScanRequest());
        }

        Ensure(seen.SetEquals(allIds), $"gsi {Py.Repr(index)} never converged to {Repr(allIds)}, last saw {Repr(seen)}");

        foreach (var indexName in new[] { null, index })
        {
            var eq = await Scan(ddb, table, indexName, new ScanRequest
            {
                FilterExpression = "s = :e",
                ExpressionAttributeValues = Item((":e", S(""))),
            });
            Ensure(eq.SetEquals(sEmptyIds), $"s = '' on index={Py.Repr(indexName)} selected {Repr(eq)}, expected {Repr(sEmptyIds)}");

            var bw = await Scan(ddb, table, indexName, new ScanRequest
            {
                FilterExpression = "begins_with(s, :e)",
                ExpressionAttributeValues = Item((":e", S(""))),
            });
            Ensure(bw.SetEquals(allIds), $"begins_with(s, '') on index={Py.Repr(indexName)} selected {Repr(bw)}, expected {Repr(allIds)}");

            var sz = await Scan(ddb, table, indexName, new ScanRequest
            {
                FilterExpression = "size(b) = :z",
                ExpressionAttributeValues = Item((":z", N("0"))),
            });
            Ensure(sz.SetEquals(bEmptyIds), $"size(b) = 0 on index={Py.Repr(indexName)} selected {Repr(sz)}, expected {Repr(bEmptyIds)}");

            var legacy = await Scan(ddb, table, indexName, new ScanRequest
            {
                ScanFilter = new Dictionary<string, Condition>(StringComparer.Ordinal)
                {
                    ["b"] = new Condition { ComparisonOperator = ComparisonOperator.EQ, AttributeValueList = [B("")] },
                },
            });
            Ensure(
                legacy.SetEquals(bEmptyIds),
                $"legacy ScanFilter EQ b='' on index={Py.Repr(indexName)} selected {Repr(legacy)}, expected {Repr(bEmptyIds)}");
        }
    }

    private static string Repr(HashSet<string> ids) => $"frozenset({{{string.Join(", ", ids.Order(StringComparer.Ordinal).Select(Py.Repr))}}})";

    /// <summary>Every page's `pk` values from a strongly consistent Scan, filtered as given.</summary>
    private static async Task<HashSet<string>> Scan(IAmazonDynamoDB ddb, string table, string? indexName, ScanRequest request)
    {
        request.TableName = table;
        if (indexName is not null)
        {
            request.IndexName = indexName;
        }
        else
        {
            request.ConsistentRead = true;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        Dictionary<string, AttributeValue>? start = null;
        while (true)
        {
            request.ExclusiveStartKey = start is { Count: > 0 } ? start : null;
            var page = await ddb.ScanAsync(request);
            foreach (var i in page.Items ?? [])
            {
                ids.Add(i["pk"].S);
            }

            start = page.LastEvaluatedKey;
            if (start is not { Count: > 0 })
            {
                return ids;
            }
        }
    }

    /// <summary>A table with hash key pk (S) and a GSI by gpk (S), projecting all attributes.</summary>
    private static async Task<(string Name, string Index)> CreateTable(FidelityContext ctx)
    {
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName("sim2real-behav-");
        const string index = "by-gpk";
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("gpk", ScalarAttributeType.S)],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = index,
                    KeySchema = [new KeySchemaElement("gpk", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 30);
        return (name, index);
    }
}
