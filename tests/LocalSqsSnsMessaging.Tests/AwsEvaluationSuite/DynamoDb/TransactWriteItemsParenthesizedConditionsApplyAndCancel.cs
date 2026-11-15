// Translated from behavioral/dynamodb/check_transact_write_items_parenthesized_conditions_apply_and_cancel.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-items-parenthesized-conditions-apply-and-cancel</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteItemsParenthesizedConditionsApplyAndCancel
{
    private const string Check = "transact-write-items-parenthesized-conditions-apply-and-cancel";

    private static readonly (string Code, string Message) TxnCanceled = (
        "TransactionCanceledException",
        "Transaction cancelled, please refer cancellation reasons for specific reasons [None, ConditionalCheckFailed]");

    /// <summary>A cleanup running undo() where a failed delete raises.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };

    /// <summary>Poll DescribeTable every 2 s until the table is ACTIVE against a deadline.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var timeLeft = ctx.TimeLeftSeconds;
        var limit = Math.Max(0.0, (timeLeft == 0 ? 300.0 : timeLeft) - 30.0);
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(limit))
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            if (desc.TableStatus == TableStatus.ACTIVE)
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {desc?.TableStatus?.Value}");
    }

    /// <summary>The item at pk read with ConsistentRead, or None.</summary>
    private static async Task<Dictionary<string, AttributeValue>?> Get(IAmazonDynamoDB ddb, string table, string pk) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;

    /// <summary>Assert fn() raises an error with exactly (code, message); re-raise an unexpected success.</summary>
    private static async Task<List<Dictionary<string, object?>>?> ExpectError<T>(Func<Task<T>> fn, string code, string message)
        where T : class
    {
        var (result, err) = await FidelityContext.Capture(fn);
        if (err is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        var got = (err.Code, err.Message);
        Ensure(got == (code, message), $"expected {Py.Repr((code, message))}, got {Py.Repr(got)}");
        return err.CancellationReasons()?.Select(Reason).ToList();
    }

    /// <summary>A cancellation reason as boto3 hands it back.</summary>
    private static Dictionary<string, object?> Reason(CancellationReason reason)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (reason.Item is not null)
        {
            result["Item"] = reason.Item;
        }

        if (reason.Code is not null)
        {
            result["Code"] = reason.Code;
        }

        if (reason.Message is not null)
        {
            result["Message"] = reason.Message;
        }

        return result;
    }

    /// <summary>
    /// A parenthesized ConditionExpression in Put, Update, Delete and ConditionCheck evaluates
    /// like its unparenthesized form; a failing one cancels the whole transaction, unchanged state.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-items-parenthesized-conditions-apply-and-cancel")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-txpar-");
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

        var putSeed = Item(("pk", S("tw-put")), ("status", S("pending")), ("qty", N("5")));
        var updateSeed = Item(("pk", S("tw-update")), ("status", S("active")), ("qty", N("10")));
        var deleteSeed = Item(("pk", S("tw-delete")), ("status", S("ready")), ("qty", N("0")));
        var checkSeed = Item(("pk", S("tw-check")), ("status", S("checked")), ("qty", N("1")));
        foreach (var item in new[] { putSeed, updateSeed, deleteSeed, checkSeed })
        {
            await ddb.PutItemAsync(table, item);
        }

        // Per-condition "(a) AND (b)" on Put, full-wrap "(a AND b)" on Update, nested
        // "(a AND (b))" on Delete, per-condition again on ConditionCheck: each must pass
        // and apply exactly as its unparenthesized form would.
        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem
                {
                    Put = new Put
                    {
                        TableName = table,
                        Item = Item(("pk", S("tw-put")), ("status", S("done")), ("qty", N("99"))),
                        ConditionExpression = "(#s = :s) AND (qty = :q)",
                        ExpressionAttributeNames = Names(("#s", "status")),
                        ExpressionAttributeValues = Item((":s", S("pending")), (":q", N("5"))),
                    },
                },
                new TransactWriteItem
                {
                    Update = new Update
                    {
                        TableName = table,
                        Key = Item(("pk", S("tw-update"))),
                        UpdateExpression = "SET qty = :newq",
                        ConditionExpression = "(#s = :s AND qty = :q)",
                        ExpressionAttributeNames = Names(("#s", "status")),
                        ExpressionAttributeValues = Item((":s", S("active")), (":q", N("10")), (":newq", N("20"))),
                    },
                },
                new TransactWriteItem
                {
                    Delete = new Delete
                    {
                        TableName = table,
                        Key = Item(("pk", S("tw-delete"))),
                        ConditionExpression = "(#s = :s AND (qty = :q))",
                        ExpressionAttributeNames = Names(("#s", "status")),
                        ExpressionAttributeValues = Item((":s", S("ready")), (":q", N("0"))),
                    },
                },
                new TransactWriteItem
                {
                    ConditionCheck = new ConditionCheck
                    {
                        TableName = table,
                        Key = Item(("pk", S("tw-check"))),
                        ConditionExpression = "(#s = :s) AND (qty = :q)",
                        ExpressionAttributeNames = Names(("#s", "status")),
                        ExpressionAttributeValues = Item((":s", S("checked")), (":q", N("1"))),
                    },
                },
            ],
        });

        var gotPut = await Get(ddb, table, "tw-put");
        Ensure(
            Py.Equal(gotPut, Item(("pk", S("tw-put")), ("status", S("done")), ("qty", N("99")))),
            $"Put with a per-condition parenthesized ConditionExpression gave {Py.Repr(gotPut)}");
        var gotUpdate = await Get(ddb, table, "tw-update");
        Ensure(
            Py.Equal(gotUpdate, Item(("pk", S("tw-update")), ("status", S("active")), ("qty", N("20")))),
            $"Update with a fully-wrapped parenthesized ConditionExpression gave {Py.Repr(gotUpdate)}");
        var gotDelete = await Get(ddb, table, "tw-delete");
        Ensure(gotDelete is null, $"Delete with a nested parenthesized ConditionExpression left {Py.Repr(gotDelete)}");
        var gotCheck = await Get(ddb, table, "tw-check");
        Ensure(
            Py.Equal(gotCheck, checkSeed),
            $"ConditionCheck with a per-condition parenthesized ConditionExpression changed the item to {Py.Repr(gotCheck)}");

        // A failing parenthesized condition (nested form, deliberately wrong qty) cancels the
        // whole transaction: the sibling Put is rolled back and the Update target is unchanged.
        var reasons = await ExpectError(
            () => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems =
                [
                    new TransactWriteItem
                    {
                        Put = new Put { TableName = table, Item = Item(("pk", S("tw-neg-put")), ("status", S("new")), ("qty", N("1"))) },
                    },
                    new TransactWriteItem
                    {
                        Update = new Update
                        {
                            TableName = table,
                            Key = Item(("pk", S("tw-update"))),
                            UpdateExpression = "SET qty = :newq2",
                            ConditionExpression = "(#s = :s2 AND (qty = :qwrong))",
                            ExpressionAttributeNames = Names(("#s", "status")),
                            ExpressionAttributeValues = Item((":s2", S("active")), (":qwrong", N("999")), (":newq2", N("30"))),
                        },
                    },
                ],
            }),
            TxnCanceled.Code,
            TxnCanceled.Message);
        var expectedReasons = new List<Dictionary<string, object?>>
        {
            new(StringComparer.Ordinal) { ["Code"] = "None" },
            new(StringComparer.Ordinal) { ["Code"] = "ConditionalCheckFailed", ["Message"] = "The conditional request failed" },
        };
        Ensure(Py.Equal(reasons, expectedReasons), $"cancellation reasons were {Py.Repr(reasons)}");
        var gotNegPut = await Get(ddb, table, "tw-neg-put");
        Ensure(gotNegPut is null, $"the canceled transaction's sibling Put landed: {Py.Repr(gotNegPut)}");
        var gotUpdateAfter = await Get(ddb, table, "tw-update");
        Ensure(
            Py.Equal(gotUpdateAfter, gotUpdate),
            $"tw-update changed to {Py.Repr(gotUpdateAfter)} despite the canceled transaction");
    }
}
