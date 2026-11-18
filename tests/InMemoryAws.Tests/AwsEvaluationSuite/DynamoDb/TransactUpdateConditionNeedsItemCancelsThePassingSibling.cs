// Translated from behavioral/dynamodb/check_transact_update_condition_needs_item_cancels_the_passing_sibling.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-update-condition-needs-item-cancels-the-passing-sibling</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactUpdateConditionNeedsItemCancelsThePassingSibling
{
    private const string CheckId = "transact-update-condition-needs-item-cancels-the-passing-sibling";
    private const double WaitSeconds = 60.0;
    private static readonly (string Code, string Message) Ccf = ("ConditionalCheckFailedException", "The conditional request failed");

    private static readonly (string Code, string Message) CanceledFirstFails = (
        "TransactionCanceledException",
        "Transaction cancelled, please refer cancellation reasons for specific reasons "
        + "[ConditionalCheckFailed, None]");

    /// <summary>
    /// A TransactWriteItems Update whose own condition needs an absent item cancels the whole
    /// transaction, rolling back a sibling Update whose own condition already held. The same rule
    /// rejects a standalone UpdateItem with a plain comparison condition and creates nothing.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-update-condition-needs-item-cancels-the-passing-sibling")]
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
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await WaitTableActive(ddb, table);

        Dictionary<string, AttributeValue> GhostKey() => Item(("id", S("ghost-order")));
        Dictionary<string, AttributeValue> SiblingKey() => Item(("id", S("sibling-counter")));
        var siblingSeed = Item(("id", S("sibling-counter")), ("count", N("10")));
        await ddb.PutItemAsync(table, Item(("id", S("sibling-counter")), ("count", N("10"))));

        Task<TransactWriteItemsResponse> Transfer() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem
                {
                    Update = new Update
                    {
                        TableName = table,
                        Key = GhostKey(),
                        UpdateExpression = "SET #st = :s",
                        ConditionExpression = "attribute_exists(id) AND version = :v",
                        ExpressionAttributeNames = Names(("#st", "status")),
                        ExpressionAttributeValues = Item((":s", S("filled")), (":v", N("1"))),
                    },
                },
                new TransactWriteItem
                {
                    Update = new Update
                    {
                        TableName = table,
                        Key = SiblingKey(),
                        UpdateExpression = "SET #c = #c + :inc",
                        ConditionExpression = "#c = :expected",
                        ExpressionAttributeNames = Names(("#c", "count")),
                        ExpressionAttributeValues = Item((":inc", N("1")), (":expected", N("10"))),
                    },
                },
            ],
        });

        var exc = await ExpectError(Transfer, CanceledFirstFails);
        var reasons = (exc.CancellationReasons() ?? []).Select(r => ((string?)r.Code, (string?)r.Message)).ToList();
        Ensure(
            reasons.SequenceEqual([("ConditionalCheckFailed", "The conditional request failed"), ("None", null)]),
            $"expected [ConditionalCheckFailed, None] cancellation reasons, got {Py.Repr(reasons)}");

        var ghostGot = await Get(ddb, table, GhostKey());
        Ensure(ghostGot is null, $"the cancelled transaction's own ghost key must stay absent, got {Py.Repr(ghostGot)}");
        var siblingGot = await Get(ddb, table, SiblingKey());
        Ensure(
            Py.Equal(siblingGot, siblingSeed),
            $"the sibling Update's own condition held, but it must still be rolled back: got {Py.Repr(siblingGot)}");

        // Positive control: create the ghost item for real, then the identical transaction commits both.
        await ddb.PutItemAsync(table, Item(("id", S("ghost-order")), ("version", N("1")), ("status", S("new"))));
        await Transfer();
        var ghostAfter = await Get(ddb, table, GhostKey());
        Ensure(
            Py.Equal(ghostAfter, Item(("id", S("ghost-order")), ("version", N("1")), ("status", S("filled")))),
            $"expected the committed transaction to flip status to filled, got {Py.Repr(ghostAfter)}");
        var siblingAfter = await Get(ddb, table, SiblingKey());
        Ensure(
            Py.Equal(siblingAfter, Item(("id", S("sibling-counter")), ("count", N("11")))),
            $"expected the committed transaction to bump the sibling's count to 11, got {Py.Repr(siblingAfter)}");

        // Standalone UpdateItem, a pure comparison condition (no attribute_exists), on an absent key.
        Dictionary<string, AttributeValue> LonelyKey() => Item(("id", S("lonely-widget")));

        Task<UpdateItemResponse> Restock() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = LonelyKey(),
            UpdateExpression = "SET stock = :s",
            ConditionExpression = "stock > :zero",
            ExpressionAttributeValues = Item((":s", N("5")), (":zero", N("0"))),
        });

        await ExpectError(Restock, Ccf);
        var lonelyGot = await Get(ddb, table, LonelyKey());
        Ensure(lonelyGot is null, $"the rejected standalone update must create nothing, got {Py.Repr(lonelyGot)}");

        // Positive control: once the item exists with a satisfying value, the identical call commits.
        await ddb.PutItemAsync(table, Item(("id", S("lonely-widget")), ("stock", N("3"))));
        await Restock();
        var lonelyAfter = await Get(ddb, table, LonelyKey());
        Ensure(
            Py.Equal(lonelyAfter, Item(("id", S("lonely-widget")), ("stock", N("5")))),
            $"expected the restored conditional update to land, got {Py.Repr(lonelyAfter)}");
    }

    /// <summary>Call fn() and grade the exact (code, message) it raises; a success is itself a failure.</summary>
    private static async Task<AwsError> ExpectError(Func<Task> fn, (string Code, string Message) expected)
    {
        var error = await FidelityContext.CaptureError(fn)
            ?? throw Fail($"expected {Py.Repr(expected.Code)} but the call succeeded");
        Ensure(error.Code == expected.Code, $"expected {Py.Repr(expected.Code)}, got {Py.Repr(error.Code)}: {Py.Repr(error.Message)}");
        Ensure(error.Message == expected.Message, $"expected message {Py.Repr(expected.Message)}, got {Py.Repr(error.Message)}");
        return error;
    }

    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };

    /// <summary>Poll DescribeTable until the table is ACTIVE; on expiry fail with the last status read.</summary>
    private static async Task WaitTableActive(IAmazonDynamoDB ddb, string table, double timeoutSeconds = WaitSeconds)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var status = (await ddb.DescribeTableAsync(table)).Table.TableStatus.Value;
            if (status == "ACTIVE")
            {
                return;
            }

            if (clock.Elapsed.TotalSeconds >= timeoutSeconds)
            {
                throw Fail($"table {table} not ACTIVE within {timeoutSeconds:F0}s; last status {Py.Repr(status)}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1.0));
        }
    }

    private static async Task<Dictionary<string, AttributeValue>?> Get(IAmazonDynamoDB ddb, string table, Dictionary<string, AttributeValue> key) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
}
