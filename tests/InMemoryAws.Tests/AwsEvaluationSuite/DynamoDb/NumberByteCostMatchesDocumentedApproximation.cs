// Translated from behavioral/dynamodb/check_number_byte_cost_matches_documented_approximation.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.number-byte-cost-matches-documented-approximation</summary>
[Category("AwsEvaluationSuite")]
public sealed class NumberByteCostMatchesDocumentedApproximation
{
    private const string CheckId = "number-byte-cost-matches-documented-approximation";
    private const string ErrValidation = "ValidationException";
    private const string ErrMessage = "Item size has exceeded the maximum allowed size";
    private const int ItemLimit = 409600;
    private static readonly string[] Values = ["0", "1.5", "100.5", "3.14159", "-42", "12345678901234567890"];

    /// <summary>
    /// Digit count after the sign, the decimal point and any leading zeroes drop out.
    ///
    /// '0' itself has none. None of the values this check tries has a trailing zero that a
    /// stricter significant-figure rule would also drop, so this reading needs no such rule.
    /// </summary>
    private static int SignificantDigits(string raw)
    {
        var digits = raw.TrimStart('-').Replace(".", "", StringComparison.Ordinal);
        return digits.TrimStart('0').Length;
    }

    /// <summary>The documented approximation: 1 byte per two significant digits, plus 1 byte.</summary>
    private static int ApproxCost(int digits) => ((digits + 1) / 2) + 1;

    /// <summary>
    /// PutItem pk with extra merged in; True if accepted, False if rejected for size.
    ///
    /// Any other error is a bug in the check, not an expected outcome, and is left to raise.
    /// </summary>
    private static async Task<bool> Fits(IAmazonDynamoDB ddb, string table, string pk, Dictionary<string, AttributeValue> extra)
    {
        var item = Item(("pk", S(pk)));
        foreach (var (name, value) in extra)
        {
            item[name] = value;
        }

        var err = await FidelityContext.CaptureError(() => ddb.PutItemAsync(table, item));
        if (err is null)
        {
            return true;
        }

        Ensure(err.Code == ErrValidation, $"oversized PutItem: expected {ErrValidation}, got {err.Code}: {Py.Repr(err.Message)}");
        Ensure(err.Message == ErrMessage, $"oversized PutItem: expected AWS's exact message {Py.Repr(ErrMessage)}, got {Py.Repr(err.Message)}");
        return false;
    }

    private static Dictionary<string, AttributeValue> WithFiller(Dictionary<string, AttributeValue> extra, int length)
    {
        var copy = new Dictionary<string, AttributeValue>(extra, StringComparer.Ordinal) { ["f"] = S(new string('x', length)) };
        return copy;
    }

    /// <summary>The largest filler length (bytes) that keeps pk+extra+filler under the item size limit.</summary>
    private static async Task<int> MaxFillerLen(IAmazonDynamoDB ddb, string table, string pk, Dictionary<string, AttributeValue> extra)
    {
        var (lo, hi) = (0, ItemLimit);
        Ensure(await Fits(ddb, table, pk, WithFiller(extra, 0)), $"{pk}: an empty filler must already fit under {ItemLimit} bytes");
        Ensure(!await Fits(ddb, table, pk, WithFiller(extra, hi)), $"{pk}: a {hi}-byte filler must already exceed {ItemLimit} bytes");
        return await Search(ddb, table, pk, extra, lo, hi);
    }

    /// <summary>Binary search for the largest filler length in [lo, hi], given that lo fits and hi+1 does not.</summary>
    private static async Task<int> Search(IAmazonDynamoDB ddb, string table, string pk, Dictionary<string, AttributeValue> extra, int lo, int hi)
    {
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (await Fits(ddb, table, pk, WithFiller(extra, mid)))
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

    /// <summary>
    /// The same boundary as MaxFillerLen, searched in guess +/- slack first.
    ///
    /// Two probes bracket the window; only a boundary outside it falls back to the full search, so
    /// the result is exact either way and a wrong guess costs time, never correctness.
    /// </summary>
    private static async Task<int> MaxFillerNear(IAmazonDynamoDB ddb, string table, string pk, Dictionary<string, AttributeValue> extra, int guess, int slack)
    {
        var (lo, hi) = (guess - slack, guess + slack);
        if (lo >= 0 && await Fits(ddb, table, pk, WithFiller(extra, lo)) && !await Fits(ddb, table, pk, WithFiller(extra, hi + 1)))
        {
            return await Search(ddb, table, pk, extra, lo, hi);
        }

        return await MaxFillerLen(ddb, table, pk, extra);
    }

    /// <summary>
    /// A DynamoDB Number's byte cost is within one byte of the documented approximation of
    /// one byte per two significant digits plus one byte, which the docs call approximate, not
    /// exact -- measured against the 409,600-byte item size ceiling.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/CapacityUnitCalculations.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.number-byte-cost-matches-documented-approximation")]
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
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 30);

        // Every trial, including the baseline, writes the SAME pk so the key's own byte cost is
        // identical across them and cancels exactly out of the difference below.
        const string pk = "k";
        // The documented item size (attribute names plus values) puts the boundary near the limit less
        // 'pk', 'k' and 'f'; the search is exact wherever it lies.
        var baseline = await MaxFillerNear(ddb, table, pk, [], ItemLimit - "pkkf".Length, 64);

        foreach (var raw in Values)
        {
            var digits = SignificantDigits(raw);
            var expected = ApproxCost(digits);
            // Searched within a byte of where the documented cost puts the boundary, the window the
            // assert below accepts; a boundary outside it is found by the full search and fails there.
            var maxFiller = await MaxFillerNear(ddb, table, pk, Item(("n", N(raw))), baseline - "n".Length - expected, 1);
            // Adding the 'n' attribute (1-byte name) displaces exactly its own byte cost from the
            // filler budget shared with the baseline under the same item size ceiling.
            var measuredCost = baseline - maxFiller - "n".Length;
            // The docs call this "approximately", so the measured cost is graded within a byte of
            // it rather than pinned to an exact undocumented internal encoding.
            Ensure(
                Math.Abs(measuredCost - expected) <= 1,
                $"N {Py.Repr(raw)} ({digits} significant digits): expected within 1 byte of the documented approximation of {expected} bytes, measured {measuredCost} bytes from the {ItemLimit}-byte boundary");

            // Positive control: the discovered boundary itself is accepted and reads back whole, so
            // a target that silently truncates the filler instead of rejecting it cannot pass here.
            await ddb.PutItemAsync(table, Item(("pk", S(pk)), ("n", N(raw)), ("f", S(new string('x', maxFiller)))));
            var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;
            Ensure(Py.Equal(got["n"], N(raw)), $"{Py.Repr(raw)} must round-trip unchanged at the boundary, got {Py.Repr(got["n"])}");
            Ensure(got["f"].S.Length == maxFiller, $"the {maxFiller}-byte boundary filler must read back whole, got {got["f"].S.Length} bytes");
        }
    }
}
