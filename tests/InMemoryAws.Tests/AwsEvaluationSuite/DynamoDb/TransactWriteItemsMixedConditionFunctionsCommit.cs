// Translated from behavioral/dynamodb/check_transact_write_items_mixed_condition_functions_commit.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-items-mixed-condition-functions-commit</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteItemsMixedConditionFunctionsCommit
{
    private const string Check = "transact-write-items-mixed-condition-functions-commit";

    /// <summary>
    /// A TransactWriteItems with a ConditionCheck (contains), a Put (attribute_type), a Delete
    /// (begins_with), and an Update whose condition ANDs contains, attribute_type and size, commits
    /// every member once all four hold, and cancels when the Update's size clause alone fails.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.ConditionExpressions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-items-mixed-condition-functions-commit")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-mixed-fn-");
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

        var ccKey = Item(("pk", S("cc-target")));
        var ccItem = With(ccKey, ("roles", SS("admin")));

        var putKey = Item(("pk", S("put-target")));
        var putItem = With(putKey, ("kind", S("legacy")));
        var putOverwrite = With(putKey, ("kind", S("legacy-v2")));

        var deleteKey = Item(("pk", S("delete-target")));
        var deleteItem = With(deleteKey, ("code", S("abc-123")));

        var updateKey = Item(("pk", S("update-target")));
        var updateItem = With(
            updateKey,
            ("flags", SS("x", "y")),
            ("kind", S("doc")),
            ("notes", L(S("a"), S("b"), S("c"))));

        foreach (var item in new[] { ccItem, putItem, deleteItem, updateItem })
        {
            await ddb.PutItemAsync(table, item);
        }

        Task<TransactWriteItemsResponse> Attempt(int sizeFloor) => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem
                {
                    ConditionCheck = new ConditionCheck
                    {
                        TableName = table,
                        Key = ccKey,
                        ConditionExpression = "contains(#r, :v)",
                        ExpressionAttributeNames = Names(("#r", "roles")),
                        ExpressionAttributeValues = Item((":v", S("admin"))),
                    },
                },
                new TransactWriteItem
                {
                    Put = new Put
                    {
                        TableName = table,
                        Item = putOverwrite,
                        ConditionExpression = "attribute_type(#k, :t)",
                        ExpressionAttributeNames = Names(("#k", "kind")),
                        ExpressionAttributeValues = Item((":t", S("S"))),
                    },
                },
                new TransactWriteItem
                {
                    Delete = new Delete
                    {
                        TableName = table,
                        Key = deleteKey,
                        ConditionExpression = "begins_with(#c, :p)",
                        ExpressionAttributeNames = Names(("#c", "code")),
                        ExpressionAttributeValues = Item((":p", S("abc"))),
                    },
                },
                new TransactWriteItem
                {
                    Update = new Update
                    {
                        TableName = table,
                        Key = updateKey,
                        UpdateExpression = "SET #s = :s",
                        ConditionExpression = "contains(#f, :v) AND attribute_type(#k, :t) AND size(#n) > :cnt",
                        ExpressionAttributeNames = Names(("#f", "flags"), ("#k", "kind"), ("#n", "notes"), ("#s", "status")),
                        ExpressionAttributeValues = Item(
                            (":v", S("x")), (":t", S("S")), (":cnt", N(sizeFloor.ToString(System.Globalization.CultureInfo.InvariantCulture))), (":s", S("touched"))),
                    },
                },
            ],
        });

        // --- control: the size clause alone fails (notes has 3 elements, floor is 5) and cancels ---
        var resp = await ExpectCanceled(() => Attempt(5));
        Ensure(resp.Code == "TransactionCanceledException", $"got {Py.Repr(resp.Code)}");
        var reasons = resp.CancellationReasons()!.Select(r => r.Code).ToList();
        Ensure(
            reasons.SequenceEqual(["None", "None", "None", "ConditionalCheckFailed"]),
            $"got reasons {Py.Repr(reasons)}");
        foreach (var item in new[] { ccItem, putItem, deleteItem, updateItem })
        {
            var key = Item(("pk", item["pk"]));
            var got = await Get(ddb, table, key);
            Ensure(Py.Equal(got, item), $"the cancelled transaction must leave {Py.Repr(key)} unchanged, got {Py.Repr(got)}");
        }

        // --- all four conditions hold (floor 2 < size 3): the transaction commits ---
        await Attempt(2);

        var gotPut = await Get(ddb, table, putKey);
        Ensure(Py.Equal(gotPut, putOverwrite), $"Put under attribute_type must commit, got {Py.Repr(gotPut)}");
        var gotDelete = await Get(ddb, table, deleteKey);
        Ensure(gotDelete is null, $"Delete under begins_with must commit, got {Py.Repr(gotDelete)}");
        var gotUpdate = await Get(ddb, table, updateKey);
        Ensure(
            Py.Equal(gotUpdate, With(updateItem, ("status", S("touched")))),
            $"Update under contains AND attribute_type AND size must commit, got {Py.Repr(gotUpdate)}");
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
