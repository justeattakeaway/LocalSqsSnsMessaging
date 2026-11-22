// Translated from behavioral/dynamodb/check_transact_write_items_update_child_of_list_scalar_cancels_with_validation_error.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-items-update-child-of-list-scalar-cancels-with-validation-error</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteItemsUpdateChildOfListScalarCancelsWithValidationError
{
    private const string Check = "transact-write-items-update-child-of-list-scalar-cancels-with-validation-error";
    private const double TableActiveBudgetS = 120.0;
    private const double TeardownWaitS = 120.0;
    private const string ErrCanceled = "TransactionCanceledException";
    private const string CanceledPrefix = "Transaction cancelled, please refer cancellation reasons for specific reasons ";
    private const string PathInvalidMsg = "The document path provided in the update expression is invalid for update";

    /// <summary>
    /// A TransactWriteItems Update setting a child path of a list element that holds a scalar
    /// cancels that member with ValidationError, alongside another member's own failure reason.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_TransactWriteItems.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-items-update-child-of-list-scalar-cancels-with-validation-error")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx, ddb);

        var lockedKey = Item(("pk", S("locked")));
        await ddb.PutItemAsync(table, With(lockedKey, ("status", S("LOCKED"))));

        var docKey = Item(("pk", S("doc")));
        var docItem = With(docKey, ("items", L(M(("child", N("1"))), S("not-a-map"))));
        await ddb.PutItemAsync(table, docItem);

        var ccFails = new TransactWriteItem
        {
            ConditionCheck = new ConditionCheck
            {
                TableName = table,
                Key = lockedKey,
                ConditionExpression = "#s = :expected",
                ExpressionAttributeNames = Names(("#s", "status")),
                ExpressionAttributeValues = Item((":expected", S("UNLOCKED"))),
            },
        };
        // items[1] is a String, not a Map; setting a child path through it is invalid.
        var updateScalarChild = new TransactWriteItem
        {
            Update = new Update
            {
                TableName = table,
                Key = docKey,
                UpdateExpression = "SET #i[1].#c = :v",
                ExpressionAttributeNames = Names(("#i", "items"), ("#c", "child")),
                ExpressionAttributeValues = Item((":v", S("x"))),
            },
        };

        var err = await FidelityContext.CaptureError(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = [ccFails, updateScalarChild] }))
            ?? throw Fail("expected the transaction to be canceled, but it succeeded");
        Ensure(err.Code == ErrCanceled, $"expected {ErrCanceled}, got {err}");
        var expectedMessage = CanceledPrefix + "[ConditionalCheckFailed, ValidationError]";
        Ensure(err.Message == expectedMessage, $"expected message {Py.Repr(expectedMessage)}, got {Py.Repr(err.Message)}");
        var reasons = err.CancellationReasons();
        Ensure(reasons is not null && reasons.Count == 2, $"expected 2 cancellation reasons, got {Py.Repr(reasons?.Select(r => (r.Code, r.Message)))}");
        var (ccReason, updateReason) = (reasons![0], reasons[1]);
        Ensure(ccReason.Code == "ConditionalCheckFailed", $"member 0 should be ConditionalCheckFailed, got {Py.Repr((ccReason.Code, ccReason.Message))}");
        Ensure(updateReason.Code == "ValidationError", $"member 1 should be ValidationError, got {Py.Repr((updateReason.Code, updateReason.Message))}");
        Ensure(
            updateReason.Message == PathInvalidMsg,
            $"a child path through a scalar list element must carry {Py.Repr(PathInvalidMsg)}, "
            + $"got {Py.Repr(updateReason.Message)}");

        var unchanged = await Get(ddb, table, docKey);
        Ensure(Py.Equal(unchanged, docItem), $"the canceled Update must leave the document unchanged, got {Py.Repr(unchanged)}");

        // Positive control: the same shape, targeting items[0], which IS a Map, commits.
        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem
                {
                    ConditionCheck = new ConditionCheck
                    {
                        TableName = table,
                        Key = lockedKey,
                        ConditionExpression = "#s = :expected",
                        ExpressionAttributeNames = Names(("#s", "status")),
                        ExpressionAttributeValues = Item((":expected", S("LOCKED"))),
                    },
                },
                new TransactWriteItem
                {
                    Update = new Update
                    {
                        TableName = table,
                        Key = docKey,
                        UpdateExpression = "SET #i[0].#c = :v",
                        ExpressionAttributeNames = Names(("#i", "items"), ("#c", "child")),
                        ExpressionAttributeValues = Item((":v", N("2"))),
                    },
                },
            ],
        });
        var committed = await Get(ddb, table, docKey);
        var expectedCommitted = With(docKey, ("items", L(M(("child", N("2"))), S("not-a-map"))));
        Ensure(Py.Equal(committed, expectedCommitted), $"a child path through a Map list element must commit, got {Py.Repr(committed)}");
    }

    private static async Task DeleteTableWhenPossible(IAmazonDynamoDB ddb, string tableName)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(tableName));
            if (error is null || error.Code == "ResourceNotFoundException")
            {
                return;
            }

            if (error.Code == "ResourceInUseException" && clock.Elapsed.TotalSeconds < TeardownWaitS)
            {
                await FidelityTime.Delay(TimeSpan.FromSeconds(5));
                continue;
            }

            throw error.Exception;
        }
    }

    private static async Task<string> CreateTable(FidelityContext ctx, IAmazonDynamoDB ddb)
    {
        var name = FidelityContext.UniqueName("sim2real-behav-ddb-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => DeleteTableWhenPossible(ddb, name));
        var clock = FidelityTime.StartNew();
        TableStatus? status = null;
        while (clock.Elapsed.TotalSeconds < TableActiveBudgetS)
        {
            status = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = name })).Table.TableStatus;
            if (status == TableStatus.ACTIVE)
            {
                return name;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {Py.Repr(name)} not ACTIVE within {TableActiveBudgetS:0.0}s: {Py.Repr(status)}");
    }

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
}
