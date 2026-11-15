// Translated from behavioral/dynamodb/check_transact_write_multi_table_atomicity.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-multi-table-atomicity</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteMultiTableAtomicity
{
    private const string ErrTransactionCanceled = "TransactionCanceledException";

    /// <summary>
    /// Call fn(); return its botocore (code, message). An unexpected SUCCESS raises, because an
    /// emulator ACCEPTING input real AWS refuses is the strongest signal available.
    /// </summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, err) = await FidelityContext.Capture(fn);
        if (err is not null)
        {
            return (err.Code, err.Message);
        }

        throw Fail($"expected {ErrTransactionCanceled}, but the call SUCCEEDED and returned {result}");
    }

    private static async Task<string> SimpleTable(FidelityContext ctx, IAmazonDynamoDB ddb, string prefix)
    {
        var name = FidelityContext.UniqueName(prefix);
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = "transact-write-multi-table-atomicity" }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 250);
        return name;
    }

    /// <summary>
    /// A TransactWriteItems call spanning TWO distinct tables commits both tables' writes together
    /// when every condition holds, and rolls back BOTH tables' writes together (including the op with
    /// no condition of its own) when a condition on either table fails.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-multi-table-atomicity")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableA = await SimpleTable(ctx, ddb, "sim2real-behav-twma-a-");
        var tableB = await SimpleTable(ctx, ddb, "sim2real-behav-twma-b-");
        var keyA = Item(("pk", S("row-a")));
        var keyB = Item(("pk", S("row-b")));
        await ddb.PutItemAsync(tableA, Item(("pk", S("row-a")), ("status", S("ok"))));
        await ddb.PutItemAsync(tableB, Item(("pk", S("row-b")), ("v", N("1"))));

        // --- success: both tables mutate together.
        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem
                {
                    ConditionCheck = new ConditionCheck
                    {
                        TableName = tableA,
                        Key = keyA,
                        ConditionExpression = "#s = :ok",
                        ExpressionAttributeNames = Names(("#s", "status")),
                        ExpressionAttributeValues = Item((":ok", S("ok"))),
                    },
                },
                new TransactWriteItem
                {
                    Update = new Update
                    {
                        TableName = tableB,
                        Key = keyB,
                        UpdateExpression = "SET v = :two",
                        ExpressionAttributeValues = Item((":two", N("2"))),
                    },
                },
            ],
        });
        var bAfterSuccess = (await ddb.GetItemAsync(new GetItemRequest { TableName = tableB, Key = keyB, ConsistentRead = true })).Item;
        Ensure(Py.Equal(bAfterSuccess["v"], N("2")), Py.Repr(bAfterSuccess));

        // --- failure: table_b's condition fails -> table_a's unconditioned Put must ALSO roll back.
        var (code, message) = await Error(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem
                {
                    Put = new Put
                    {
                        TableName = tableA,
                        Item = Item(("pk", S("row-a-new")), ("created", Bool(true))),
                    },
                },
                new TransactWriteItem
                {
                    Update = new Update
                    {
                        TableName = tableB,
                        Key = keyB,
                        UpdateExpression = "SET v = :three",
                        ConditionExpression = "v = :wrong",
                        ExpressionAttributeValues = Item((":three", N("3")), (":wrong", N("999"))),
                    },
                },
            ],
        }));
        Ensure(code == ErrTransactionCanceled, $"expected {ErrTransactionCanceled}, got {code}");
        const string expectedMessage =
            "Transaction cancelled, please refer cancellation reasons for specific reasons "
            + "[None, ConditionalCheckFailed]";
        Ensure(message == expectedMessage, $"expected message {Py.Repr(expectedMessage)}, got {Py.Repr(message)}");

        var neverWritten = await ddb.GetItemAsync(new GetItemRequest { TableName = tableA, Key = Item(("pk", S("row-a-new"))), ConsistentRead = true });
        Ensure(neverWritten.Item is null, "cross-table sibling op must not partially apply on rollback");
        var bUnchanged = (await ddb.GetItemAsync(new GetItemRequest { TableName = tableB, Key = keyB, ConsistentRead = true })).Item;
        Ensure(Py.Equal(bUnchanged["v"], N("2")), "the failing table item must remain at its pre-txn value");
    }
}
