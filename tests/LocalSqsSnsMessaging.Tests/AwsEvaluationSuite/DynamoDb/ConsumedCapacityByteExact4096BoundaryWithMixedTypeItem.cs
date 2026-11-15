// Translated from behavioral/dynamodb/check_consumed_capacity_byte_exact_4096_boundary_with_mixed_type_item.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.consumed-capacity-byte-exact-4096-boundary-with-mixed-type-item</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConsumedCapacityByteExact4096BoundaryWithMixedTypeItem
{
    private const string Check = "consumed-capacity-byte-exact-4096-boundary-with-mixed-type-item";
    private const int ItemLimit = 409600;
    private const string ErrValidation = "ValidationException";
    private const string ErrOversized = "Item size has exceeded the maximum allowed size";

    /// <summary>
    /// A GetItem's CapacityUnits is 1.0 strong / 0.5 eventual for an item of exactly 4096 bytes
    /// and 2.0 / 1.0 for 4097, with the size built from N, B, BOOL, NULL, L and M members.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/CapacityUnitCalculations.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.consumed-capacity-byte-exact-4096-boundary-with-mixed-type-item")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-rcub-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        const string pk = "k";
        // The fixed N/B/BOOL/NULL/L/M attributes plus `pk` have a byte cost this check does not
        // compute by hand: a Number's documented cost is only "approximately" one byte per two
        // significant digits, so the exact figure is measured against the item size ceiling -- a hard
        // byte-exact boundary -- instead of assumed from the approximation.
        Ensure(await Fits(ddb, table, pk, 0), "the fixed mixed-type item alone must already fit under 400KB");
        Ensure(
            !await Fits(ddb, table, pk, ItemLimit),
            $"the fixed mixed-type item plus a {Py.Repr(ItemLimit)}-byte filler must already exceed 400KB");
        var maxFiller = await Search(ddb, table, pk, 0, ItemLimit);
        // max_filler fits and max_filler + 1 does not, so the item at max_filler is exactly 409600
        // bytes; everything but the filler's own bytes is the remainder.
        var fixedBytes = ItemLimit - maxFiller;

        var target4096 = 4096 - fixedBytes;
        var target4097 = target4096 + 1;
        Ensure(
            target4096 >= 0,
            $"the fixed mixed-type item alone is {Py.Repr(fixedBytes)} bytes, which already exceeds the " +
            "4096-byte block this check means to land on");

        async Task Put(int fillerLen)
        {
            var item = FixedItem(pk);
            item["f"] = S(new string('x', fillerLen));
            await ddb.PutItemAsync(table, item);
        }

        async Task<double?> Capacity(bool consistent)
        {
            var resp = await ddb.GetItemAsync(new GetItemRequest
            {
                TableName = table,
                Key = Item(("pk", S(pk))),
                ConsistentRead = consistent,
                ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
            });
            Ensure(resp.Item is not null, "the item must exist before its capacity can be read");
            return resp.ConsumedCapacity.CapacityUnits;
        }

        await Put(target4096);
        var strong = await Capacity(consistent: true);
        Ensure(
            strong == 1.0,
            $"a strongly consistent GetItem of the {Py.Repr(fixedBytes)}+{Py.Repr(target4096)}=4096-byte " +
            $"mixed-type item cost {Py.Repr(strong)} CapacityUnits, expected 1.0");
        var eventual = await Capacity(consistent: false);
        Ensure(
            eventual == 0.5,
            $"an eventually consistent GetItem of the same 4096-byte item cost {Py.Repr(eventual)} " +
            "CapacityUnits, expected 0.5");

        await Put(target4097);
        var strongOver = await Capacity(consistent: true);
        Ensure(
            strongOver == 2.0,
            $"a strongly consistent GetItem of the {Py.Repr(fixedBytes)}+{Py.Repr(target4097)}=4097-byte " +
            $"mixed-type item cost {Py.Repr(strongOver)} CapacityUnits, expected 2.0 -- one byte over the " +
            "4096-byte edge must already round up to a second block");
        var eventualOver = await Capacity(consistent: false);
        Ensure(
            eventualOver == 1.0,
            $"an eventually consistent GetItem of the same 4097-byte item cost {Py.Repr(eventualOver)} " +
            "CapacityUnits, expected 1.0");

        // Positive control: every mixed-type member reads back exactly as written at the larger size,
        // proving the capacity numbers above price a real item and not an absent or truncated one.
        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(got["n"], N("42")), $"the Number member read back as {Py.Repr(got["n"])}, expected N '42'");
        Ensure(Py.Equal(got["b"], B([0x01, 0x02, 0x03, 0x04])), $"the Binary member read back as {Py.Repr(got["b"])}");
        Ensure(Py.Equal(got["bo"], Bool(true)), $"the Boolean member read back as {Py.Repr(got["bo"])}");
        Ensure(Py.Equal(got["nu"], Null()), $"the Null member read back as {Py.Repr(got["nu"])}");
        Ensure(Py.Equal(got["l"], L(S("ab"), B([0x05, 0x06]), Bool(false))), $"the List member read back as {Py.Repr(got["l"])}");
        Ensure(Py.Equal(got["m"], M(("x", S("y")), ("z", Null()))), $"the Map member read back as {Py.Repr(got["m"])}");
        Ensure(
            got["f"].S.Length == target4097,
            $"the filler read back {Py.Repr(got["f"].S.Length)} bytes, expected {Py.Repr(target4097)}");
    }

    /// <summary>
    /// A cleanup running <c>undo()</c> where a failed delete raises; the resource's own not-found
    /// code means it is already gone.
    /// </summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };

    /// <summary>A representative mixed-type item: N, B, BOOL, NULL, L and M members.</summary>
    private static Dictionary<string, AttributeValue> FixedItem(string pk) => Item(
        ("pk", S(pk)),
        ("n", N("42")),
        ("b", B([0x01, 0x02, 0x03, 0x04])),
        ("bo", Bool(true)),
        ("nu", Null()),
        ("l", L(S("ab"), B([0x05, 0x06]), Bool(false))),
        ("m", M(("x", S("y")), ("z", Null()))));

    /// <summary>
    /// PutItem of the fixed mixed-type item plus an <c>f</c> filler of <c>filler_len</c> bytes.
    ///
    /// True if accepted; False if rejected for exceeding the item size limit. Any other error is a
    /// defect in the check, not an expected outcome, and is left to raise.
    /// </summary>
    private static async Task<bool> Fits(IAmazonDynamoDB ddb, string table, string pk, int fillerLen)
    {
        var item = FixedItem(pk);
        item["f"] = S(new string('x', fillerLen));
        var error = await FidelityContext.CaptureError(() => ddb.PutItemAsync(table, item));
        if (error is null)
        {
            return true;
        }

        Ensure(
            error.Code == ErrValidation,
            $"oversized PutItem: expected {Py.Repr(ErrValidation)}, got {Py.Repr(error.Code)}: " +
            $"{Py.Repr(error.Message)}");
        Ensure(
            error.Message == ErrOversized,
            $"oversized PutItem: expected the exact message {Py.Repr(ErrOversized)}, got " +
            $"{Py.Repr(error.Message)}");
        return false;
    }

    /// <summary>Binary search for the largest filler length in [lo, hi], given lo fits and hi+1 does not.</summary>
    private static async Task<int> Search(IAmazonDynamoDB ddb, string table, string pk, int lo, int hi)
    {
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (await Fits(ddb, table, pk, mid))
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo;
    }
}
