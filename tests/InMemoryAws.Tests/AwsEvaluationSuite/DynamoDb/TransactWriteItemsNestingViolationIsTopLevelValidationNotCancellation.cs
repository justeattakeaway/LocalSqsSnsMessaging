// Translated from behavioral/dynamodb/check_transact_write_items_nesting_violation_is_top_level_validation_not_cancellation.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-items-nesting-violation-is-top-level-validation-not-cancellation</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteItemsNestingViolationIsTopLevelValidationNotCancellation
{
    private const string Check = "transact-write-items-nesting-violation-is-top-level-validation-not-cancellation";
    private const string ErrValidation = "ValidationException";
    private const string ErrCancelled = "TransactionCanceledException";
    private const string NestingMessage =
        "Nesting Levels have exceeded supported limits: Attributes in the item have nested levels beyond supported limit";

    /// <summary>A chain of `levels` nested maps around one scalar leaf, `levels` + 1 deep in total.</summary>
    private static AttributeValue Deep(int levels)
    {
        var value = S("leaf");
        for (var i = 0; i < levels; i++)
        {
            value = M(("n", value));
        }

        return value;
    }

    /// <summary>(Error.Code, Error.Message) of the error fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be rejected, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }

    /// <summary>
    /// A 33-level item in a TransactWriteItems Put, or a 33-level value in its Update SET, is a
    /// top-level ValidationException for the whole call, never a per-action TransactionCanceledException,
    /// and neither action's effect lands.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-items-nesting-violation-is-top-level-validation-not-cancellation")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-ddbtxn-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 30);

        var deep = Deep(32); // 33 levels, one past the limit.
        await ddb.PutItemAsync(t, Item(("pk", S("update-target")), ("ctr", N("1"))));

        // The over-deep item is the Put action; the Update action, on a different item, is itself valid.
        var (code, message) = await Error(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem { Put = new Put { TableName = t, Item = Item(("pk", S("new-item-via-put")), ("v", deep)) } },
                new TransactWriteItem
                {
                    Update = new Update
                    {
                        TableName = t,
                        Key = Item(("pk", S("update-target"))),
                        UpdateExpression = "SET ctr = :two",
                        ExpressionAttributeValues = Item((":two", N("2"))),
                    },
                },
            ],
        }));
        Ensure(code == ErrValidation, $"a 33-level TransactWriteItems Put must be {ErrValidation} for the whole call, got {Py.Repr(code)}: {Py.Repr(message)}");
        Ensure(code != ErrCancelled, $"nesting-depth validation is a top-level request error, never a per-action cancellation, got {Py.Repr(code)}");
        Ensure(message == NestingMessage, $"expected the exact ValidationException message, got {Py.Repr(message)}");
        var newItem = await Get(ddb, t, "new-item-via-put");
        Ensure(newItem is null, $"the over-nested Put must not be written, got {Py.Repr(newItem)}");
        var unchanged = await Get(ddb, t, "update-target");
        Ensure(
            Py.Equal(unchanged?.GetValueOrDefault("ctr"), N("1")),
            $"the transaction's own valid Update action must not take effect either, got {Py.Repr(unchanged)}");

        // The over-deep value is the Update action's SET; the Put action, on a different item, is valid.
        (code, message) = await Error(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem { Put = new Put { TableName = t, Item = Item(("pk", S("new-item-via-sibling-put")), ("v", S("fine"))) } },
                new TransactWriteItem
                {
                    Update = new Update
                    {
                        TableName = t,
                        Key = Item(("pk", S("update-target"))),
                        UpdateExpression = "SET v = :deep",
                        ExpressionAttributeValues = Item((":deep", deep)),
                    },
                },
            ],
        }));
        Ensure(code == ErrValidation, $"a 33-level TransactWriteItems Update SET must be {ErrValidation} for the whole call, got {Py.Repr(code)}: {Py.Repr(message)}");
        Ensure(code != ErrCancelled, $"nesting-depth validation is a top-level request error, never a per-action cancellation, got {Py.Repr(code)}");
        Ensure(message == NestingMessage, $"expected the exact ValidationException message, got {Py.Repr(message)}");
        var sibling = await Get(ddb, t, "new-item-via-sibling-put");
        Ensure(sibling is null, $"the transaction's own valid Put action must not take effect either, got {Py.Repr(sibling)}");
        unchanged = await Get(ddb, t, "update-target");
        Ensure(
            Py.Equal(unchanged ?? [], Item(("pk", S("update-target")), ("ctr", N("1")))),
            $"the over-nested Update must leave the existing item untouched, got {Py.Repr(unchanged)}");
    }

    private static async Task<Dictionary<string, AttributeValue>?> Get(IAmazonDynamoDB ddb, string table, string pk) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;
}
