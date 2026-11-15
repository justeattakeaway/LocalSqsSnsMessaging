// Translated from behavioral/dynamodb/check_bool_and_null_attribute_byte_cost_at_item_size_gate.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.bool-and-null-attribute-byte-cost-at-item-size-gate</summary>
[Category("AwsEvaluationSuite")]
public sealed class BoolAndNullAttributeByteCostAtItemSizeGate
{
    private const string Check = "bool-and-null-attribute-byte-cost-at-item-size-gate";
    private const int ItemLimit = 409600;
    private const string Pk = "k";
    private static readonly (string Code, string Message) Refusal = ("ValidationException", "Item size has exceeded the maximum allowed size");

    /// <summary>
    /// PutItem bills a BOOL or a NULL attribute at its name's byte length plus exactly one byte
    /// toward the 409,600-byte item size ceiling, for both BOOL values and for NULL.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/CapacityUnitCalculations.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.bool-and-null-attribute-byte-cost-at-item-size-gate")]
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
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        // Every cost trial, including the baseline, writes the SAME pk so the key's own byte cost is
        // identical across them and cancels exactly out of the difference below.
        var baseline = await MaxFillerNear(ddb, table, Pk, [], ItemLimit - "pkkf".Length, 8);

        var cases = new (string Name, Func<AttributeValue> Value)[]
        {
            ("b", () => Bool(true)),
            ("b", () => Bool(false)),
            ("z", Null),
        };
        foreach (var (name, value) in cases)
        {
            // Searched within the window the extra attribute's 1-byte expected cost puts the boundary
            // at; a boundary outside it is found by the full search and fails there regardless.
            var maxFiller = await MaxFillerNear(ddb, table, Pk, [(name, value)], baseline - name.Length - 1, 4);
            var measuredCost = baseline - maxFiller - name.Length;
            Ensure(
                measuredCost == 1,
                $"{name}={Py.Repr(value())}: expected exactly 1 byte of value cost, measured {measuredCost} bytes " +
                $"from the {ItemLimit}-byte boundary");
        }

        // The item-size gate itself, built with a BOOL attribute present. "acc-bool" and "rej-bool"
        // are the same length, so the boundary found for one is exactly the boundary for the other.
        var boolFiller = await MaxFillerNear(ddb, table, "acc-bool", [("b", () => Bool(true))], baseline - "b".Length - 1, 4);
        await ddb.PutItemAsync(table, Item(("pk", S("acc-bool")), ("b", Bool(true)), ("f", S(new string('x', boolFiller)))));
        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("acc-bool"))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(got["b"], Bool(true)), $"the accepted boundary item's BOOL attribute read back {Py.Repr(got["b"])}");
        Ensure(
            got["f"].S.Length == boolFiller,
            $"the accepted boundary item's filler read back {got["f"].S.Length} bytes, expected {boolFiller}");

        // One byte over the same boundary is refused with the exact code and message, and the
        // rejected write leaves no item behind at a key never written before.
        await ExpectError(
            () => ddb.PutItemAsync(table, Item(("pk", S("rej-bool")), ("b", Bool(true)), ("f", S(new string('x', boolFiller + 1))))),
            Refusal.Code,
            Refusal.Message);
        var rejected = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("rej-bool"))), ConsistentRead = true });
        Ensure(rejected.Item is null, $"the refused over-limit PutItem must not persist, got {Py.Repr(rejected.Item)}");

        // The same boundary demonstrated with a NULL attribute instead of BOOL.
        var nullFiller = await MaxFillerNear(ddb, table, "acc-null", [("z", Null)], baseline - "z".Length - 1, 4);
        await ddb.PutItemAsync(table, Item(("pk", S("acc-null")), ("z", Null()), ("f", S(new string('x', nullFiller)))));
        got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("acc-null"))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(got["z"], Null()), $"the accepted boundary item's NULL attribute read back {Py.Repr(got["z"])}");

        await ExpectError(
            () => ddb.PutItemAsync(table, Item(("pk", S("rej-null")), ("z", Null()), ("f", S(new string('x', nullFiller + 1))))),
            Refusal.Code,
            Refusal.Message);
        rejected = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("rej-null"))), ConsistentRead = true });
        Ensure(rejected.Item is null, $"the refused over-limit PutItem must not persist, got {Py.Repr(rejected.Item)}");
    }

    /// <summary>Call fn() and assert the ClientError it raises carries exactly code and message.</summary>
    private static async Task<AwsError> ExpectError<T>(Func<Task<T>> fn, string code, string message)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            Ensure(error.Code == code, $"expected {Py.Repr(code)}, got {Py.Repr(error.Code)}: {Py.Repr(error.Message)}");
            Ensure(error.Message == message, $"expected message {Py.Repr(message)}, got {Py.Repr(error.Message)}");
            return error;
        }

        throw Fail($"expected {Py.Repr(code)}, but the call succeeded: {Py.Repr(result)}");
    }

    /// <summary>PutItem of {pk} merged with extra; True if accepted, False if refused for size.</summary>
    private static async Task<bool> Fits(IAmazonDynamoDB ddb, string table, string pk, (string Name, Func<AttributeValue> Value)[] extra, int filler)
    {
        var item = Item(("pk", S(pk)));
        foreach (var (name, value) in extra)
        {
            item[name] = value();
        }

        item["f"] = S(new string('x', filler));
        var error = await FidelityContext.CaptureError(() => ddb.PutItemAsync(table, item));
        if (error is null)
        {
            return true;
        }

        Ensure(
            error.Code == Refusal.Code,
            $"oversized PutItem: expected {Refusal.Code}, got {error.Code}: {Py.Repr(error.Message)}");
        Ensure(
            error.Message == Refusal.Message,
            $"oversized PutItem: expected AWS's exact message {Py.Repr(Refusal.Message)}, got {Py.Repr(error.Message)}");
        return false;
    }

    /// <summary>
    /// The largest filler length (bytes) for which {pk, **extra, f: filler} still fits.
    ///
    /// Two probes bracket the window at guess +/- slack; only a boundary outside it falls back to a
    /// full search, so the result is exact either way.
    /// </summary>
    private static async Task<int> MaxFillerNear(IAmazonDynamoDB ddb, string table, string pk, (string Name, Func<AttributeValue> Value)[] extra, int guess, int slack)
    {
        var (lo, hi) = (guess - slack, guess + slack);
        if (lo >= 0 && await Fits(ddb, table, pk, extra, lo) && !await Fits(ddb, table, pk, extra, hi + 1))
        {
            while (lo < hi)
            {
                var mid = (lo + hi + 1) / 2;
                if (await Fits(ddb, table, pk, extra, mid))
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

        (lo, hi) = (0, ItemLimit);
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (await Fits(ddb, table, pk, extra, mid))
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
