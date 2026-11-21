// Translated from behavioral/dynamodb/check_transact_write_items_condition_evaluated_before_update_runtime_validation.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-items-condition-evaluated-before-update-runtime-validation</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteItemsConditionEvaluatedBeforeUpdateRuntimeValidation
{
    private const string Check = "transact-write-items-condition-evaluated-before-update-runtime-validation";
    private const double TableActiveBudgetS = 120.0;
    private const double TeardownWaitS = 120.0;
    private const string ErrCanceled = "TransactionCanceledException";
    private const string CanceledPrefix = "Transaction cancelled, please refer cancellation reasons for specific reasons ";
    private const string CondFailedMsg = "The conditional request failed";
    private const string TypeMismatchMsg = "An operand in the update expression has an incorrect data type";

    /// <summary>
    /// A TransactWriteItems Update's ConditionExpression is evaluated before its UpdateExpression:
    /// a false condition reports only ConditionalCheckFailed and never evaluates the arithmetic; a
    /// true condition lets the runtime arithmetic error surface as its own ValidationError reason.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_TransactWriteItems.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-items-condition-evaluated-before-update-runtime-validation")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx, ddb);

        var key = Item(("pk", S("counter")));
        // ``n`` is stored as a String; "+" needs the stored value's type read at runtime to detect
        // the mismatch, unlike a request-literal type mismatch, which DynamoDB rejects up front.
        var original = With(key, ("n", S("not-a-number")), ("status", S("ON")));
        await ddb.PutItemAsync(table, original);

        // (a) False condition + invalid arithmetic, alone: only ConditionalCheckFailed is reported,
        // and ReturnValuesOnConditionCheckFailure returns the complete conflicting item.
        var err = await Canceled(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems = [BadUpdate(table, key, conditionExpected: "OFF", returnOld: true)],
        }));
        Ensure(err.Code == ErrCanceled, $"expected {ErrCanceled}, got {err}");
        var expectedMessage = CanceledPrefix + "[ConditionalCheckFailed]";
        Ensure(err.Message == expectedMessage, $"expected message {Py.Repr(expectedMessage)}, got {Py.Repr(err.Message)}");
        var reasons = err.CancellationReasons();
        Ensure(reasons is not null && reasons.Count == 1, $"expected 1 cancellation reason, got {Py.Repr(reasons?.Select(Pair))}");
        Ensure(
            reasons![0].Code == "ConditionalCheckFailed",
            $"a false condition must report ConditionalCheckFailed, not the arithmetic error, got {Py.Repr(Pair(reasons[0]))}");
        Ensure(reasons[0].Message == CondFailedMsg, $"expected {Py.Repr(CondFailedMsg)}, got {Py.Repr(reasons[0].Message)}");
        Ensure(
            Py.Equal(reasons[0].Item, original),
            $"ReturnValuesOnConditionCheckFailure=ALL_OLD must return the complete item {Py.Repr(original)}, "
            + $"got {Py.Repr(reasons[0].Item)}");
        var unchanged = await Get(ddb, table, key);
        Ensure(Py.Equal(unchanged, original), $"the canceled Update must leave the item unchanged, got {Py.Repr(unchanged)}");

        // (b) True condition + invalid arithmetic: the condition passes, so the runtime arithmetic
        // error is reached and reported as the member's own ValidationError reason.
        err = await Canceled(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems = [BadUpdate(table, key, conditionExpected: "ON", returnOld: false)],
        }));
        Ensure(err.Code == ErrCanceled, $"expected {ErrCanceled}, got {err}");
        expectedMessage = CanceledPrefix + "[ValidationError]";
        Ensure(err.Message == expectedMessage, $"expected message {Py.Repr(expectedMessage)}, got {Py.Repr(err.Message)}");
        reasons = err.CancellationReasons();
        Ensure(reasons is not null && reasons.Count == 1, $"expected 1 cancellation reason, got {Py.Repr(reasons?.Select(Pair))}");
        Ensure(
            reasons![0].Code == "ValidationError",
            $"a true condition must let the arithmetic error surface as ValidationError, got {Py.Repr(Pair(reasons[0]))}");
        Ensure(reasons[0].Message == TypeMismatchMsg, $"expected {Py.Repr(TypeMismatchMsg)}, got {Py.Repr(reasons[0].Message)}");
        unchanged = await Get(ddb, table, key);
        Ensure(Py.Equal(unchanged, original), $"the canceled Update must still leave the item unchanged, got {Py.Repr(unchanged)}");

        // (c) The same false-condition member, alongside an unrelated sibling Put: the condition
        // failure still suppresses the arithmetic error, and the sibling reports "None".
        var siblingKey = Item(("pk", S("sibling")));
        err = await Canceled(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                BadUpdate(table, key, conditionExpected: "OFF", returnOld: false),
                new TransactWriteItem { Put = new Put { TableName = table, Item = siblingKey } },
            ],
        }));
        Ensure(err.Code == ErrCanceled, $"expected {ErrCanceled}, got {err}");
        expectedMessage = CanceledPrefix + "[ConditionalCheckFailed, None]";
        Ensure(err.Message == expectedMessage, $"expected message {Py.Repr(expectedMessage)}, got {Py.Repr(err.Message)}");
        reasons = err.CancellationReasons();
        Ensure(reasons is not null && reasons.Count == 2, $"expected 2 cancellation reasons, got {Py.Repr(reasons?.Select(Pair))}");
        var (updateReason, putReason) = (reasons![0], reasons[1]);
        Ensure(
            updateReason.Code == "ConditionalCheckFailed",
            $"the false condition must still suppress the arithmetic error with a sibling present, got {Py.Repr(Pair(updateReason))}");
        Ensure(putReason.Code == "None", $"the sibling Put must report Code 'None', got {Py.Repr(Pair(putReason))}");
        unchanged = await Get(ddb, table, key);
        Ensure(Py.Equal(unchanged, original), $"the canceled Update must still leave the item unchanged, got {Py.Repr(unchanged)}");
        Ensure(await Get(ddb, table, siblingKey) is null, "the canceled transaction must not have created the sibling item");

        // Positive control: a true condition with valid arithmetic commits. A separate numeric-typed
        // item stands in for the Update member, since ``counter`` never holds a usable numeric value.
        var numericKey = Item(("pk", S("numeric")));
        await ddb.PutItemAsync(table, With(numericKey, ("n", N("5")), ("status", S("ON"))));
        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem
                {
                    Update = new Update
                    {
                        TableName = table,
                        Key = numericKey,
                        UpdateExpression = "SET n = n + :one",
                        ConditionExpression = "#s = :expected",
                        ExpressionAttributeNames = Names(("#s", "status")),
                        ExpressionAttributeValues = Item((":one", N("1")), (":expected", S("ON"))),
                    },
                },
            ],
        });
        var committed = await Get(ddb, table, numericKey);
        Ensure(
            Py.Equal(committed, With(numericKey, ("n", N("6")), ("status", S("ON")))),
            $"a satisfiable condition with valid arithmetic must commit, got {Py.Repr(committed)}");
    }

    private static TransactWriteItem BadUpdate(string table, Dictionary<string, AttributeValue> key, string conditionExpected, bool returnOld)
    {
        var member = new Update
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET n = n + :one",
            ConditionExpression = "#s = :expected",
            ExpressionAttributeNames = Names(("#s", "status")),
            ExpressionAttributeValues = Item((":one", N("1")), (":expected", S(conditionExpected))),
        };
        if (returnOld)
        {
            member.ReturnValuesOnConditionCheckFailure = ReturnValuesOnConditionCheckFailure.ALL_OLD;
        }

        return new TransactWriteItem { Update = member };
    }

    private static (string Code, string Message) Pair(CancellationReason r) => (r.Code, r.Message);

    /// <summary>The error <paramref name="fn"/> raises; fails if it unexpectedly succeeds.</summary>
    private static async Task<AwsError> Canceled(Func<Task> fn) =>
        await FidelityContext.CaptureError(fn) ?? throw Fail("expected the transaction to be canceled, but it succeeded");

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
