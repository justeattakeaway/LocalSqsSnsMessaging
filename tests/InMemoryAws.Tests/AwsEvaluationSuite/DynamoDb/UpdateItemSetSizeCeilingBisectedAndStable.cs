// Translated from behavioral/dynamodb/check_update_item_set_size_ceiling_bisected_and_stable.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-set-size-ceiling-bisected-and-stable</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemSetSizeCeilingBisectedAndStable
{
    private const string Check = "update-item-set-size-ceiling-bisected-and-stable";
    private const string ErrValidation = "ValidationException";
    private const string ShortKey = "k";
    private const string LongKey = "a_long_descriptive_partition_key_attribute_name_used_for_the_item_size_ceiling_probe";
    private const int UpperBound = 410 * 1024;

    /// <summary>
    /// The UpdateItem SET size ceiling is a reproducible byte boundary that shifts with
    /// the primary key attribute's own name length and with the number of SET clauses, and
    /// that stays put when the same boundary is measured again.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ServiceQuotas.html#limits-items
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-set-size-ceiling-bisected-and-stable")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;

        var shortTable = FidelityContext.UniqueName("sim2real-behav-ceil-short-");
        var longTable = FidelityContext.UniqueName("sim2real-behav-ceil-long-");
        foreach (var (table, keyName) in new[] { (shortTable, ShortKey), (longTable, LongKey) })
        {
            await ddb.CreateTableAsync(new CreateTableRequest
            {
                TableName = table,
                KeySchema = [new KeySchemaElement(keyName, KeyType.HASH)],
                AttributeDefinitions = [new AttributeDefinition(keyName, ScalarAttributeType.S)],
                BillingMode = BillingMode.PAY_PER_REQUEST,
                Tags = [new Tag { Key = "sim2real", Value = Check }],
            });
            ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        }

        await ddb.WaitForTableExists(shortTable, delaySeconds: 2, maxAttempts: 30);
        await ddb.WaitForTableExists(longTable, delaySeconds: 2, maxAttempts: 30);

        var combos = new (string Label, string Table, string KeyName, int Clauses)[]
        {
            ("short-1clause", shortTable, ShortKey, 1),
            ("short-2clause", shortTable, ShortKey, 2),
            ("long-1clause", longTable, LongKey, 1),
            ("long-2clause", longTable, LongKey, 2),
        };

        // Every pk value used anywhere in this check is a fixed-width 8-digit counter, so the
        // key *value*'s own contribution to item size never confounds a boundary comparison.
        using var counter = Enumerable.Range(1, 99999).Select(i => i.ToString("D8", CultureInfo.InvariantCulture)).GetEnumerator();
        string Next()
        {
            counter.MoveNext();
            return counter.Current;
        }

        var boundaries = new Dictionary<string, (int Lo, int Hi)>(StringComparer.Ordinal);
        foreach (var (label, table, keyName, clauses) in combos)
        {
            var (lo, hi) = await Bisect(ddb, table, keyName, clauses, Next);
            Ensure(hi == lo + 1, $"{label}: bisection did not converge to adjacent bytes, got lo={lo} hi={hi}");
            var floor = 300 * 1024 / clauses;
            Ensure(
                lo > floor,
                $"{label}: accepted boundary is implausibly small ({lo} bytes, floor {floor}); the " +
                "account may not be accepting ordinary UpdateItem calls at all");
            boundaries[label] = (lo, hi);
        }

        // Re-measure each boundary twice more: the accept/reject answer at the SAME byte count
        // must repeat, both at the largest accepted size and at the smallest refused one.
        for (var runIdx = 0; runIdx < 2; runIdx++)
        {
            foreach (var (label, table, keyName, clauses) in combos)
            {
                var (lo, hi) = boundaries[label];
                Ensure(
                    await Accepted(ddb, table, keyName, Next(), clauses, lo),
                    $"{label} re-measurement {runIdx}: the previously-accepted size {lo} is now refused");
                Ensure(
                    !await Accepted(ddb, table, keyName, Next(), clauses, hi),
                    $"{label} re-measurement {runIdx}: the previously-refused size {hi} is now accepted");
            }
        }

        // The long key name's own bytes count toward the 400 KB limit, so its ceiling is
        // strictly lower than the short key's, for both clause counts.
        Ensure(
            boundaries["long-1clause"].Lo < boundaries["short-1clause"].Lo,
            $"long-key 1-clause boundary {boundaries["long-1clause"].Lo} is not below " +
            $"short-key 1-clause boundary {boundaries["short-1clause"].Lo}");
        Ensure(
            boundaries["long-2clause"].Lo < boundaries["short-2clause"].Lo,
            $"long-key 2-clause boundary {boundaries["long-2clause"].Lo} is not below " +
            $"short-key 2-clause boundary {boundaries["short-2clause"].Lo}");

        // Splitting the same per-value growth across two SET clauses buys no extra room: the
        // limit is on the item's aggregate size, so the 2-clause per-value ceiling is roughly
        // half the 1-clause ceiling, not an independent 400 KB budget per clause.
        Ensure(
            boundaries["short-2clause"].Lo < boundaries["short-1clause"].Lo / 1.5,
            $"short-key 2-clause boundary {boundaries["short-2clause"].Lo} is not roughly half of " +
            $"the 1-clause boundary {boundaries["short-1clause"].Lo}; two SET clauses may not be " +
            "sharing one aggregate limit");
    }

    /// <summary>True if an UpdateItem SET of <paramref name="clauses"/> attribute(s), each an n-byte string, is accepted.</summary>
    private static async Task<bool> Accepted(IAmazonDynamoDB ddb, string table, string keyName, string pk, int clauses, int n)
    {
        await ddb.PutItemAsync(table, Item((keyName, S(pk))));
        string expr;
        Dictionary<string, AttributeValue> values;
        if (clauses == 1)
        {
            expr = "SET v = :v";
            values = Item((":v", S(new string('x', n))));
        }
        else
        {
            expr = "SET v1 = :v1, v2 = :v2";
            values = Item((":v1", S(new string('x', n))), (":v2", S(new string('y', n))));
        }

        try
        {
            await ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = Item((keyName, S(pk))),
                UpdateExpression = expr,
                ExpressionAttributeValues = values,
            });
            return true;
        }
        catch (AmazonDynamoDBException ex) when (ex.ErrorCode == ErrValidation)
        {
            return false;
        }
    }

    /// <summary>(largest accepted n, smallest refused n) for a SET of <paramref name="clauses"/> n-byte value(s).</summary>
    private static async Task<(int Lo, int Hi)> Bisect(IAmazonDynamoDB ddb, string table, string keyName, int clauses, Func<string> pkSource)
    {
        int lo = 1, hi = UpperBound;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            if (await Accepted(ddb, table, keyName, pkSource(), clauses, mid))
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        return (lo, hi);
    }
}
