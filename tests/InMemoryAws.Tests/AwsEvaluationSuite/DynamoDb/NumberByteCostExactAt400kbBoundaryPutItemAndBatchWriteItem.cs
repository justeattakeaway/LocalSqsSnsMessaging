// Translated from behavioral/dynamodb/check_number_byte_cost_exact_at_400kb_boundary_put_item_and_batch_write_item.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.number-byte-cost-exact-at-400kb-boundary-put-item-and-batch-write-item</summary>
[Category("AwsEvaluationSuite")]
public sealed class NumberByteCostExactAt400kbBoundaryPutItemAndBatchWriteItem
{
    private const string CheckId = "number-byte-cost-exact-at-400kb-boundary-put-item-and-batch-write-item";
    private const int LimitBytes = 409600;
    private static readonly (string Code, string Message) SizeErr = ("ValidationException", "Item size has exceeded the maximum allowed size");

    // raw N literal -> its byte cost: 1 + ceil(integer significant digits / 2)
    // + ceil(fraction significant digits / 2) + 1 if negative, over the zero-trimmed value,
    // with exponent notation counted on the mantissa alone (not expanded).
    private static readonly (string Raw, int Cost)[] PutValues =
    [
        ("0", 1),
        ("1", 2),
        ("123", 3),
        ("1234", 3),
        ("123456", 4),
        ("1.5", 3),
        ("3.14159", 5),
        ("0.15", 2),
        ("-42", 3),
        ("1E125", 2),
        ("1E-100", 2),
        ("12345678901234567890123456789012345678", 20),
    ];

    private static readonly (string Raw, int Cost)[] BatchValues =
    [
        ("123456", 4),
        ("1.5", 3),
        ("-42", 3),
        ("1E125", 2),
        ("12345678901234567890123456789012345678", 20),
    ];

    /// <summary>The decimal string DynamoDB stores for N literal <paramref name="raw"/>; folds E/e exponent notation.</summary>
    private static string CanonicalN(string raw)
    {
        if (!raw.Contains('E', StringComparison.Ordinal) && !raw.Contains('e', StringComparison.Ordinal))
        {
            return raw;
        }

        var normalized = raw.Replace('e', 'E');
        var split = normalized.IndexOf('E', StringComparison.Ordinal);
        var mantissa = normalized[..split];
        var exp = int.Parse(normalized[(split + 1)..], CultureInfo.InvariantCulture);
        var sign = "";
        if (mantissa.StartsWith('-'))
        {
            sign = "-";
            mantissa = mantissa[1..];
        }

        var dot = mantissa.IndexOf('.', StringComparison.Ordinal);
        var intPart = dot < 0 ? mantissa : mantissa[..dot];
        var fracPart = dot < 0 ? "" : mantissa[(dot + 1)..];
        var digits = intPart + fracPart;
        var point = intPart.Length + exp;
        string body;
        if (point <= 0)
        {
            body = "0." + new string('0', -point) + digits;
        }
        else if (point >= digits.Length)
        {
            body = digits + new string('0', point - digits.Length);
        }
        else
        {
            body = digits[..point] + "." + digits[point..];
        }

        return sign + body;
    }

    /// <summary>A pk + N 'n' (byte cost <paramref name="cost"/>) + filler 'f' item whose aggregate size is exactly totalBytes.</summary>
    private static Dictionary<string, AttributeValue> ItemOfTotalSize(string pkValue, string raw, int cost, int totalBytes)
    {
        var overhead = "pk".Length + pkValue.Length + "n".Length + cost + "f".Length;
        var fillerLen = totalBytes - overhead;
        Ensure(fillerLen > 0, $"{totalBytes} bytes is too small for an N attribute of cost {cost}");
        return Item(("pk", S(pkValue)), ("n", N(raw)), ("f", S(new string('x', fillerLen))));
    }

    /// <summary>Assert <paramref name="fn"/> raises exactly (code, message); an unexpected success raises instead.</summary>
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

    /// <summary>
    /// A Number's byte cost at the 400 KB item limit is 1 plus half its zero-trimmed integer and
    /// fraction significant digits (each rounded up), plus 1 if negative, exponent unexpanded.
    /// PutItem and BatchWriteItem accept an item sized to exactly that cost and refuse one byte over.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.number-byte-cost-exact-at-400kb-boundary-put-item-and-batch-write-item")]
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

        async Task<GetItemResponse> Get(string pk) =>
            await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true });

        for (var i = 0; i < PutValues.Length; i++)
        {
            var (raw, cost) = PutValues[i];
            var expectedN = N(CanonicalN(raw));

            var pkAt = $"put-at-{i}";
            var atLimit = ItemOfTotalSize(pkAt, raw, cost, LimitBytes);
            await ddb.PutItemAsync(table, atLimit);
            var got = (await Get(pkAt)).Item;
            Ensure(Py.Equal(got["n"], expectedN), $"{Py.Repr(raw)} (byte cost {cost}): N must round-trip as {Py.Repr(expectedN)}, got {Py.Repr(got["n"])}");
            Ensure(got["f"].S == atLimit["f"].S, $"{Py.Repr(raw)}: a {LimitBytes}-byte item built on byte cost {cost} must read back whole");

            var pkOver = $"put-over-{i}";
            var over = ItemOfTotalSize(pkOver, raw, cost, LimitBytes + 1);
            await ExpectError(() => ddb.PutItemAsync(table, over), SizeErr);
            var rejected = await Get(pkOver);
            Ensure(rejected.Item is null, $"{Py.Repr(raw)}: a PutItem rejected for size must not persist, got {Py.Repr(rejected.Item)}");
        }

        for (var i = 0; i < BatchValues.Length; i++)
        {
            var (raw, cost) = BatchValues[i];
            var expectedN = N(CanonicalN(raw));

            var pkAt = $"batch-at-{i}";
            var atLimit = ItemOfTotalSize(pkAt, raw, cost, LimitBytes);
            var resp = await ddb.BatchWriteItemAsync(new BatchWriteItemRequest
            {
                RequestItems = new Dictionary<string, List<WriteRequest>>(StringComparer.Ordinal) { [table] = [new WriteRequest { PutRequest = new PutRequest { Item = atLimit } }] },
            });
            Ensure(
                (resp.UnprocessedItems ?? []).Count == 0,
                $"{Py.Repr(raw)}: a {LimitBytes}-byte BatchWriteItem PutRequest built on byte cost {cost} must fully process, got UnprocessedItems={Py.Repr(resp.UnprocessedItems)}");
            var got = (await Get(pkAt)).Item;
            Ensure(Py.Equal(got["n"], expectedN), $"{Py.Repr(raw)} (byte cost {cost}): N must round-trip as {Py.Repr(expectedN)} through BatchWriteItem, got {Py.Repr(got["n"])}");

            var pkOver = $"batch-over-{i}";
            var over = ItemOfTotalSize(pkOver, raw, cost, LimitBytes + 1);
            await ExpectError(
                () => ddb.BatchWriteItemAsync(new BatchWriteItemRequest
                {
                    RequestItems = new Dictionary<string, List<WriteRequest>>(StringComparer.Ordinal) { [table] = [new WriteRequest { PutRequest = new PutRequest { Item = over } }] },
                }),
                SizeErr);
            var rejected = await Get(pkOver);
            Ensure(rejected.Item is null, $"{Py.Repr(raw)}: a BatchWriteItem PutRequest rejected for size must not persist, got {Py.Repr(rejected.Item)}");
        }
    }
}
