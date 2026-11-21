// Translated from behavioral/dynamodb/check_transact_write_items_update_runtime_arithmetic_error_reports_validation_error_reason.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-items-update-runtime-arithmetic-error-reports-validation-error-reason</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteItemsUpdateRuntimeArithmeticErrorReportsValidationErrorReason
{
    private const string Check = "transact-write-items-update-runtime-arithmetic-error-reports-validation-error-reason";
    private const double TableActiveBudgetS = 120.0;
    private const double TeardownWaitS = 120.0;
    private const string ErrCanceled = "TransactionCanceledException";
    private const string CanceledPrefix = "Transaction cancelled, please refer cancellation reasons for specific reasons ";
    private const string TypeMismatchMsg = "An operand in the update expression has an incorrect data type";

    /// <summary>
    /// A TransactWriteItems member's runtime arithmetic type error reports ValidationError on its
    /// own CancellationReasons slot, positionally aligned with the request regardless of order.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_TransactWriteItems.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-items-update-runtime-arithmetic-error-reports-validation-error-reason")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx, ddb);

        var lockedKey = Item(("pk", S("locked")));
        await ddb.PutItemAsync(table, With(lockedKey, ("status", S("LOCKED"))));
        var counterKey = Item(("pk", S("counter")));
        // ``n`` is stored as a String; "+" needs the stored value's type read at runtime to detect
        // the mismatch, unlike a request-literal type mismatch, which DynamoDB rejects up front.
        var counterItem = With(counterKey, ("n", S("not-a-number")));
        await ddb.PutItemAsync(table, counterItem);

        TransactWriteItem CcMember(string expected) => new()
        {
            ConditionCheck = new ConditionCheck
            {
                TableName = table,
                Key = lockedKey,
                ConditionExpression = "#s = :expected",
                ExpressionAttributeNames = Names(("#s", "status")),
                ExpressionAttributeValues = Item((":expected", S(expected))),
            },
        };
        TransactWriteItem UpdateMember(Dictionary<string, AttributeValue> key) => new()
        {
            Update = new Update
            {
                TableName = table,
                Key = key,
                UpdateExpression = "SET n = n + :one",
                ExpressionAttributeValues = Item((":one", N("1"))),
            },
        };
        var harmlessPutAKey = Item(("pk", S("harmless-a")));

        // Order 1: ConditionCheck, Update, Put — the condition fails first; the runtime arithmetic
        // error on the Update still reports its own reason; the untouched Put reports "None".
        var err = await Canceled(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems = [CcMember("UNLOCKED"), UpdateMember(counterKey), new TransactWriteItem { Put = new Put { TableName = table, Item = harmlessPutAKey } }],
        }));
        Ensure(err.Code == ErrCanceled, $"expected {ErrCanceled}, got {err}");
        var expectedMessage = CanceledPrefix + "[ConditionalCheckFailed, ValidationError, None]";
        Ensure(err.Message == expectedMessage, $"expected message {Py.Repr(expectedMessage)}, got {Py.Repr(err.Message)}");
        var reasons = err.CancellationReasons();
        Ensure(reasons is not null && reasons.Count == 3, $"expected 3 cancellation reasons, got {Py.Repr(reasons?.Select(Pair))}");
        var (ccReason, updateReason, putReason) = (reasons![0], reasons[1], reasons[2]);
        Ensure(ccReason.Code == "ConditionalCheckFailed", $"member 0 should be ConditionalCheckFailed, got {Py.Repr(Pair(ccReason))}");
        Ensure(updateReason.Code == "ValidationError", $"member 1 should be ValidationError, got {Py.Repr(Pair(updateReason))}");
        Ensure(
            updateReason.Message == TypeMismatchMsg,
            $"the runtime arithmetic error must carry {Py.Repr(TypeMismatchMsg)}, got {Py.Repr(updateReason.Message)}");
        Ensure(putReason.Code == "None", $"member 2 should report Code 'None', got {Py.Repr(Pair(putReason))}");
        Ensure(putReason.Item is null, $"the non-failing member must not carry an Item, got {Py.Repr(putReason.Item)}");

        var unchanged = await Get(ddb, table, counterKey);
        Ensure(Py.Equal(unchanged, counterItem), $"the canceled Update must leave the counter unchanged, got {Py.Repr(unchanged)}");
        Ensure(await Get(ddb, table, harmlessPutAKey) is null, "the canceled transaction must not have created the untouched Put's item");

        // Order 2: Delete, Update, ConditionCheck. CancellationReasons stay positionally aligned with
        // the request order, not with evaluation or failure order; the Delete member, which would have
        // succeeded on its own, reports "None" even though the whole transaction is canceled.
        var deletableKey = Item(("pk", S("deletable")));
        var deletableItem = With(deletableKey, ("v", S("x")));
        await ddb.PutItemAsync(table, deletableItem);
        err = await Canceled(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem { Delete = new Delete { TableName = table, Key = deletableKey } },
                UpdateMember(counterKey),
                CcMember("UNLOCKED"),
            ],
        }));
        Ensure(err.Code == ErrCanceled, $"expected {ErrCanceled}, got {err}");
        expectedMessage = CanceledPrefix + "[None, ValidationError, ConditionalCheckFailed]";
        Ensure(err.Message == expectedMessage, $"expected message {Py.Repr(expectedMessage)}, got {Py.Repr(err.Message)}");
        reasons = err.CancellationReasons();
        Ensure(reasons is not null && reasons.Count == 3, $"expected 3 cancellation reasons, got {Py.Repr(reasons?.Select(Pair))}");
        var deleteReason = reasons![0];
        (updateReason, ccReason) = (reasons[1], reasons[2]);
        Ensure(deleteReason.Code == "None", $"member 0 should report Code 'None', got {Py.Repr(Pair(deleteReason))}");
        Ensure(updateReason.Code == "ValidationError", $"member 1 should be ValidationError, got {Py.Repr(Pair(updateReason))}");
        Ensure(
            updateReason.Message == TypeMismatchMsg,
            $"the runtime arithmetic error must carry {Py.Repr(TypeMismatchMsg)}, got {Py.Repr(updateReason.Message)}");
        Ensure(ccReason.Code == "ConditionalCheckFailed", $"member 2 should be ConditionalCheckFailed, got {Py.Repr(Pair(ccReason))}");

        unchanged = await Get(ddb, table, counterKey);
        Ensure(Py.Equal(unchanged, counterItem), $"the second canceled Update must still leave the counter unchanged, got {Py.Repr(unchanged)}");
        var stillThere = await Get(ddb, table, deletableKey);
        Ensure(Py.Equal(stillThere, deletableItem), $"the canceled transaction must not have applied the Delete member, got {Py.Repr(stillThere)}");

        // Positive control: the same three member kinds, all satisfiable, commit together. A
        // separate numeric-typed item stands in for the Update member, since the runtime-mismatched
        // counter above never holds a usable numeric value to add to.
        var numericKey = Item(("pk", S("numeric")));
        await ddb.PutItemAsync(table, With(numericKey, ("n", N("5"))));
        var harmlessPutCKey = Item(("pk", S("harmless-c")));
        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem { Put = new Put { TableName = table, Item = harmlessPutCKey } },
                UpdateMember(numericKey),
                CcMember("LOCKED"),
            ],
        });
        var committed = await Get(ddb, table, numericKey);
        Ensure(Py.Equal(committed, With(numericKey, ("n", N("6")))), $"a committed transaction must apply the Update, got {Py.Repr(committed)}");
        Ensure(Py.Equal(await Get(ddb, table, harmlessPutCKey), harmlessPutCKey), "a committed transaction must apply the sibling Put");
    }

    private static (string Code, string Message) Pair(CancellationReason r) => (r.Code, r.Message);

    /// <summary>The error <paramref name="fn"/> raises; fails if it unexpectedly succeeds.</summary>
    private static async Task<AwsError> Canceled(Func<Task> fn) =>
        await FidelityContext.CaptureError(fn) ?? throw Fail("expected the transaction to be canceled, but it succeeded");

    private static async Task DeleteTableWhenPossible(IAmazonDynamoDB ddb, string tableName)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(tableName));
            if (error is null || error.Code == "ResourceNotFoundException")
            {
                return;
            }

            if (error.Code == "ResourceInUseException" && clock.Elapsed.TotalSeconds < TeardownWaitS)
            {
                await FidelityTime.Delay(TimeSpan.FromSeconds(5));
                continue;
            }

            throw error.Exception;
        }
    }

    private static async Task<string> CreateTable(FidelityContext ctx, IAmazonDynamoDB ddb)
    {
        var name = FidelityContext.UniqueName("sim2real-behav-ddb-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => DeleteTableWhenPossible(ddb, name));
        var clock = FidelityTime.StartNew();
        TableStatus? status = null;
        while (clock.Elapsed.TotalSeconds < TableActiveBudgetS)
        {
            status = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = name })).Table.TableStatus;
            if (status == TableStatus.ACTIVE)
            {
                return name;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {Py.Repr(name)} not ACTIVE within {TableActiveBudgetS:0.0}s: {Py.Repr(status)}");
    }

    /// <summary><c>{**item, name: value, ...}</c>.</summary>
    private static Dictionary<string, AttributeValue> With(Dictionary<string, AttributeValue> item, params (string Name, AttributeValue Value)[] entries)
    {
        var result = new Dictionary<string, AttributeValue>(item, StringComparer.Ordinal);
        foreach (var (name, value) in entries)
        {
            result[name] = value;
        }

        return result;
    }

    private static async Task<Dictionary<string, AttributeValue>?> Get(IAmazonDynamoDB ddb, string table, Dictionary<string, AttributeValue> key) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
}
