// Translated from behavioral/dynamodb/check_update_item_set_remove_fixed_action_cost_ceiling.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-set-remove-fixed-action-cost-ceiling</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemSetRemoveFixedActionCostCeiling
{
    private const string Check = "update-item-set-remove-fixed-action-cost-ceiling";
    private static readonly (string Code, string Message) OverLimit = ("ValidationException", "Item size to update has exceeded the maximum allowed size");
    private const int UpperBound = 410 * 1024;
    private const string ForeignRegion = "us-east-1";

    /// <summary>
    /// A second SET clause in an UpdateItem adds exactly 19 fixed bytes toward the item-size
    /// ceiling, and a REMOVE beside a SET adds exactly its path length plus one whether or not the
    /// path holds a value, with the same byte costs in us-west-2 and us-east-1.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ServiceQuotas.html#limits-items
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-set-remove-fixed-action-cost-ceiling")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        Ensure(ctx.Region != ForeignRegion, "the probe needs two distinct regions");
        var ddbHome = ctx.Dynamo;
        // region-pinned: UpdateItem's fixed per-action byte costs are compared against us-east-1.
        var ddbForeign = ctx.DynamoClient(region: ForeignRegion);

        var tableHome = await CreateTable(ctx, ddbHome, "sim2real-behav-ddbcost-home-");
        var tableForeign = await CreateTable(ctx, ddbForeign, "sim2real-behav-ddbcost-far-");

        var home = await Measure(ddbHome, tableHome, ctx.Region);
        var foreign = await Measure(ddbForeign, tableForeign, ForeignRegion);

        Ensure(
            home == foreign,
            $"the fixed per-action byte costs must match between regions, got {ctx.Region}={Py.Repr(home)}, " +
            $"{ForeignRegion}={Py.Repr(foreign)}");
    }

    /// <summary>Call fn(); assert the ClientError it raises is exactly (code, message).</summary>
    private static async Task ExpectError(Func<Task<UpdateItemResponse>> fn, string code, string message)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            Ensure(error.Code == code, $"expected {Py.Repr(code)}, got {Py.Repr(error.Code)}: {Py.Repr(error.Message)}");
            Ensure(error.Message == message, $"expected message {Py.Repr(message)}, got {Py.Repr(error.Message)}");
            return;
        }

        throw Fail($"expected a refusal, but the call succeeded: {Py.Repr(result?.Attributes)}");
    }

    /// <summary>
    /// True if fn() (an UpdateItem call) is accepted; False if refused with exactly
    /// OverLimit. Any other outcome fails the check.
    /// </summary>
    private static async Task<bool> Accepted(Func<Task<UpdateItemResponse>> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is null)
        {
            return true;
        }

        var pair = (error.Code, error.Message);
        if (pair == OverLimit)
        {
            return false;
        }

        throw Fail($"unexpected error instead of the size refusal: {Py.Repr(pair)}");
    }

    /// <summary>The largest n in [1, UpperBound] for which acceptFn(n) is accepted; n+1 is refused.</summary>
    private static async Task<int> Bisect(Func<int, Task<bool>> acceptFn)
    {
        int lo = 1, hi = UpperBound;
        Ensure(await acceptFn(lo), $"the smallest probe size {lo} must be accepted");
        Ensure(!await acceptFn(hi), $"the largest probe size {hi} must be refused");
        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            if (await acceptFn(mid))
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    private static IEnumerator<string> Counter() =>
        Enumerable.Range(1, 99999).Select(i => i.ToString("D8", CultureInfo.InvariantCulture)).GetEnumerator();

    private static string Next(IEnumerator<string> source)
    {
        source.MoveNext();
        return source.Current;
    }

    private static async Task<string> CreateTable(FidelityContext ctx, IAmazonDynamoDB ddb, string prefix)
    {
        var table = FidelityContext.UniqueName(prefix);
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("k", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("k", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 30);
        return table;
    }

    private static Task<UpdateItemResponse> SetV(IAmazonDynamoDB ddb, string table, string pk, string expression, Dictionary<string, AttributeValue> values) =>
        ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("k", S(pk))),
            UpdateExpression = expression,
            ExpressionAttributeValues = values,
        });

    private static async Task<Dictionary<string, AttributeValue>> Get(IAmazonDynamoDB ddb, string table, string pk) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("k", S(pk))), ConsistentRead = true })).Item;

    /// <summary>The four UpdateItem SET/REMOVE byte ceilings this check compares, for one region.</summary>
    private static async Task<(int, int, int, int)> Measure(IAmazonDynamoDB ddb, string table, string label)
    {
        var pks = Counter();

        // One SET clause, a 1-character attribute name, on an item that does not have it yet.
        var pk1 = Next(pks);
        await ddb.PutItemAsync(table, Item(("k", S(pk1))));
        var nSet1 = await Bisect(n => Accepted(() => SetV(ddb, table, pk1, "SET v = :v", Item((":v", S(new string('x', n)))))));
        // Positive control beside the refusal: the accepted boundary round-trips whole, and the
        // next byte is refused leaving the attribute at its last accepted length.
        await SetV(ddb, table, pk1, "SET v = :v", Item((":v", S(new string('x', nSet1)))));
        var got = await Get(ddb, table, pk1);
        Ensure(
            got["v"].S.Length == nSet1,
            $"{label}: the accepted {nSet1}-byte SET must round-trip whole, got {got["v"].S.Length} bytes");
        await ExpectError(() => SetV(ddb, table, pk1, "SET v = :v", Item((":v", S(new string('x', nSet1 + 1))))), OverLimit.Code, OverLimit.Message);
        var after = await Get(ddb, table, pk1);
        Ensure(
            after["v"].S.Length == nSet1,
            $"{label}: a refused SET must leave the attribute at its last accepted length, " +
            $"got {after["v"].S.Length} bytes");

        // Two SET clauses: v1 fixed at 1 byte, v2 varied; both attribute names are 2 characters.
        var pk2 = Next(pks);
        await ddb.PutItemAsync(table, Item(("k", S(pk2))));
        var nSet2 = await Bisect(n => Accepted(() => SetV(
            ddb, table, pk2, "SET v1 = :v1, v2 = :v2", Item((":v1", S("z")), (":v2", S(new string('y', n)))))));

        // One SET plus a REMOVE of a 1-character attribute path that already holds a value.
        var pk3 = Next(pks);
        await ddb.PutItemAsync(table, Item(("k", S(pk3)), ("w", S("present"))));
        var nRm1 = await Bisect(n => Accepted(() => SetV(ddb, table, pk3, "SET v = :v REMOVE w", Item((":v", S(new string('x', n)))))));
        // Positive control: the REMOVE actually removed w, and v holds the accepted length.
        var final = await Get(ddb, table, pk3);
        Ensure(!final.ContainsKey("w"), $"{label}: REMOVE w must leave no trace, got {Py.Repr(final.Keys.Order(StringComparer.Ordinal).ToList())}");
        Ensure(
            final["v"].S.Length == nRm1,
            $"{label}: the accepted SET+REMOVE round-trip must store {nRm1} bytes, got {final["v"].S.Length}");

        // One SET plus a REMOVE of a 2-character attribute path, never populated.
        var pk4 = Next(pks);
        await ddb.PutItemAsync(table, Item(("k", S(pk4))));
        var nRm2 = await Bisect(n => Accepted(() => SetV(ddb, table, pk4, "SET v = :v REMOVE ww", Item((":v", S(new string('x', n)))))));

        // A second SET clause adds exactly 19 fixed bytes: 'v1', 'z' and 'v2' together contribute
        // 4 more real content bytes than 'v' alone, so the rest of the n_set1-n_set2 difference is
        // the extra clause's own fixed cost.
        var knownExtraContent = "v1".Length + "z".Length + "v2".Length - "v".Length;
        var secondSetCost = nSet1 - nSet2 - knownExtraContent;
        Ensure(
            secondSetCost == 19,
            $"{label}: a second SET clause must add exactly 19 bytes of fixed overhead, measured " +
            $"{secondSetCost} (n_set1={nSet1}, n_set2={nSet2})");

        // A REMOVE beside a SET adds exactly one fixed byte plus the removed path's own length,
        // whether or not that path currently holds a value.
        var removeCostW = nSet1 - nRm1;
        Ensure(
            removeCostW == 1 + "w".Length,
            $"{label}: REMOVE w beside a SET must add exactly {1 + "w".Length} bytes, measured {removeCostW}");
        var removeCostWw = nSet1 - nRm2;
        Ensure(
            removeCostWw == 1 + "ww".Length,
            $"{label}: REMOVE ww beside a SET must add exactly {1 + "ww".Length} bytes, measured {removeCostWw}");

        return (nSet1, nSet2, nRm1, nRm2);
    }
}
