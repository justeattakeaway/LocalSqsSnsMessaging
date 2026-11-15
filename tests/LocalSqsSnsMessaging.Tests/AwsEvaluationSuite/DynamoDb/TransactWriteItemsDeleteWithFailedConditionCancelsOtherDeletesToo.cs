// Translated from behavioral/dynamodb/check_transact_write_items_delete_with_failed_condition_cancels_other_deletes_too.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-items-delete-with-failed-condition-cancels-other-deletes-too</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteItemsDeleteWithFailedConditionCancelsOtherDeletesToo
{
    private const string Check = "transact-write-items-delete-with-failed-condition-cancels-other-deletes-too";

    private static readonly (string Code, string Message) TxCancelled = (
        "TransactionCanceledException",
        "Transaction cancelled, please refer cancellation reasons for specific reasons [None, ConditionalCheckFailed, None]");

    private static readonly string[] ControlKeys = ["tx-del-ctl-a", "tx-del-ctl-b"];
    private static readonly string[] Keys = ["tx-del-a", "tx-del-b", "tx-del-c"];

    /// <summary>
    /// One Delete's failed ConditionExpression cancels a TransactWriteItems entirely: the other
    /// Deletes in the same transaction do not apply, even though they carry no condition of their own.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-items-delete-with-failed-condition-cancels-other-deletes-too")]
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

        // Positive control: an unconditioned transaction of several distinct Deletes all apply.
        foreach (var key in ControlKeys)
        {
            await ddb.PutItemAsync(table, Item(("pk", S(key))));
        }

        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem { Delete = new Delete { TableName = table, Key = Item(("pk", S("tx-del-ctl-a"))) } },
                new TransactWriteItem { Delete = new Delete { TableName = table, Key = Item(("pk", S("tx-del-ctl-b"))) } },
            ],
        });
        foreach (var key in ControlKeys)
        {
            Ensure(
                (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(key))), ConsistentRead = true })).Item is null,
                $"control: an unconditioned transactional Delete of {Py.Repr(key)} must have applied");
        }

        // The negative: three items, the middle Delete's condition fails on an existing item.
        foreach (var key in Keys)
        {
            await ddb.PutItemAsync(table, Item(("pk", S(key))));
        }

        Task TransactDeleteThree() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem { Delete = new Delete { TableName = table, Key = Item(("pk", S("tx-del-a"))) } },
                new TransactWriteItem
                {
                    Delete = new Delete { TableName = table, Key = Item(("pk", S("tx-del-b"))), ConditionExpression = "attribute_not_exists(pk)" },
                },
                new TransactWriteItem { Delete = new Delete { TableName = table, Key = Item(("pk", S("tx-del-c"))) } },
            ],
        });
        await ExpectError(TransactDeleteThree, TxCancelled.Code, TxCancelled.Message);

        foreach (var key in Keys)
        {
            var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(key))), ConsistentRead = true })).Item;
            Ensure(Py.Equal(got, Item(("pk", S(key)))), $"the cancelled transaction must leave {Py.Repr(key)} untouched, got {Py.Repr(got)}");
        }
    }

    /// <summary>Call <paramref name="fn"/>, which must raise an error with exactly this (code, message).</summary>
    private static async Task ExpectError(Func<Task> fn, string code, string message)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is null)
        {
            throw Fail($"expected a refusal {Py.Repr((code, message))}, the call succeeded");
        }

        var got = (error.Code, error.Message);
        Ensure(got == (code, message), $"expected {Py.Repr((code, message))}, got {Py.Repr(got)}");
    }
}
