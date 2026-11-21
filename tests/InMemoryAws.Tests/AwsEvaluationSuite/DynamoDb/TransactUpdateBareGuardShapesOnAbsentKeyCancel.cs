// Translated from behavioral/dynamodb/check_transact_update_bare_guard_shapes_on_absent_key_cancel.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-update-bare-guard-shapes-on-absent-key-cancel</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactUpdateBareGuardShapesOnAbsentKeyCancel
{
    private const string Check = "transact-update-bare-guard-shapes-on-absent-key-cancel";
    private const double WaitS = 60.0;
    private static readonly (string Code, string Message) CanceledFirstFails = (
        "TransactionCanceledException",
        "Transaction cancelled, please refer cancellation reasons for specific reasons [ConditionalCheckFailed, None]");

    /// <summary>
    /// A TransactWriteItems Update on an absent key cancels the whole transaction and creates no
    /// item, whether its own condition is a bare attribute_exists guarding an ADD, or a plain
    /// comparison with no attribute_exists at all; a passing sibling action still rolls back.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-update-bare-guard-shapes-on-absent-key-cancel")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("id", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("id", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await WaitTableActive(ddb, table);

        // Scenario A: ADD guarded by a bare attribute_exists(id) (no AND, no equality) on an absent key.
        var ghostKey = Item(("id", S("ghost-tally")));
        var siblingAKey = Item(("id", S("sibling-tally-a")));
        var siblingASeed = Item(("id", S("sibling-tally-a")), ("count", N("10")));
        await ddb.PutItemAsync(table, siblingASeed);

        Task<TransactWriteItemsResponse> BumpTally() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem
                {
                    Update = new Update
                    {
                        TableName = table,
                        Key = ghostKey,
                        UpdateExpression = "ADD #c :inc",
                        ConditionExpression = "attribute_exists(id)",
                        ExpressionAttributeNames = Names(("#c", "count")),
                        ExpressionAttributeValues = Item((":inc", N("1"))),
                    },
                },
                new TransactWriteItem
                {
                    Update = new Update
                    {
                        TableName = table,
                        Key = siblingAKey,
                        UpdateExpression = "SET #c = #c + :inc",
                        ConditionExpression = "#c = :expected",
                        ExpressionAttributeNames = Names(("#c", "count")),
                        ExpressionAttributeValues = Item((":inc", N("1")), (":expected", N("10"))),
                    },
                },
            ],
        });

        var excA = await ExpectError(BumpTally, CanceledFirstFails.Code, CanceledFirstFails.Message);
        var reasonsA = (excA.CancellationReasons() ?? []).Select(r => ((string?)r.Code, (string?)r.Message)).ToList();
        Ensure(
            reasonsA.SequenceEqual([("ConditionalCheckFailed", "The conditional request failed"), ("None", null)]),
            $"expected [ConditionalCheckFailed, None] cancellation reasons, got {Py.Repr(reasonsA)}");

        var ghostGot = await Get(ddb, table, ghostKey);
        Ensure(ghostGot is null, $"the cancelled ADD must not create the ghost item, got {Py.Repr(ghostGot)}");
        var siblingAGot = await Get(ddb, table, siblingAKey);
        Ensure(
            Py.Equal(siblingAGot, siblingASeed),
            $"the sibling Update's own condition held, but it must still be rolled back: got {Py.Repr(siblingAGot)}");

        // Positive control: create the ghost item for real, then the identical transaction commits both.
        await ddb.PutItemAsync(table, Item(("id", S("ghost-tally")), ("count", N("5"))));
        await BumpTally();
        var ghostAfter = await Get(ddb, table, ghostKey);
        Ensure(
            Py.Equal(ghostAfter, Item(("id", S("ghost-tally")), ("count", N("6")))),
            $"expected the committed transaction to ADD 1 to the ghost tally, got {Py.Repr(ghostAfter)}");
        var siblingAAfter = await Get(ddb, table, siblingAKey);
        Ensure(
            Py.Equal(siblingAAfter, Item(("id", S("sibling-tally-a")), ("count", N("11")))),
            $"expected the committed transaction to bump the sibling's count to 11, got {Py.Repr(siblingAAfter)}");

        // Scenario B: a plain comparison condition, with no attribute_exists at all, on an absent key,
        // inside a transaction.
        var lonelyKey = Item(("id", S("lonely-widget")));
        var siblingBKey = Item(("id", S("sibling-tally-b")));
        var siblingBSeed = Item(("id", S("sibling-tally-b")), ("stock", N("5")));
        await ddb.PutItemAsync(table, siblingBSeed);

        Task<TransactWriteItemsResponse> Restock() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem
                {
                    Update = new Update
                    {
                        TableName = table,
                        Key = lonelyKey,
                        UpdateExpression = "SET stock = :s",
                        ConditionExpression = "stock > :zero",
                        ExpressionAttributeValues = Item((":s", N("5")), (":zero", N("0"))),
                    },
                },
                new TransactWriteItem
                {
                    Update = new Update
                    {
                        TableName = table,
                        Key = siblingBKey,
                        UpdateExpression = "SET stock = stock + :inc",
                        ConditionExpression = "stock = :expected",
                        ExpressionAttributeValues = Item((":inc", N("1")), (":expected", N("5"))),
                    },
                },
            ],
        });

        var excB = await ExpectError(Restock, CanceledFirstFails.Code, CanceledFirstFails.Message);
        var reasonsB = (excB.CancellationReasons() ?? []).Select(r => ((string?)r.Code, (string?)r.Message)).ToList();
        Ensure(
            reasonsB.SequenceEqual([("ConditionalCheckFailed", "The conditional request failed"), ("None", null)]),
            $"expected [ConditionalCheckFailed, None] cancellation reasons, got {Py.Repr(reasonsB)}");

        var lonelyGot = await Get(ddb, table, lonelyKey);
        Ensure(lonelyGot is null, $"the cancelled comparison-guarded update must create nothing, got {Py.Repr(lonelyGot)}");
        var siblingBGot = await Get(ddb, table, siblingBKey);
        Ensure(
            Py.Equal(siblingBGot, siblingBSeed),
            $"the sibling Update's own condition held, but it must still be rolled back: got {Py.Repr(siblingBGot)}");

        // Positive control: once the key exists with a satisfying value, the identical transaction commits.
        await ddb.PutItemAsync(table, Item(("id", S("lonely-widget")), ("stock", N("3"))));
        await Restock();
        var lonelyAfter = await Get(ddb, table, lonelyKey);
        Ensure(
            Py.Equal(lonelyAfter, Item(("id", S("lonely-widget")), ("stock", N("5")))),
            $"expected the committed transaction to set stock to 5, got {Py.Repr(lonelyAfter)}");
        var siblingBAfter = await Get(ddb, table, siblingBKey);
        Ensure(
            Py.Equal(siblingBAfter, Item(("id", S("sibling-tally-b")), ("stock", N("6")))),
            $"expected the committed transaction to bump the sibling's stock to 6, got {Py.Repr(siblingBAfter)}");
    }

    /// <summary>Call fn() and grade the exact (code, message) it raises; a success is itself a failure.</summary>
    private static async Task<AwsError> ExpectError(Func<Task> fn, string code, string message)
    {
        var error = await FidelityContext.CaptureError(fn) ?? throw Fail($"expected {Py.Repr(code)} but the call succeeded");
        Ensure(error.Code == code, $"expected {Py.Repr(code)}, got {Py.Repr(error.Code)}: {Py.Repr(error.Message)}");
        Ensure(error.Message == message, $"expected message {Py.Repr(message)}, got {Py.Repr(error.Message)}");
        return error;
    }

    /// <summary>Poll DescribeTable until the table is ACTIVE; on expiry fail with the last status read.</summary>
    private static async Task WaitTableActive(IAmazonDynamoDB ddb, string table, double timeoutS = WaitS)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var status = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = table })).Table.TableStatus;
            if (status == TableStatus.ACTIVE)
            {
                return;
            }

            if (clock.Elapsed.TotalSeconds >= timeoutS)
            {
                throw Fail($"table {table} not ACTIVE within {timeoutS:0}s; last status {Py.Repr(status)}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }
    }

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
