// Translated from behavioral/dynamodb/check_number_byte_cost_zero_trim_and_mid_fraction_literals_at_409600_boundary.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.number-byte-cost-zero-trim-and-mid-fraction-literals-at-409600-boundary</summary>
[Category("AwsEvaluationSuite")]
public sealed class NumberByteCostZeroTrimAndMidFractionLiteralsAt409600Boundary
{
    private const string Check = "number-byte-cost-zero-trim-and-mid-fraction-literals-at-409600-boundary";

    private const int LimitBytes = 409600;

    private static readonly (string Code, string Message) SizeErr = ("ValidationException", "Item size has exceeded the maximum allowed size");

    // raw N literal -> (its canonical stored string, its byte cost at the 409,600-byte item gate).
    // Byte cost: 1 + ceil(integer significant digits / 2) + ceil(fraction significant digits / 2),
    // over the zero-trimmed value (leading zeros in the integer part, trailing zeros in the
    // fraction, dropped before counting).
    private static readonly (string Raw, string Canonical, int Cost)[] Values =
    [
        ("-0", "0", 1),
        ("0.0000001", "0.0000001", 2),
        ("0042", "42", 2),
        ("1.2", "1.2", 3),
        ("1.200", "1.2", 3),
        ("1.234", "1.234", 4),
    ];

    /// <summary>
    /// A Number's byte cost at the 400 KB item limit is 1 plus half its zero-trimmed integer and
    /// fraction significant digits, for a negative zero, a padded integer and a padded or mid
    /// fraction. PutItem accepts an item sized to exactly that cost and refuses one byte over.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.number-byte-cost-zero-trim-and-mid-fraction-literals-at-409600-boundary")]
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
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 30);

        for (var i = 0; i < Values.Length; i++)
        {
            var (raw, canonical, cost) = Values[i];
            var expectedN = N(canonical);

            var pkAt = $"put-at-{i}";
            var atLimit = ItemOfTotalSize(pkAt, raw, cost, LimitBytes);
            await ddb.PutItemAsync(table, atLimit);
            var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pkAt))), ConsistentRead = true })).Item;
            Ensure(
                Py.Equal(got["n"], expectedN),
                $"{Py.Repr(raw)} (byte cost {cost}): N must round-trip as {Py.Repr(expectedN)}, got {Py.Repr(got["n"])}");
            Ensure(
                got["f"].S == atLimit["f"].S,
                $"{Py.Repr(raw)}: a {LimitBytes}-byte item built on byte cost {cost} must read back whole");

            var pkOver = $"put-over-{i}";
            var over = ItemOfTotalSize(pkOver, raw, cost, LimitBytes + 1);
            await ExpectError(() => ddb.PutItemAsync(table, over), SizeErr);
            var rejected = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pkOver))), ConsistentRead = true });
            Ensure(
                rejected.Item is null,
                $"{Py.Repr(raw)}: a PutItem rejected for size must not persist, got {Py.Repr(rejected.Item)}");
        }
    }

    /// <summary>A pk + N 'n' (byte cost <paramref name="cost"/>) + filler 'f' item whose aggregate size is exactly totalBytes.</summary>
    private static Dictionary<string, AttributeValue> ItemOfTotalSize(string pkValue, string raw, int cost, int totalBytes)
    {
        var overhead = "pk".Length + pkValue.Length + "n".Length + cost + "f".Length;
        var fillerLen = totalBytes - overhead;
        Ensure(fillerLen > 0, $"{totalBytes} bytes is too small for an N attribute of cost {cost}");
        return Item(("pk", S(pkValue)), ("n", N(raw)), ("f", S(new string('x', fillerLen))));
    }

    /// <summary>Assert <c>fn</c> raises exactly (code, message); an unexpected success raises instead.</summary>
    private static async Task ExpectError<T>(Func<Task<T>> fn, (string Code, string Message) expected)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            Ensure(error.Code == expected.Code, $"expected {expected.Code}, got {error.Code}: {Py.Repr(error.Message)}");
            Ensure(error.Message == expected.Message, $"expected AWS's exact message {Py.Repr(expected.Message)}, got {Py.Repr(error.Message)}");
            return;
        }

        throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
    }
}
