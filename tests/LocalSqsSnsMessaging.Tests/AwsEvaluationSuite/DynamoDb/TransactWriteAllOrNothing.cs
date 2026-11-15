// Translated from behavioral/dynamodb/check_transact_write_all_or_nothing.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-all-or-nothing</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteAllOrNothing
{
    private const string CheckId = "transact-write-all-or-nothing";
    private const string ErrTransactionCanceled = "TransactionCanceledException";

    /// <summary>TransactWriteItems is atomic: if one condition fails, nothing is written.</summary>
    [Test]
    [DisplayName("dynamodb.transact-write-all-or-nothing")]
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
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(table, Item(("pk", S("tw-existing")), ("v", N("1"))));

        // Positive control: an unconditioned multi-item transaction with no conflicting
        // condition commits every member. Without this, a target that rejects every
        // multi-item TransactWriteItems call would pass the negative below for the wrong
        // reason.
        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem { Put = new Put { TableName = table, Item = Item(("pk", S("tw-control-a")), ("v", N("1"))) } },
                new TransactWriteItem { Put = new Put { TableName = table, Item = Item(("pk", S("tw-control-b")), ("v", N("1"))) } },
            ],
        });
        var controlA = await Get(ddb, table, "tw-control-a");
        var controlB = await Get(ddb, table, "tw-control-b");
        Ensure(
            controlA is not null && controlB is not null,
            $"positive control: expected both members committed, got a={Py.Repr(controlA)} b={Py.Repr(controlB)}");

        var error = await FidelityContext.CaptureError(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem { Put = new Put { TableName = table, Item = Item(("pk", S("tw-new")), ("v", N("2"))) } },
                new TransactWriteItem
                {
                    Put = new Put
                    {
                        TableName = table,
                        Item = Item(("pk", S("tw-existing")), ("v", N("99"))),
                        ConditionExpression = "attribute_not_exists(pk)",
                    },
                },
            ],
        }));
        var code = error?.Code;
        var message = error?.Message;
        Ensure(
            code == ErrTransactionCanceled,
            $"expected {ErrTransactionCanceled} when one member's condition fails, got {code ?? "None"}");
        Ensure(
            message == "Transaction cancelled, please refer cancellation reasons for specific reasons [None, ConditionalCheckFailed]",
            $"expected AWS's exact TransactionCanceledException message, got {Py.Repr(message)}");
        var got = await Get(ddb, table, "tw-new");
        Ensure(got is null, $"transaction was canceled, so tw-new must not have been written, got {Py.Repr(got)}");
        var gotExisting = await Get(ddb, table, "tw-existing");
        Ensure(
            Py.Equal(gotExisting!["v"], N("1")),
            $"transaction was canceled, so tw-existing must remain at v=1, got {Py.Repr(gotExisting)}");
    }

    private static async Task<Dictionary<string, AttributeValue>?> Get(IAmazonDynamoDB ddb, string table, string pk) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;
}
