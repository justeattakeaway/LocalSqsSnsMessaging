// Translated from behavioral/dynamodb/check_transact_write_condition_non_orderable_literal_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-condition-non-orderable-literal-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteConditionNonOrderableLiteralRejected
{
    private const string OperandTypeMsg =
        "Invalid ConditionExpression: Incorrect operand type for operator or function; "
        + "operator or function: >, operand type: BOOL";

    private const string CancelledMsg =
        "Transaction cancelled, please refer cancellation reasons for specific reasons "
        + "[ConditionalCheckFailed]";

    private static Dictionary<string, AttributeValue> Base() => Item(("pk", S("p1")), ("n", N("5")), ("note", S("orig")));

    /// <summary>
    /// A TransactWriteItems Put, Delete, Update or ConditionCheck whose condition orders against a
    /// BOOL value is a top-level ValidationException, not a cancellation, and writes nothing.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_TransactWriteItems.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-condition-non-orderable-literal-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-txn-order-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), ["ResourceNotFoundException"]));
        await WaitActive(ddb, ctx, table);
        Dictionary<string, AttributeValue> Key() => Item(("pk", S("p1")));
        await ddb.PutItemAsync(table, Base());
        Dictionary<string, AttributeValue> Bad() => Item((":v", Bool(true)));

        var actions = new (string Name, Func<TransactWriteItem> Action)[]
        {
            ("Put", () => new TransactWriteItem
            {
                Put = new Put
                {
                    TableName = table,
                    Item = Item(("pk", S("p1")), ("n", N("5")), ("note", S("put"))),
                    ConditionExpression = "n > :v",
                    ExpressionAttributeValues = Bad(),
                },
            }),
            ("Delete", () => new TransactWriteItem
            {
                Delete = new Delete { TableName = table, Key = Key(), ConditionExpression = "n > :v", ExpressionAttributeValues = Bad() },
            }),
            ("Update", () => new TransactWriteItem
            {
                Update = new Update
                {
                    TableName = table,
                    Key = Key(),
                    UpdateExpression = "SET note = :c",
                    ConditionExpression = "n > :v",
                    ExpressionAttributeValues = Item((":v", Bool(true)), (":c", S("update"))),
                },
            }),
            ("ConditionCheck", () => new TransactWriteItem
            {
                ConditionCheck = new ConditionCheck { TableName = table, Key = Key(), ConditionExpression = "n > :v", ExpressionAttributeValues = Bad() },
            }),
        };
        TransactWriteItem Other() => new() { Put = new Put { TableName = table, Item = Item(("pk", S("p2")), ("note", S("side"))) } };
        foreach (var (name, action) in actions)
        {
            var (code, msg) = await Error(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = [action(), Other()] }));
            Ensure(
                (code, msg) == ("ValidationException", OperandTypeMsg),
                $"TransactWriteItems {name} with condition 'n > :v' (:v BOOL) gave {Py.Repr(code)}: {Py.Repr(msg)}");
        }

        var item = await Get(ddb, table, Key());
        Ensure(Py.Equal(item, Base()), $"item after the refused transactions is {Py.Repr(item)}, expected {Py.Repr(Base())}");
        var side = await Get(ddb, table, Item(("pk", S("p2"))));
        Ensure(side is null, $"the sibling Put of a refused transaction was written: {Py.Repr(side)}");

        {
            var (code, msg) = await Error(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems =
                [
                    new TransactWriteItem
                    {
                        ConditionCheck = new ConditionCheck
                        {
                            TableName = table,
                            Key = Key(),
                            ConditionExpression = "n > :v",
                            ExpressionAttributeValues = Item((":v", N("9"))),
                        },
                    },
                ],
            }));
            Ensure(
                (code, msg) == ("TransactionCanceledException", CancelledMsg),
                $"a well-formed but false transaction condition (5 > 9) gave {Py.Repr(code)}: {Py.Repr(msg)}");
        }

        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem
                {
                    Update = new Update
                    {
                        TableName = table,
                        Key = Key(),
                        UpdateExpression = "SET note = :c",
                        ConditionExpression = "n > :v",
                        ExpressionAttributeValues = Item((":v", N("0")), (":c", S("update"))),
                    },
                },
                Other(),
            ],
        });
        item = await Get(ddb, table, Key());
        Ensure(
            Py.Equal(item, Item(("pk", S("p1")), ("n", N("5")), ("note", S("update")))),
            $"the valid transaction left {Py.Repr(item)}");
        side = await Get(ddb, table, Item(("pk", S("p2"))));
        Ensure(Py.Equal(side, Other().Put.Item), $"the valid transaction's sibling Put left {Py.Repr(side)}");
    }

    /// <summary>A cleanup running undo() where a failed delete raises; in-use is retried every 2 s.</summary>
    private static Func<Task> Teardown(Func<Task> undo, string[] notFound, double timeoutSeconds = 300.0) => async () =>
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(undo);
            if (error is null || notFound.Contains(error.Code))
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed.TotalSeconds >= timeoutSeconds)
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    };

    /// <summary>Poll DescribeTable every 2 s until the table and its indexes are ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var clock = FidelityTime.StartNew();
        var timeLeft = ctx.TimeLeftSeconds;
        var deadline = Math.Max(0.0, (timeLeft > 0 ? timeLeft : 300.0) - 30.0);
        TableDescription? desc = null;
        while (clock.Elapsed.TotalSeconds < deadline)
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            if (desc.TableStatus == TableStatus.ACTIVE
                && (desc.GlobalSecondaryIndexes ?? []).All(g => g.IndexStatus == IndexStatus.ACTIVE))
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {desc?.TableStatus}");
    }

    /// <summary>(Error.Code, Error.Message) of the error <paramref name="fn"/> raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn)
            ?? throw Fail("expected the call to be refused, but it succeeded");
        return (error.Code, error.Message);
    }

    private static async Task<Dictionary<string, AttributeValue>?> Get(IAmazonDynamoDB ddb, string table, Dictionary<string, AttributeValue> key) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
}
