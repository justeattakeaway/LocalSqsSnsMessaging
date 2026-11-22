// Translated from behavioral/dynamodb/check_transact_write_items_put_delete_update_function_conditions_cancel_in_order.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-items-put-delete-update-function-conditions-cancel-in-order</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteItemsPutDeleteUpdateFunctionConditionsCancelInOrder
{
    private const string Check = "transact-write-items-put-delete-update-function-conditions-cancel-in-order";
    private const string Canceled = "TransactionCanceledException";
    private const string CanceledMsg = "Transaction cancelled, please refer cancellation reasons for specific "
        + "reasons [ConditionalCheckFailed, ConditionalCheckFailed, ConditionalCheckFailed, None]";

    private static Dictionary<string, object?> Ccf() => new(StringComparer.Ordinal) { ["Code"] = "ConditionalCheckFailed", ["Message"] = "The conditional request failed" };

    private static Dictionary<string, object?> NoneReason() => new(StringComparer.Ordinal) { ["Code"] = "None" };

    /// <summary>
    /// A TransactWriteItems whose Put, Delete and Update each fail a different condition function
    /// (begins_with on binary, contains on a set, begins_with on a string) cancels with per-item
    /// reasons [ConditionalCheckFailed, ConditionalCheckFailed, ConditionalCheckFailed, None].
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_TransactWriteItems.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-items-put-delete-update-function-conditions-cancel-in-order")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-multi-fail-");
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

        var putKey = Item(("pk", S("put-target")));
        var putItem = With(putKey, ("blob", B("xyz-binary")));
        var putOverwrite = With(putKey, ("blob", B("new-binary")));

        var deleteKey = Item(("pk", S("delete-target")));
        var deleteItem = With(deleteKey, ("roles", SS("admin")));

        var updateKey = Item(("pk", S("update-target")));
        var updateItem = With(updateKey, ("code", S("xyz-code")));

        var ccKey = Item(("pk", S("cc-target")));
        var ccItem = With(ccKey, ("status", S("active")));

        foreach (var item in new[] { putItem, deleteItem, updateItem, ccItem })
        {
            await ddb.PutItemAsync(table, item);
        }

        Task<TransactWriteItemsResponse> Attempt(string putPrefix, string deleteValue, string updatePrefix) =>
            ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems =
                [
                    new TransactWriteItem
                    {
                        Put = new Put
                        {
                            TableName = table,
                            Item = putOverwrite,
                            ConditionExpression = "begins_with(#b, :p)",
                            ExpressionAttributeNames = Names(("#b", "blob")),
                            ExpressionAttributeValues = Item((":p", B(putPrefix))),
                        },
                    },
                    new TransactWriteItem
                    {
                        Delete = new Delete
                        {
                            TableName = table,
                            Key = deleteKey,
                            ConditionExpression = "contains(#r, :v)",
                            ExpressionAttributeNames = Names(("#r", "roles")),
                            ExpressionAttributeValues = Item((":v", S(deleteValue))),
                        },
                    },
                    new TransactWriteItem
                    {
                        Update = new Update
                        {
                            TableName = table,
                            Key = updateKey,
                            UpdateExpression = "SET #s = :s",
                            ConditionExpression = "begins_with(#c, :p)",
                            ExpressionAttributeNames = Names(("#c", "code"), ("#s", "status")),
                            ExpressionAttributeValues = Item((":p", S(updatePrefix)), (":s", S("touched"))),
                        },
                    },
                    new TransactWriteItem
                    {
                        ConditionCheck = new ConditionCheck
                        {
                            TableName = table,
                            Key = ccKey,
                            ConditionExpression = "attribute_exists(#s)",
                            ExpressionAttributeNames = Names(("#s", "status")),
                        },
                    },
                ],
            });

        var resp = await ExpectCanceled(() => Attempt("abc", "owner", "abc"));
        Ensure(resp.Code == Canceled, $"got {Py.Repr(resp.Code)}");
        Ensure(resp.Message == CanceledMsg, $"got message {Py.Repr(resp.Message)}");
        var reasons = resp.CancellationReasons()!.Select(r =>
        {
            var reason = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (r.Code is not null)
            {
                reason["Code"] = r.Code;
            }

            if (r.Message is not null)
            {
                reason["Message"] = r.Message;
            }

            return reason;
        }).ToList();
        Ensure(Py.Equal(reasons, new[] { Ccf(), Ccf(), Ccf(), NoneReason() }), $"got reasons {Py.Repr(reasons)}");

        foreach (var item in new[] { putItem, deleteItem, updateItem, ccItem })
        {
            var key = Item(("pk", item["pk"]));
            var got = await Get(ddb, table, key);
            Ensure(Py.Equal(got, item), $"the cancelled transaction must leave {Py.Repr(key)} unchanged, got {Py.Repr(got)}");
        }

        // --- control: the same shape with every condition holding commits all three writes ---
        await Attempt("xyz", "admin", "xyz");
        var gotPut = await Get(ddb, table, putKey);
        Ensure(Py.Equal(gotPut, putOverwrite), $"Put must commit, got {Py.Repr(gotPut)}");
        var gotDelete = await Get(ddb, table, deleteKey);
        Ensure(gotDelete is null, $"Delete must commit, got {Py.Repr(gotDelete)}");
        var gotUpdate = await Get(ddb, table, updateKey);
        Ensure(Py.Equal(gotUpdate, With(updateItem, ("status", S("touched")))), $"Update must commit, got {Py.Repr(gotUpdate)}");
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
