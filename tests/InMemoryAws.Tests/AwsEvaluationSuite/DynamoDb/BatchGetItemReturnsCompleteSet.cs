// Translated from behavioral/dynamodb/check_batch_get_item_returns_complete_set.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-get-item-returns-complete-set</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchGetItemReturnsCompleteSet
{
    /// <summary>
    /// A BatchGetItem at the 100-key limit returns every requested item exactly once with its attributes
    /// and an empty UnprocessedKeys: the key-count boundary itself does not cause a partial response.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batch-get-item-returns-complete-set")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx, ddb);

        var expected = Enumerable.Range(0, 100).ToDictionary(i => $"key-{i}", i => i.ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal);
        foreach (var (pk, val) in expected)
        {
            await ddb.PutItemAsync(table, Item(("pk", S(pk)), ("val", N(val))));
        }

        var resp = await ddb.BatchGetItemAsync(new BatchGetItemRequest
        {
            RequestItems = new()
            {
                [table] = new KeysAndAttributes { Keys = [.. expected.Keys.Select(pk => Item(("pk", S(pk))))], ConsistentRead = true },
            },
        });

        var unprocessed = resp.UnprocessedKeys ?? [];
        Ensure(unprocessed.Count == 0, $"expected no unprocessed keys for 100 tiny items, got {Py.Repr(unprocessed.Keys)}");

        var items = resp.Responses[table];
        Ensure(items.Count == expected.Count, $"expected all {expected.Count} items back in one response, got {items.Count}");

        var seenPks = items.Select(item => item["pk"].S).ToList();
        Ensure(
            seenPks.Count == seenPks.Distinct(StringComparer.Ordinal).Count(),
            $"BatchGetItem returned a duplicate item at the 100-key boundary: {Py.Repr(seenPks)}");

        var actual = items.ToDictionary(item => item["pk"].S, item => item["val"].N, StringComparer.Ordinal);
        Ensure(
            Py.Equal(actual, expected),
            "complete set mismatch at the 100-key boundary; symmetric difference " +
            $"{Py.Repr(SymmetricDifference(actual.Keys, expected.Keys))}");
    }

    private static async Task<string> CreateTable(FidelityContext ctx, IAmazonDynamoDB ddb)
    {
        var name = FidelityContext.UniqueName("sim2real-behav-bgicomplete-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 250);
        return name;
    }

    /// <summary><c>sorted(set(a) ^ set(b))</c>.</summary>
    private static List<string> SymmetricDifference(IEnumerable<string> a, IEnumerable<string> b)
    {
        var set = a.ToHashSet(StringComparer.Ordinal);
        set.SymmetricExceptWith(b);
        return [.. set.Order(StringComparer.Ordinal)];
    }
}
