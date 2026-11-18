// Translated from behavioral/dynamodb/check_number_magnitude_floor_put_update_batchwrite.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.number-magnitude-floor-put-update-batchwrite</summary>
[Category("AwsEvaluationSuite")]
public sealed class NumberMagnitudeFloorPutUpdateBatchwrite
{
    private const string CheckId = "number-magnitude-floor-put-update-batchwrite";

    // Documented N range floor and ceiling:
    // https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/HowItWorks.NamingRulesDataTypes.html#HowItWorks.DataTypes.Number
    private const string Floor = "1E-130";
    private const string FloorNeg = "-1E-130";
    private const string BelowFloor = "1E-131";
    private const string BelowFloorNeg = "-1E-131";
    private const string CeilPos = "9.9999999999999999999999999999999999999E+125";
    private const string CeilNeg = "-9.9999999999999999999999999999999999999E+125";

    // DynamoDB folds every accepted N into a canonical plain-decimal string with no exponent;
    // Decimal's 'f' format produces the identical string for these magnitudes.
    private static readonly string FloorStored = PlainDecimal(Floor);
    private static readonly string FloorNegStored = PlainDecimal(FloorNeg);
    private static readonly string CeilPosStored = PlainDecimal(CeilPos);
    private static readonly string CeilNegStored = PlainDecimal(CeilNeg);

    // PutItem and UpdateItem wrap the message in the "N validation error(s) detected" envelope;
    // BatchWriteItem's ValidationException carries the bare message with no envelope.
    private const string UnderflowEnvelope = "1 validation error detected: Number underflow. Attempting to store a number with magnitude smaller than supported range";
    private const string UnderflowBare = "Number underflow. Attempting to store a number with magnitude smaller than supported range";
    private static readonly (string Code, string Message) PutUnderflow = ("ValidationException", UnderflowEnvelope);
    private static readonly (string Code, string Message) UpdateUnderflow = ("ValidationException", UnderflowEnvelope);
    private static readonly (string Code, string Message) BatchUnderflow = ("ValidationException", UnderflowBare);

    /// <summary>Python's <c>format(Decimal(raw), "f")</c>: the plain-decimal string, exponent folded in.</summary>
    private static string PlainDecimal(string raw)
    {
        var sign = raw.StartsWith('-') ? "-" : "";
        var text = raw.TrimStart('-', '+');
        var exp = 0;
        var e = text.IndexOfAny(['e', 'E']);
        if (e >= 0)
        {
            exp = int.Parse(text[(e + 1)..], CultureInfo.InvariantCulture);
            text = text[..e];
        }

        var dot = text.IndexOf('.', StringComparison.Ordinal);
        var intPart = dot < 0 ? text : text[..dot];
        var fracPart = dot < 0 ? "" : text[(dot + 1)..];
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

    /// <summary>(code, message) fn()'s ClientError carries; an unexpected success raises.</summary>
    private static async Task ExpectError<T>(Func<Task<T>> fn, (string Code, string Message) expected)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            Ensure(
                (error.Code, error.Message) == expected,
                $"expected ({Py.Repr(expected.Code)}, {Py.Repr(expected.Message)}), got ({Py.Repr(error.Code)}, {Py.Repr(error.Message)})");
            return;
        }

        throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
    }

    /// <summary>
    /// PutItem, UpdateItem and BatchWriteItem accept N at the documented magnitude floor and
    /// ceiling and round-trip the canonical stored string; one step past the floor is
    /// ValidationException (underflow) with no item written, and the envelope differs by operation.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/HowItWorks.NamingRulesDataTypes.html#HowItWorks.DataTypes.Number
    /// </summary>
    [Test]
    [DisplayName("dynamodb.number-magnitude-floor-put-update-batchwrite")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-numfloor-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        async Task<Dictionary<string, AttributeValue>?> Get(string pk) =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;

        // --- PutItem: floor and negative-floor are accepted and round-trip the canonical string ---
        await ddb.PutItemAsync(table, Item(("pk", S("put-floor")), ("v", N(Floor))));
        var got = await Get("put-floor");
        Ensure(Py.Equal(got!["v"], N(FloorStored)), $"PutItem {Floor} must store as {Py.Repr(FloorStored)}, got {Py.Repr(got["v"])}");

        await ddb.PutItemAsync(table, Item(("pk", S("put-floor-neg")), ("v", N(FloorNeg))));
        got = await Get("put-floor-neg");
        Ensure(Py.Equal(got!["v"], N(FloorNegStored)), $"PutItem {FloorNeg} must store as {Py.Repr(FloorNegStored)}, got {Py.Repr(got["v"])}");

        // --- PutItem: the negative ceiling is accepted and round-trips ---
        await ddb.PutItemAsync(table, Item(("pk", S("put-ceil-neg")), ("v", N(CeilNeg))));
        got = await Get("put-ceil-neg");
        Ensure(Py.Equal(got!["v"], N(CeilNegStored)), $"PutItem {CeilNeg} must store as {Py.Repr(CeilNegStored)}, got {Py.Repr(got["v"])}");

        // --- PutItem: an NS holding the positive ceiling, negative ceiling, floor and zero stores all four ---
        await ddb.PutItemAsync(table, Item(("pk", S("put-ns")), ("v", NS(CeilPos, CeilNeg, Floor, "0"))));
        got = await Get("put-ns");
        Ensure(
            got!["v"].NS.Order(StringComparer.Ordinal).SequenceEqual(new[] { CeilPosStored, CeilNegStored, FloorStored, "0" }.Order(StringComparer.Ordinal)),
            $"NS of ceiling/floor/zero must store all four members, got {Py.Repr(got["v"])}");

        // --- PutItem: one step past the floor, on either sign, is underflow and writes nothing ---
        await ExpectError(() => ddb.PutItemAsync(table, Item(("pk", S("put-under")), ("v", N(BelowFloor)))), PutUnderflow);
        Ensure(await Get("put-under") is null, "a rejected PutItem below the floor must not create the item");

        await ExpectError(() => ddb.PutItemAsync(table, Item(("pk", S("put-under-neg")), ("v", N(BelowFloorNeg)))), PutUnderflow);
        Ensure(await Get("put-under-neg") is null, "a rejected PutItem below the negative floor must not create the item");

        // --- UpdateItem: SET to the floor is accepted and round-trips ---
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("upd"))),
            UpdateExpression = "SET v = :v",
            ExpressionAttributeValues = Item((":v", N(Floor))),
        });
        got = await Get("upd");
        Ensure(Py.Equal(got!["v"], N(FloorStored)), $"UpdateItem SET to {Floor} must store as {Py.Repr(FloorStored)}, got {Py.Repr(got["v"])}");

        // --- UpdateItem: SET one step past the floor is underflow and leaves the item unchanged ---
        await ExpectError(
            () => ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = Item(("pk", S("upd"))),
                UpdateExpression = "SET v = :v",
                ExpressionAttributeValues = Item((":v", N(BelowFloor))),
            }),
            UpdateUnderflow);
        got = await Get("upd");
        Ensure(Py.Equal(got!["v"], N(FloorStored)), $"a rejected UpdateItem below the floor must leave the item at {Py.Repr(FloorStored)}, got {Py.Repr(got["v"])}");

        // --- BatchWriteItem: a single PutRequest at the floor is accepted and round-trips ---
        var resp = await ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new Dictionary<string, List<WriteRequest>>(StringComparer.Ordinal)
            {
                [table] = [new WriteRequest { PutRequest = new PutRequest { Item = Item(("pk", S("batch-floor")), ("v", N(Floor))) } }],
            },
        });
        Ensure(resp.UnprocessedItems is { Count: 0 }, $"accepted batch left UnprocessedItems {Py.Repr(resp.UnprocessedItems)}");
        got = await Get("batch-floor");
        Ensure(Py.Equal(got!["v"], N(FloorStored)), $"BatchWriteItem {Floor} must store as {Py.Repr(FloorStored)}, got {Py.Repr(got["v"])}");

        // --- BatchWriteItem: one step past the floor rejects the whole request, bare message, writes nothing ---
        await ExpectError(
            () => ddb.BatchWriteItemAsync(new BatchWriteItemRequest
            {
                RequestItems = new Dictionary<string, List<WriteRequest>>(StringComparer.Ordinal)
                {
                    [table] =
                    [
                        new WriteRequest { PutRequest = new PutRequest { Item = Item(("pk", S("batch-good")), ("v", N("42"))) } },
                        new WriteRequest { PutRequest = new PutRequest { Item = Item(("pk", S("batch-under")), ("v", N(BelowFloor))) } },
                    ],
                },
            }),
            BatchUnderflow);
        Ensure(await Get("batch-good") is null, "a rejected BatchWriteItem must not write its other, valid item");
        Ensure(await Get("batch-under") is null, "a rejected BatchWriteItem below the floor must not create the item");

        // Control: PutItem still succeeds after every rejection above.
        await ddb.PutItemAsync(table, Item(("pk", S("ctrl")), ("v", N("42"))));
        Ensure(Py.Equal((await Get("ctrl"))!["v"], N("42")), "control write must still succeed");
    }
}
