// Translated from behavioral/dynamodb/check_execute_transaction_duplicate_insert_rolls_back_sibling_insert.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.execute-transaction-duplicate-insert-rolls-back-sibling-insert</summary>
[Category("AwsEvaluationSuite")]
public sealed class ExecuteTransactionDuplicateInsertRollsBackSiblingInsert
{
    private const string CheckId = "execute-transaction-duplicate-insert-rolls-back-sibling-insert";
    private const string ErrTransactionCanceled = "TransactionCanceledException";

    /// <summary>
    /// ExecuteTransaction cancels on a DuplicateItem INSERT and rolls back a sibling INSERT in
    /// the same transaction that would have succeeded on its own, leaving both keys as they were
    /// before the call.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.execute-transaction-duplicate-insert-rolls-back-sibling-insert")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = await PartiqlTable(ctx);

        // positive control: two fresh-key INSERTs in one ExecuteTransaction both commit
        await ddb.ExecuteTransactionAsync(new ExecuteTransactionRequest
        {
            TransactStatements =
            [
                new ParameterizedStatement { Statement = $"INSERT INTO \"{t}\" VALUE {{'pk':'control-a','sk':'s1'}}" },
                new ParameterizedStatement { Statement = $"INSERT INTO \"{t}\" VALUE {{'pk':'control-b','sk':'s1'}}" },
            ],
        });
        var controlA = await Get(ddb, t, "control-a");
        var controlB = await Get(ddb, t, "control-b");
        Ensure(
            Py.Equal(controlA, Item(("pk", S("control-a")), ("sk", S("s1")))),
            $"positive control: the first ExecuteTransaction INSERT should have committed, got {Py.Repr(controlA)}");
        Ensure(
            Py.Equal(controlB, Item(("pk", S("control-b")), ("sk", S("s1")))),
            $"positive control: the second ExecuteTransaction INSERT should have committed, got {Py.Repr(controlB)}");

        // establish an existing item that the first statement will collide with
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"INSERT INTO \"{t}\" VALUE {{'pk':'existing','sk':'s1','tag':'original'}}" });

        var duplicateStatement = $"INSERT INTO \"{t}\" VALUE {{'pk':'existing','sk':'s1','tag':'should-not-persist'}}";
        var siblingStatement = $"INSERT INTO \"{t}\" VALUE {{'pk':'sibling','sk':'s1'}}";
        var err = await FidelityContext.CaptureError(() => ddb.ExecuteTransactionAsync(new ExecuteTransactionRequest
        {
            TransactStatements = [new ParameterizedStatement { Statement = duplicateStatement }, new ParameterizedStatement { Statement = siblingStatement }],
        }));
        Ensure(err is not null, "expected TransactionCanceledException, got success");
        Ensure(err.Code == ErrTransactionCanceled, $"expected {ErrTransactionCanceled}, got {err.Code}");
        const string expectedMessage =
            "Transaction cancelled, please refer cancellation reasons for specific reasons "
            + "[DuplicateItem, None]";
        Ensure(err.Message == expectedMessage, $"expected message {Py.Repr(expectedMessage)}, got {Py.Repr(err.Message)}");
        var reasons = err.CancellationReasons();
        Ensure(reasons is { Count: 2 }, $"expected 2 positionally-aligned cancellation reasons, got {Py.Repr(reasons?.Select(Repr).ToList())}");
        Ensure(reasons![0].Code == "DuplicateItem", $"expected the duplicate INSERT at index 0 to report DuplicateItem, got {Py.Repr(Repr(reasons[0]))}");
        Ensure(
            Py.Equal(Repr(reasons[1]), new Dictionary<string, object?> { ["Code"] = "None" }),
            $"the rolled-back sibling must carry Code \"None\" and no Message, got {Py.Repr(Repr(reasons[1]))}");

        // effect absent: the sibling INSERT's key never appears
        var siblingAfter = await Get(ddb, t, "sibling");
        Ensure(siblingAfter is null, $"canceled transaction's sibling INSERT wrote anyway: {Py.Repr(siblingAfter)}");

        // existing item unchanged: the collided-with item still carries its original tag
        var existingAfter = await Get(ddb, t, "existing");
        Ensure(
            Py.Equal(existingAfter?.GetValueOrDefault("tag"), S("original")),
            $"canceled transaction must leave the existing item untouched, got {Py.Repr(existingAfter)}");

        // restore: remove the collision, then the identical transaction commits both statements
        await ddb.DeleteItemAsync(t, Item(("pk", S("existing")), ("sk", S("s1"))));
        await ddb.ExecuteTransactionAsync(new ExecuteTransactionRequest
        {
            TransactStatements = [new ParameterizedStatement { Statement = duplicateStatement }, new ParameterizedStatement { Statement = siblingStatement }],
        });
        var restoredExisting = await Get(ddb, t, "existing");
        var restoredSibling = await Get(ddb, t, "sibling");
        Ensure(
            Py.Equal(restoredExisting?.GetValueOrDefault("tag"), S("should-not-persist")),
            $"expected the identical duplicate statement to succeed after restore, got {Py.Repr(restoredExisting)}");
        Ensure(
            Py.Equal(restoredSibling, Item(("pk", S("sibling")), ("sk", S("s1")))),
            $"expected the sibling INSERT to commit once the collision is removed, got {Py.Repr(restoredSibling)}");
    }

    private static async Task<Dictionary<string, AttributeValue>?> Get(IAmazonDynamoDB ddb, string t, string pk) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S(pk)), ("sk", S("s1"))), ConsistentRead = true })).Item;

    /// <summary>A cancellation reason as boto3 hands it back: only the members present.</summary>
    private static Dictionary<string, object?> Repr(CancellationReason reason)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (reason.Item is not null)
        {
            dict["Item"] = reason.Item;
        }

        if (reason.Code is not null)
        {
            dict["Code"] = reason.Code;
        }

        if (reason.Message is not null)
        {
            dict["Message"] = reason.Message;
        }

        return dict;
    }

    /// <summary>A pk/sk table for PartiQL statements.</summary>
    private static async Task<string> PartiqlTable(FidelityContext ctx)
    {
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 250);
        return name;
    }
}
