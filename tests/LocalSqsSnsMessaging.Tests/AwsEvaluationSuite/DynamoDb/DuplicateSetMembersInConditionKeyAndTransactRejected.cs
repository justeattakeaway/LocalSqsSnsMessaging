// Translated from behavioral/dynamodb/check_duplicate_set_members_in_condition_key_and_transact_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.duplicate-set-members-in-condition-key-and-transact-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DuplicateSetMembersInConditionKeyAndTransactRejected
{
    private const string CheckId = "duplicate-set-members-in-condition-key-and-transact-rejected";
    private const string ErrValidation = "ValidationException";

    /// <summary>
    /// A duplicate SS/NS/BS member outside an Item -- a ConditionExpression value, a Key, or a
    /// slot inside Transact*Items -- is rejected ValidationException before evaluation, taking a
    /// valid sibling slot in the same batch or transaction down with it.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.duplicate-set-members-in-condition-key-and-transact-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-dupset-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 1, maxAttempts: 60);

        await ddb.PutItemAsync(t, Item(("pk", S("item1")), ("other", S("x"))));
        const string invalid = "One or more parameter values were invalid: ";
        const string counted = "1 validation error detected: " + invalid;

        // --- a duplicate in a ConditionExpression value, on PutItem and UpdateItem ---
        var (code, message) = await ErrorCode(
            () => ddb.PutItemAsync(new PutItemRequest
            {
                TableName = t,
                Item = Item(("pk", S("item1")), ("other", S("y"))),
                ConditionExpression = "other = :v",
                ExpressionAttributeValues = Item((":v", SS("a", "a"))),
            }),
            "PutItem ConditionExpression value with a duplicate-bearing SS");
        Ensure(code == ErrValidation, $"PutItem with a duplicate SS condition value: got {Py.Repr(code)}");
        Ensure(message == counted + "Input collection [\"a\", \"a\"] contains duplicates.", $"PutItem with a duplicate SS condition value: got message {Py.Repr(message)}");

        (code, message) = await ErrorCode(
            () => ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = t,
                Key = Item(("pk", S("item1"))),
                UpdateExpression = "SET other = :newval",
                ConditionExpression = "contains(:v, other)",
                ExpressionAttributeValues = Item((":v", NS("1", "1")), (":newval", S("z"))),
            }),
            "UpdateItem ConditionExpression value with a duplicate-bearing NS");
        Ensure(code == ErrValidation, $"UpdateItem with a duplicate NS condition value: got {Py.Repr(code)}");
        Ensure(message == counted + "Input collection contains duplicates.", $"UpdateItem with a duplicate NS condition value: got message {Py.Repr(message)}");

        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("item1"))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(got, Item(("pk", S("item1")), ("other", S("x")))), $"both rejected conditional writes must leave the item untouched, got {Py.Repr(got)}");

        // --- a duplicate in the Key of GetItem, DeleteItem and BatchGetItem ---
        (code, message) = await ErrorCode(
            () => ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", SS("a", "a"))) }),
            "GetItem Key with a duplicate-bearing SS");
        Ensure(code == ErrValidation, $"GetItem with a duplicate SS key: got {Py.Repr(code)}");
        Ensure(message == invalid + "Input collection [\"a\", \"a\"] contains duplicates.", $"GetItem with a duplicate SS key: got message {Py.Repr(message)}");

        (code, message) = await ErrorCode(
            () => ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", BS([0x01], [0x01]))) }),
            "GetItem Key with a duplicate-bearing BS");
        Ensure(code == ErrValidation, $"GetItem with a duplicate BS key: got {Py.Repr(code)}");
        Ensure(message == invalid + "Input collection [\"AQ==\",\"AQ==\"]of type BS contains duplicates.", $"GetItem with a duplicate BS key: got message {Py.Repr(message)}");

        (code, message) = await ErrorCode(
            () => ddb.DeleteItemAsync(new DeleteItemRequest { TableName = t, Key = Item(("pk", SS("b", "b"))) }),
            "DeleteItem Key with a duplicate-bearing SS");
        Ensure(code == ErrValidation, $"DeleteItem with a duplicate SS key: got {Py.Repr(code)}");
        Ensure(message == counted + "Input collection [\"b\", \"b\"] contains duplicates.", $"DeleteItem with a duplicate SS key: got message {Py.Repr(message)}");

        (code, message) = await ErrorCode(
            () => ddb.BatchGetItemAsync(new BatchGetItemRequest
            {
                RequestItems = new() { [t] = new KeysAndAttributes { Keys = [Item(("pk", S("item1"))), Item(("pk", NS("2", "2")))] } },
            }),
            "BatchGetItem Keys with one duplicate-bearing NS key beside a valid one");
        Ensure(code == ErrValidation, $"BatchGetItem with a duplicate NS key: got {Py.Repr(code)}");
        Ensure(message == invalid + "Input collection contains duplicates.", $"BatchGetItem with a duplicate NS key: got message {Py.Repr(message)}");

        // --- a duplicate inside one slot of TransactWriteItems/TransactGetItems rejects the whole
        // call, before any slot -- including a valid sibling -- is evaluated or applied ---
        (code, message) = await ErrorCode(
            () => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems =
                [
                    new TransactWriteItem { Put = new Put { TableName = t, Item = Item(("pk", S("legit-put")), ("x", S("1"))) } },
                    new TransactWriteItem { Put = new Put { TableName = t, Item = Item(("pk", S("bad-put")), ("s", SS("d", "d"))) } },
                ],
            }),
            "TransactWriteItems with a duplicate-bearing SS in one Put.Item beside a valid Put");
        Ensure(code == ErrValidation, $"TransactWriteItems with a duplicate SS item: got {Py.Repr(code)}");
        Ensure(message == invalid + "Input collection [\"d\", \"d\"] contains duplicates.", $"TransactWriteItems with a duplicate SS item: got message {Py.Repr(message)}");

        (code, message) = await ErrorCode(
            () => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems =
                [
                    new TransactWriteItem { Put = new Put { TableName = t, Item = Item(("pk", S("legit-check")), ("x", S("1"))) } },
                    new TransactWriteItem
                    {
                        ConditionCheck = new ConditionCheck
                        {
                            TableName = t,
                            Key = Item(("pk", S("item1"))),
                            ConditionExpression = "contains(:v, other)",
                            ExpressionAttributeValues = Item((":v", BS([0x02], [0x02]))),
                        },
                    },
                ],
            }),
            "TransactWriteItems with a duplicate-bearing BS condition value beside a valid Put");
        Ensure(code == ErrValidation, $"TransactWriteItems with a duplicate BS condition value: got {Py.Repr(code)}");
        Ensure(message == invalid + "Input collection [\"Ag==\",\"Ag==\"]of type BS contains duplicates.", $"TransactWriteItems with a duplicate BS condition value: got message {Py.Repr(message)}");

        (code, message) = await ErrorCode(
            () => ddb.TransactGetItemsAsync(new TransactGetItemsRequest
            {
                TransactItems =
                [
                    new TransactGetItem { Get = new Get { TableName = t, Key = Item(("pk", S("item1"))) } },
                    new TransactGetItem { Get = new Get { TableName = t, Key = Item(("pk", SS("f", "f"))) } },
                ],
            }),
            "TransactGetItems with a duplicate-bearing SS Get.Key beside a valid Get");
        Ensure(code == ErrValidation, $"TransactGetItems with a duplicate SS key: got {Py.Repr(code)}");
        Ensure(message == invalid + "Input collection [\"f\", \"f\"] contains duplicates.", $"TransactGetItems with a duplicate SS key: got message {Py.Repr(message)}");

        // Neither the valid sibling Put nor any of the probed items landed: rejection is request-wide.
        var scanned = await ddb.ScanAsync(new ScanRequest { TableName = t, ConsistentRead = true });
        Ensure(
            Py.Equal(scanned.Items, new[] { Item(("pk", S("item1")), ("other", S("x"))) }),
            $"no write from a rejected ConditionExpression, Key or Transact*Items call may land, including a valid sibling slot, got {Py.Repr(scanned.Items)}");
    }

    /// <summary>The AWS error code and message `fn` raises; an unexpected success fails the check.</summary>
    private static async Task<(string? Code, string? Message)> ErrorCode<T>(Func<Task<T>> fn, string what)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"{what}: the call was ACCEPTED with {Py.Repr(result)}; real AWS rejects it");
        }

        return (error.Code, error.Message);
    }
}
