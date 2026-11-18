// Translated from behavioral/dynamodb/check_partiql_delete_noop_on_absent_key_and_update_from_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-delete-noop-on-absent-key-and-update-from-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlDeleteNoopOnAbsentKeyAndUpdateFromRejected
{
    private const string CheckId = "partiql-delete-noop-on-absent-key-and-update-from-rejected";
    private const string ExpectedUpdateFromMessage = "Statement wasn't well formed, can't be processed: Expected data manipulation";

    private static async Task<Dictionary<string, AttributeValue>?> Stored(IAmazonDynamoDB ddb, string table, string pk, string sk) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk)), ("sk", S(sk))), ConsistentRead = true })).Item;

    /// <summary>The (Code, Message) <paramref name="fn"/> raises. An unexpected success raises too, naming what came back.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the statement to be refused, but it succeeded: {Py.Repr(result)}");
    }

    /// <summary>
    /// A PartiQL DELETE on a key the table does not hold is a no-op even with an extra non-key
    /// predicate in WHERE, and an UPDATE that places a FROM clause before SET/WHERE is rejected as
    /// malformed, leaving the stored item untouched in both cases.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ql-reference.delete.html
    /// https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ql-reference.update.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-delete-noop-on-absent-key-and-update-from-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-pqlfromdel-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 60);

        var item = Item(("pk", S("p1")), ("sk", S("s1")), ("status", S("active")));
        await ddb.PutItemAsync(table, item);

        // 1. DELETE on a key the table never held, carrying an extra non-key predicate: the
        // key-not-found outcome wins over the predicate, so the statement succeeds as a no-op.
        var r1 = await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"DELETE FROM \"{table}\" WHERE pk = 'absent' AND sk = 'absent' AND status = 'active' RETURNING ALL OLD *",
        });
        Ensure(r1.Items is { Count: 0 }, $"DELETE on an absent key with an extra non-key predicate must be a no-op, got {Py.Repr(r1.Items)}");
        Ensure(
            Py.Equal(await Stored(ddb, table, "p1", "s1"), item),
            $"the unrelated existing item must be untouched, got {Py.Repr(await Stored(ddb, table, "p1", "s1"))}");

        // 2. UPDATE that places a FROM clause before SET/WHERE: the statement is not well formed and
        // is rejected without touching the item SET would have modified.
        var (code, message) = await Error(() => ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"UPDATE \"{table}\" FROM \"{table}\" SET status = 'changed' WHERE pk = 'p1' AND sk = 's1'",
        }));
        Ensure(code == "ValidationException", $"UPDATE with FROM before SET/WHERE must be refused with ValidationException, got {Py.Repr(code)}");
        Ensure(message == ExpectedUpdateFromMessage, $"expected message {Py.Repr(ExpectedUpdateFromMessage)}, got {Py.Repr(message)}");
        Ensure(
            Py.Equal(await Stored(ddb, table, "p1", "s1"), item),
            $"the rejected UPDATE must leave the item untouched, got {Py.Repr(await Stored(ddb, table, "p1", "s1"))}");

        // Positive control: the same UPDATE without the FROM clause, and the same DELETE against the
        // key it actually holds, both succeed.
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"UPDATE \"{table}\" SET status = 'changed' WHERE pk = 'p1' AND sk = 's1'" });
        var changed = Item(("pk", S("p1")), ("sk", S("s1")), ("status", S("changed")));
        Ensure(
            Py.Equal(await Stored(ddb, table, "p1", "s1"), changed),
            $"the well-formed UPDATE must have applied, got {Py.Repr(await Stored(ddb, table, "p1", "s1"))}");
        var r2 = await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"DELETE FROM \"{table}\" WHERE pk = 'p1' AND sk = 's1' RETURNING ALL OLD *" });
        Ensure(
            Py.Equal(r2.Items, new[] { changed }),
            $"DELETE against the key it actually holds must remove it and return {Py.Repr(new[] { changed })}, got {Py.Repr(r2.Items)}");
        Ensure(
            await Stored(ddb, table, "p1", "s1") is null,
            $"the item must be gone after the successful DELETE, got {Py.Repr(await Stored(ddb, table, "p1", "s1"))}");
    }
}
