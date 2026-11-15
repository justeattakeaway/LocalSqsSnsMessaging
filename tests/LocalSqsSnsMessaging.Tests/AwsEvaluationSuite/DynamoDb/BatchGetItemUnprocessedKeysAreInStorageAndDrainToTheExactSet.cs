// Translated from behavioral/dynamodb/check_batch_get_item_unprocessed_keys_are_in_storage_and_drain_to_the_exact_set.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-get-item-unprocessed-keys-are-in-storage-and-drain-to-the-exact-set</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchGetItemUnprocessedKeysAreInStorageAndDrainToTheExactSet
{
    private const string Check = "batch-get-item-unprocessed-keys-are-in-storage-and-drain-to-the-exact-set";
    private const int NItems = 60;
    private const int PayloadBytes = 380 * 1024;

    /// <summary>
    /// A BatchGetItem of 60 large items comes back partial: UnprocessedKeys is disjoint from the
    /// returned items and names keys present in storage, and draining it yields exactly the 60, once.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_BatchGetItem.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batch-get-item-unprocessed-keys-are-in-storage-and-drain-to-the-exact-set")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-batchget-");
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

        var payload = new string('z', PayloadBytes);
        var allKeys = Enumerable.Range(0, NItems).Select(i => $"k{i.ToString("00", CultureInfo.InvariantCulture)}").ToList();
        foreach (var key in allKeys)
        {
            await ddb.PutItemAsync(table, Item(("pk", S(key)), ("pad", S(payload))));
        }

        var first = await ddb.BatchGetItemAsync(new BatchGetItemRequest
        {
            RequestItems = new()
            {
                [table] = new KeysAndAttributes { Keys = [.. allKeys.Select(k => Item(("pk", S(k))))], ConsistentRead = true },
            },
        });
        var returned = first.Responses[table].Select(i => i["pk"].S).ToList();
        var unprocessed = (first.UnprocessedKeys?.GetValueOrDefault(table)?.Keys ?? []).Select(k => k["pk"].S).ToList();

        Ensure(
            returned.Count > 0 && unprocessed.Count > 0,
            $"{Py.Repr(NItems)} items of {Py.Repr(PayloadBytes)} bytes cannot fit one BatchGetItem response, so " +
            $"the call must come back partial: it returned {Py.Repr(returned.Count)} items and named " +
            $"{Py.Repr(unprocessed.Count)} unprocessed keys");
        var overlap = returned.Intersect(unprocessed, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        Ensure(overlap.Count == 0, $"a key was both returned and reported unprocessed: {Py.Repr(overlap)}");
        Ensure(
            first.Responses[table].All(i => i["pad"].S.Length == PayloadBytes),
            "the service truncated payloads to fit the response instead of deferring whole items: " +
            $"{Py.Repr(first.Responses[table].Select(i => i["pad"].S.Length).Distinct().Order().ToList())}");

        // UnprocessedKeys is about the response budget, not about existence: the deferred key is in
        // storage right now, which is what makes its absence above a deferral rather than a loss.
        var probe = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(unprocessed[0]))), ConsistentRead = true });
        Ensure(
            probe.Item?.GetValueOrDefault("pk")?.S == unprocessed[0],
            "a key reported unprocessed is not in the table at all, so the report was about " +
            $"existence rather than the response budget: {Py.Repr(unprocessed[0])} -> {Py.Repr(probe.Item)}");
        Ensure(
            probe.Item["pad"].S.Length == PayloadBytes,
            $"the deferred item is present but truncated: {Py.Repr(probe.Item["pad"].S.Length)} bytes");

        var seen = new List<string>(returned);
        var pending = first.UnprocessedKeys ?? [];
        var rounds = 0;
        while (pending.Count > 0)
        {
            rounds++;
            Ensure(
                rounds <= 30,
                $"the drain loop did not converge in {Py.Repr(rounds)} rounds; {Py.Repr(seen.Count)} of " +
                $"{Py.Repr(NItems)} seen");
            var page = await ddb.BatchGetItemAsync(new BatchGetItemRequest { RequestItems = pending });
            seen.AddRange(page.Responses[table].Select(i => i["pk"].S));
            pending = (page.UnprocessedKeys ?? [])
                .Where(kv => kv.Value.Keys is { Count: > 0 })
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        }

        Ensure(
            seen.Order(StringComparer.Ordinal).SequenceEqual(allKeys.Order(StringComparer.Ordinal)),
            "draining UnprocessedKeys did not yield exactly the requested set: symmetric difference " +
            $"{Py.Repr(SymmetricDifference(seen, allKeys))}");
        Ensure(
            seen.Count == NItems,
            $"the drain returned an item more than once: {Py.Repr(seen.Count - seen.Distinct(StringComparer.Ordinal).Count())} duplicates in " +
            $"{Py.Repr(seen.Count)} results");
    }

    private static List<string> SymmetricDifference(IEnumerable<string> a, IEnumerable<string> b)
    {
        var set = a.ToHashSet(StringComparer.Ordinal);
        set.SymmetricExceptWith(b);
        return [.. set.Order(StringComparer.Ordinal)];
    }
}
