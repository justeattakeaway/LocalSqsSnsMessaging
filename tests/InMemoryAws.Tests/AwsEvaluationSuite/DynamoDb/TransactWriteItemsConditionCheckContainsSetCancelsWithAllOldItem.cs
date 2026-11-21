// Translated from behavioral/dynamodb/check_transact_write_items_condition_check_contains_set_cancels_with_all_old_item.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-items-condition-check-contains-set-cancels-with-all-old-item</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteItemsConditionCheckContainsSetCancelsWithAllOldItem
{
    private const string Check = "transact-write-items-condition-check-contains-set-cancels-with-all-old-item";
    private const string CcfCode = "ConditionalCheckFailed";
    private const string CcfMsg = "The conditional request failed";
    private const string Canceled = "TransactionCanceledException";
    private const string CanceledMsg = "Transaction cancelled, please refer cancellation reasons for specific reasons [ConditionalCheckFailed, None]";

    /// <summary>
    /// A TransactWriteItems ConditionCheck with contains(SS, :v) false, and
    /// ReturnValuesOnConditionCheckFailure=ALL_OLD, cancels carrying the complete conflicting item.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_TransactWriteItems.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-items-condition-check-contains-set-cancels-with-all-old-item")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-cc-contains-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await WaitActive(ddb, table);

        var holderKey = Item(("pk", S("holder")));
        var holder = With(holderKey, ("roles", SS("admin", "viewer")));
        await ddb.PutItemAsync(table, holder);

        var companion = Item(("pk", S("companion")));
        TransactWriteItemsRequest Request(string role) => new()
        {
            TransactItems =
            [
                new TransactWriteItem
                {
                    ConditionCheck = new ConditionCheck
                    {
                        TableName = table,
                        Key = holderKey,
                        ConditionExpression = "contains(#r, :v)",
                        ExpressionAttributeNames = Names(("#r", "roles")),
                        ExpressionAttributeValues = Item((":v", S(role))),
                        ReturnValuesOnConditionCheckFailure = ReturnValuesOnConditionCheckFailure.ALL_OLD,
                    },
                },
                new TransactWriteItem { Put = new Put { TableName = table, Item = companion } },
            ],
        };

        var resp = await ExpectCanceled(() => ddb.TransactWriteItemsAsync(Request("owner")));
        Ensure(resp.Code == Canceled, $"got {Py.Repr(resp.Code)}");
        Ensure(resp.Message == CanceledMsg, $"got message {Py.Repr(resp.Message)}");
        var reasons = resp.CancellationReasons()!;
        Ensure(reasons.Count == 2, $"expected 2 cancellation reasons, got {Py.Repr(reasons.Select(r => r.Code))}");
        var (failed, ok) = (reasons[0], reasons[1]);
        Ensure(failed.Code == CcfCode, $"got {Py.Repr(failed.Code)}");
        Ensure(failed.Message == CcfMsg, $"got {Py.Repr(failed.Message)}");
        Ensure(Py.Equal(failed.Item, holder), $"ALL_OLD must return the complete conflicting item, got {Py.Repr(failed.Item)}");
        Ensure(ok.Code == "None", $"the non-failing member must report Code 'None', got {Py.Repr(ok.Code)}");
        Ensure(ok.Item is null, $"the non-failing member must not carry an Item, got {Py.Repr(ok.Item)}");

        var got = await Get(ddb, table, companion);
        Ensure(got is null, $"the cancelled transaction must not have created {Py.Repr(companion)}, got {Py.Repr(got)}");
        var still = await Get(ddb, table, holderKey);
        Ensure(Py.Equal(still, holder), $"the holder item must be unchanged by the cancelled transaction, got {Py.Repr(still)}");

        // --- control: the same shape with a holding contains() condition commits both members ---
        await ddb.TransactWriteItemsAsync(Request("admin"));
        got = await Get(ddb, table, companion);
        Ensure(Py.Equal(got, companion), $"a holding contains() ConditionCheck must let the companion Put commit, got {Py.Repr(got)}");
    }

    /// <summary>Poll DescribeTable every 2 s until the table is ACTIVE.</summary>
    private static async Task WaitActive(IAmazonDynamoDB ddb, string table, double timeoutS = 120.0)
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

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>Call fn() and return its error; an accepted call fails the check.</summary>
    private static async Task<AwsError> ExpectCanceled(Func<Task> fn) =>
        await FidelityContext.CaptureError(fn) ?? throw Fail("expected the transaction to be cancelled, but it succeeded");

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

    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };
}
