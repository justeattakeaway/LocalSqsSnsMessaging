// Translated from behavioral/dynamodb/check_transact_write_items_condition_check_list_index_and_nested_path_cancels.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-items-condition-check-list-index-and-nested-path-cancels</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteItemsConditionCheckListIndexAndNestedPathCancels
{
    private const string Check = "transact-write-items-condition-check-list-index-and-nested-path-cancels";
    private static readonly (string?, string?) Ccf = ("ConditionalCheckFailed", "The conditional request failed");
    private const string Canceled = "TransactionCanceledException";
    private const string CanceledMsg = "Transaction cancelled, please refer cancellation reasons for specific reasons [ConditionalCheckFailed, None]";

    /// <summary>
    /// A TransactWriteItems ConditionCheck on an out-of-range list index, or on a map path whose
    /// parent key is absent, evaluates false and cancels with reasons [ConditionalCheckFailed, None].
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.ConditionExpressions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-items-condition-check-list-index-and-nested-path-cancels")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-cc-path-");
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

        var ownerKey = Item(("pk", S("owner")));
        var owner = With(ownerKey, ("tags", L(S("red"), S("blue"))), ("profile", M(("name", S("Jane Doe")))));
        await ddb.PutItemAsync(table, owner);

        // --- an out-of-range list index: tags has 2 elements, index 5 does not exist ---
        var companionA = Item(("pk", S("companion-list-index")));
        var resp = await ExpectCanceled(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem
                {
                    ConditionCheck = new ConditionCheck
                    {
                        TableName = table,
                        Key = ownerKey,
                        ConditionExpression = "#t[5] = :v",
                        ExpressionAttributeNames = Names(("#t", "tags")),
                        ExpressionAttributeValues = Item((":v", S("red"))),
                    },
                },
                new TransactWriteItem { Put = new Put { TableName = table, Item = companionA } },
            ],
        }));
        Ensure(resp.Code == Canceled, $"got {Py.Repr(resp.Code)}");
        Ensure(resp.Message == CanceledMsg, $"got message {Py.Repr(resp.Message)}");
        var reasons = resp.CancellationReasons()!.Select(r => ((string?)r.Code, (string?)r.Message)).ToList();
        Ensure(reasons.SequenceEqual([Ccf, ("None", null)]), $"out-of-range list index: got reasons {Py.Repr(reasons)}");
        var got = await Get(ddb, table, companionA);
        Ensure(got is null, $"the cancelled transaction must not have created {Py.Repr(companionA)}, got {Py.Repr(got)}");

        // --- a missing nested map path: profile has no 'contact' key ---
        var companionB = Item(("pk", S("companion-nested-path")));
        resp = await ExpectCanceled(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem
                {
                    ConditionCheck = new ConditionCheck
                    {
                        TableName = table,
                        Key = ownerKey,
                        ConditionExpression = "attribute_exists(#p.#c)",
                        ExpressionAttributeNames = Names(("#p", "profile"), ("#c", "contact")),
                    },
                },
                new TransactWriteItem { Put = new Put { TableName = table, Item = companionB } },
            ],
        }));
        Ensure(resp.Code == Canceled, $"got {Py.Repr(resp.Code)}");
        Ensure(resp.Message == CanceledMsg, $"got message {Py.Repr(resp.Message)}");
        reasons = resp.CancellationReasons()!.Select(r => ((string?)r.Code, (string?)r.Message)).ToList();
        Ensure(reasons.SequenceEqual([Ccf, ("None", null)]), $"missing nested map path: got reasons {Py.Repr(reasons)}");
        got = await Get(ddb, table, companionB);
        Ensure(got is null, $"the cancelled transaction must not have created {Py.Repr(companionB)}, got {Py.Repr(got)}");

        // --- control: the same shapes with a holding condition commit both members ---
        var companionC = Item(("pk", S("companion-commits")));
        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem
                {
                    ConditionCheck = new ConditionCheck
                    {
                        TableName = table,
                        Key = ownerKey,
                        ConditionExpression = "#t[0] = :v AND attribute_exists(#p.#n)",
                        ExpressionAttributeNames = Names(("#t", "tags"), ("#p", "profile"), ("#n", "name")),
                        ExpressionAttributeValues = Item((":v", S("red"))),
                    },
                },
                new TransactWriteItem { Put = new Put { TableName = table, Item = companionC } },
            ],
        });
        got = await Get(ddb, table, companionC);
        Ensure(Py.Equal(got, companionC), $"a holding ConditionCheck must let the companion Put commit, got {Py.Repr(got)}");
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
