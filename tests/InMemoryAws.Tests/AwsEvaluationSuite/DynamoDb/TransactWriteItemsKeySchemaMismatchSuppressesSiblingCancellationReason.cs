// Translated from behavioral/dynamodb/check_transact_write_items_key_schema_mismatch_suppresses_sibling_cancellation_reason.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-items-key-schema-mismatch-suppresses-sibling-cancellation-reason</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteItemsKeySchemaMismatchSuppressesSiblingCancellationReason
{
    private const string Check = "transact-write-items-key-schema-mismatch-suppresses-sibling-cancellation-reason";
    private const string KeySchema = "The provided key element does not match the schema";
    private const string ConditionFailed = "The conditional request failed";
    private const string UpdateTypeError = "An operand in the update expression has an incorrect data type";

    /// <summary>
    /// A key-schema ValidationError in one TransactWriteItems slot reports every sibling slot's
    /// cancellation reason as Code='None' regardless of order, even a sibling that would itself have
    /// failed its own ConditionCheck or update; nothing is written in any case.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_TransactWriteItems.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-items-key-schema-mismatch-suppresses-sibling-cancellation-reason")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-txsupp-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await WaitActive(ddb, ctx, table);

        var typedKey = Item(("pk", S("typed")));
        var typedItem = Item(("pk", S("typed")), ("v", S("not-a-number")));
        await ddb.PutItemAsync(table, typedItem);
        var missingKey = Item(("pk", S("absent-target")));
        var wrongKey = Item(("pk", N("1")));

        TransactWriteItem CondCheckFail() => new()
        {
            ConditionCheck = new ConditionCheck { TableName = table, Key = missingKey, ConditionExpression = "attribute_exists(pk)" },
        };
        TransactWriteItem TypeErrorUpdate() => new()
        {
            Update = new Update
            {
                TableName = table,
                Key = typedKey,
                UpdateExpression = "ADD v :inc",
                ExpressionAttributeValues = Item((":inc", N("1"))),
            },
        };
        TransactWriteItem WrongKeyDelete() => new() { Delete = new Delete { TableName = table, Key = wrongKey } };
        TransactWriteItem PutOf(string pk) => new() { Put = new Put { TableName = table, Item = Item(("pk", S(pk))) } };

        // Baseline: without any key-schema defect, a failing ConditionCheck carries its OWN reason.
        var (code, _, reasons) = await Cancelled(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems = [CondCheckFail(), PutOf("baseline-put")],
        }));
        Ensure(code == "TransactionCanceledException", $"baseline: expected TransactionCanceledException, got {Py.Repr(code)}");
        Ensure(
            Py.Equal(reasons[0], R("ConditionalCheckFailed", ConditionFailed)),
            $"baseline: the failing ConditionCheck's own reason must carry through, got {Py.Repr(reasons[0])}");
        Ensure(Py.Equal(reasons[1], R("None")), $"baseline: the passing Put's slot must carry Code='None', got {Py.Repr(reasons[1])}");

        // Baseline: without any key-schema defect, a runtime update type error carries its OWN reason.
        (code, _, reasons) = await Cancelled(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems = [TypeErrorUpdate(), PutOf("baseline-put-2")],
        }));
        Ensure(code == "TransactionCanceledException", $"baseline: expected TransactionCanceledException, got {Py.Repr(code)}");
        Ensure(
            Py.Equal(reasons[0], R("ValidationError", UpdateTypeError)),
            $"baseline: ADD on a non-numeric attribute must cancel with its own reason, got {Py.Repr(reasons[0])}");
        Ensure(Py.Equal(reasons[1], R("None")), $"baseline: the passing Put's slot must carry Code='None', got {Py.Repr(reasons[1])}");

        // 1. A LATER key-schema mismatch suppresses an EARLIER failing ConditionCheck into Code='None'.
        (code, _, reasons) = await Cancelled(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems = [CondCheckFail(), WrongKeyDelete()],
        }));
        Ensure(code == "TransactionCanceledException", $"case 1: expected TransactionCanceledException, got {Py.Repr(code)}");
        Ensure(
            Py.Equal(reasons[0], R("None")),
            $"case 1: the key-schema mismatch must suppress the earlier failing ConditionCheck to 'None', got {Py.Repr(reasons[0])}");
        Ensure(
            Py.Equal(reasons[1], R("ValidationError", KeySchema)),
            $"case 1: the mismatched slot must carry the exact schema-mismatch reason, got {Py.Repr(reasons[1])}");

        // 2. A key-schema mismatch FIRST suppresses a LATER failing ConditionCheck the same way.
        (code, _, reasons) = await Cancelled(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems = [WrongKeyDelete(), CondCheckFail()],
        }));
        Ensure(code == "TransactionCanceledException", $"case 2: expected TransactionCanceledException, got {Py.Repr(code)}");
        Ensure(
            Py.Equal(reasons[0], R("ValidationError", KeySchema)),
            $"case 2: the mismatched slot must carry the exact schema-mismatch reason, got {Py.Repr(reasons[0])}");
        Ensure(
            Py.Equal(reasons[1], R("None")),
            $"case 2: the key-schema mismatch must suppress the later failing ConditionCheck to 'None', got {Py.Repr(reasons[1])}");

        // 3. A key-schema mismatch suppresses an EARLIER runtime update type error into Code='None' too.
        (code, _, reasons) = await Cancelled(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems = [TypeErrorUpdate(), WrongKeyDelete()],
        }));
        Ensure(code == "TransactionCanceledException", $"case 3: expected TransactionCanceledException, got {Py.Repr(code)}");
        Ensure(
            Py.Equal(reasons[0], R("None")),
            $"case 3: the key-schema mismatch must suppress the earlier update type error to 'None', got {Py.Repr(reasons[0])}");
        Ensure(
            Py.Equal(reasons[1], R("ValidationError", KeySchema)),
            $"case 3: the mismatched slot must carry the exact schema-mismatch reason, got {Py.Repr(reasons[1])}");

        // Nothing from any of the five cancelled transactions above was ever written.
        var got = await Get(ddb, table, typedKey);
        Ensure(Py.Equal(got, typedItem), $"a cancelled transaction changed the typed item to {Py.Repr(got)}");
        Ensure(await Get(ddb, table, missingKey) is null, "a cancelled transaction created the missing-target item");
        var names = (await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true })).Items.Select(i => i["pk"].S).ToHashSet();
        Ensure(
            names.SetEquals(["typed"]),
            $"the table holds {Py.Repr(names.Order(StringComparer.Ordinal))} after only cancelled transactions, expected only 'typed'");

        // Positive control: a well-formed transaction against the same table still commits.
        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = [PutOf("control")] });
        Ensure(
            Py.Equal(await Get(ddb, table, Item(("pk", S("control")))), Item(("pk", S("control")))),
            "a well-formed TransactWriteItems after the cancelled ones did not commit");
    }

    /// <summary>A reason as boto3 hands it back: <c>{'Code': code, 'Message': message}</c>, without a null Message.</summary>
    private static Dictionary<string, object?> R(string? code, string? message = null)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (code is not null)
        {
            result["Code"] = code;
        }

        if (message is not null)
        {
            result["Message"] = message;
        }

        return result;
    }

    /// <summary>(Error.Code, Error.Message, CancellationReasons) of the TransactionCanceledException fn() raises.</summary>
    private static async Task<(string? Code, string? Message, List<Dictionary<string, object?>> Reasons)> Cancelled(Func<Task> fn)
    {
        var err = await FidelityContext.CaptureError(fn) ?? throw Fail("expected the transaction to be cancelled, but it succeeded");
        var reasons = err.CancellationReasons()?.Select(r => R(r.Code, r.Message)).ToList()
            ?? throw Fail($"expected CancellationReasons, got none: {err}");
        return (err.Code, err.Message, reasons);
    }

    /// <summary>Poll DescribeTable every 2 s until the table is ACTIVE.</summary>
    private static async Task WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var left = ctx.TimeLeftSeconds;
        var bound = Math.Max(0.0, (left == 0 ? 300.0 : left) - 30.0);
        var clock = FidelityTime.StartNew();
        TableStatus? status = null;
        while (clock.Elapsed.TotalSeconds < bound)
        {
            status = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = table })).Table.TableStatus;
            if (status == TableStatus.ACTIVE)
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {Py.Repr(table)} not ACTIVE before the deadline, last status {Py.Repr(status)}");
    }

    /// <summary>The item at key read with ConsistentRead, or None.</summary>
    private static async Task<Dictionary<string, AttributeValue>?> Get(IAmazonDynamoDB ddb, string table, Dictionary<string, AttributeValue> key) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;

    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };
}
