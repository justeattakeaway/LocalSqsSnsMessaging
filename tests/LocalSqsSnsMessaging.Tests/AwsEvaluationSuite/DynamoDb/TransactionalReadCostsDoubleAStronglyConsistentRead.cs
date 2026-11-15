// Translated from behavioral/dynamodb/check_transactional_read_costs_double_a_strongly_consistent_read.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transactional-read-costs-double-a-strongly-consistent-read</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactionalReadCostsDoubleAStronglyConsistentRead
{
    private const string Check = "transactional-read-costs-double-a-strongly-consistent-read";
    private const int Items = 4;
    private static readonly string Payload = new('z', 900); // well under the 4 KB read-unit boundary

    private static Dictionary<string, object?> CapacityDict(ConsumedCapacity c)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (c.TableName is not null)
        {
            d["TableName"] = c.TableName;
        }

        if (c.CapacityUnits is not null)
        {
            d["CapacityUnits"] = c.CapacityUnits;
        }

        if (c.ReadCapacityUnits is not null)
        {
            d["ReadCapacityUnits"] = c.ReadCapacityUnits;
        }

        if (c.WriteCapacityUnits is not null)
        {
            d["WriteCapacityUnits"] = c.WriteCapacityUnits;
        }

        return d;
    }

    /// <summary>
    /// Sum CapacityUnits across a ConsumedCapacity list, asserting each entry names the table.
    /// Hard-subscripted: a <c>.get(..., [])</c> would supply exactly the value this check detects.
    /// </summary>
    private static double Total(List<ConsumedCapacity>? consumed, string table)
    {
        Ensure(
            consumed is not null && consumed.Count > 0,
            "BatchGetItem/TransactGetItems must report ConsumedCapacity as a non-empty list, one entry "
            + $"per table, got {(consumed is null ? "None" : Py.Repr(consumed.Select(CapacityDict).ToList()))}");
        foreach (var entry in consumed!)
        {
            Ensure(
                entry.TableName == table,
                $"every ConsumedCapacity entry must name the table it charges, got {Py.Repr(entry.TableName)} expected {Py.Repr(table)}");
        }

        return consumed!.Sum(entry => entry.CapacityUnits!.Value);
    }

    /// <summary>{pk: full item} for a list of returned items, so three reads can be compared exactly.</summary>
    private static Dictionary<string, Dictionary<string, AttributeValue>> ItemMap(IEnumerable<Dictionary<string, AttributeValue>> items) =>
        items.ToDictionary(i => i["pk"].S, i => i, StringComparer.Ordinal);

    /// <summary>
    /// Eventual BatchGetItem, strong BatchGetItem and TransactGetItems bill 1x, 2x and 4x one unit
    /// over the same keys, while all three return identical items.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/read-write-operations.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transactional-read-costs-double-a-strongly-consistent-read")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-txnread-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real:managed", Value = "true" }, new Tag { Key = "sim2real:check", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        var keys = Enumerable.Range(0, Items).Select(i => $"K#{i.ToString("D2", CultureInfo.InvariantCulture)}").ToList();
        var written = new Dictionary<string, Dictionary<string, AttributeValue>>(StringComparer.Ordinal);
        for (var i = 0; i < keys.Count; i++)
        {
            var pk = keys[i];
            var item = Item(("pk", S(pk)), ("v", N(i.ToString(CultureInfo.InvariantCulture))), ("payload", S(Payload)));
            written[pk] = item;
            await ddb.PutItemAsync(table, item);
        }

        List<Dictionary<string, AttributeValue>> KeyList() => keys.Select(pk => Item(("pk", S(pk)))).ToList();

        var eventual = await ddb.BatchGetItemAsync(new BatchGetItemRequest
        {
            RequestItems = new Dictionary<string, KeysAndAttributes> { [table] = new() { Keys = KeyList(), ConsistentRead = false } },
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        });
        var strong = await ddb.BatchGetItemAsync(new BatchGetItemRequest
        {
            RequestItems = new Dictionary<string, KeysAndAttributes> { [table] = new() { Keys = KeyList(), ConsistentRead = true } },
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        });
        var transactional = await ddb.TransactGetItemsAsync(new TransactGetItemsRequest
        {
            TransactItems = KeyList().Select(k => new TransactGetItem { Get = new Get { TableName = table, Key = k } }).ToList(),
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        });

        // All three must have really read everything, or the numbers below are measuring less work.
        foreach (var (label, resp) in new[] { ("eventual BatchGetItem", eventual), ("strong BatchGetItem", strong) })
        {
            var residue = resp.UnprocessedKeys ?? [];
            Ensure(
                residue.Count == 0,
                $"{label} left UnprocessedKeys {Py.Repr(residue.Keys.ToList())}; its capacity number therefore covers fewer "
                + $"than the {Items} items and cannot be compared");
        }

        var eventualItems = ItemMap(eventual.Responses[table]);
        var strongItems = ItemMap(strong.Responses[table]);
        var txnItems = ItemMap(transactional.Responses.Select(r => r.Item));
        foreach (var (label, got) in new[] { ("eventual BatchGetItem", eventualItems), ("strong BatchGetItem", strongItems), ("TransactGetItems", txnItems) })
        {
            var missing = written.Keys.Except(got.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            var differing = written.Keys.Intersect(got.Keys, StringComparer.Ordinal)
                .Where(k => !Py.Equal(got[k], written[k]))
                .Order(StringComparer.Ordinal)
                .ToList();
            Ensure(
                Py.Equal(got, written),
                $"{label} must return all {Items} items byte-identically -- a cheaper capacity number "
                + $"earned by returning less data is not the property under test. Missing {Py.Repr(missing)}, differing {Py.Repr(differing)}");
        }

        var eventualCap = Total(eventual.ConsumedCapacity, table);
        var strongCap = Total(strong.ConsumedCapacity, table);
        var txnCap = Total(transactional.ConsumedCapacity, table);

        Ensure(
            eventualCap > 0,
            $"an eventually consistent BatchGetItem of {Items} items must bill something, got "
            + $"{Py.Repr(eventualCap)} -- with a zero baseline every ratio below is vacuous");
        Ensure(
            strongCap == 2 * eventualCap,
            "a strongly consistent read of an item up to 4 KB is one read unit and an eventually "
            + $"consistent read is one half, so over the identical {Items} keys the strong read must "
            + $"bill exactly twice the eventual one: strong {Py.Repr(strongCap)} vs eventual {Py.Repr(eventualCap)} "
            + $"(expected {Py.Repr(2 * eventualCap)})");
        Ensure(
            txnCap == 2 * strongCap,
            "a transactional read of an item up to 4 KB is TWO read units, so TransactGetItems over the "
            + $"identical {Items} keys must bill exactly twice the strongly consistent BatchGetItem: "
            + $"transactional {Py.Repr(txnCap)} vs strong {Py.Repr(strongCap)} (expected {Py.Repr(2 * strongCap)}); a target "
            + $"that implements the transactional read as a locked batch reports {Py.Repr(strongCap)}");
        // BatchGetItem rounds EACH item up to 4 KB and then totals, so a sub-4 KB item is exactly one
        // strong read unit.
        Ensure(
            strongCap == (double)Items,
            $"BatchGetItem rounds each item up to the next 4 KB boundary and then totals, so {Items} "
            + $"sub-4 KB items must bill exactly {Py.Repr((double)Items)} strongly consistent read units, got "
            + $"{Py.Repr(strongCap)}");

        // The single-item form must obey the same rule, so the ratio is not an artefact of batching.
        var singleStrong = (await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = KeyList()[0],
            ConsistentRead = true,
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        })).ConsumedCapacity.CapacityUnits!.Value;
        var singleTxn = Total((await ddb.TransactGetItemsAsync(new TransactGetItemsRequest
        {
            TransactItems = [new TransactGetItem { Get = new Get { TableName = table, Key = KeyList()[0] } }],
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        })).ConsumedCapacity, table);
        Ensure(
            singleTxn == 2 * singleStrong,
            "the 2x transactional factor must hold for a single-item read too, not only in a batch: "
            + $"TransactGetItems billed {Py.Repr(singleTxn)} against GetItem(ConsistentRead=True) "
            + $"{Py.Repr(singleStrong)} (expected {Py.Repr(2 * singleStrong)})");
    }
}
