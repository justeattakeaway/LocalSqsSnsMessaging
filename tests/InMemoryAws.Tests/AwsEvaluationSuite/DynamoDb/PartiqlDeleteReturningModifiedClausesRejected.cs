// Translated from behavioral/dynamodb/check_partiql_delete_returning_modified_clauses_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-delete-returning-modified-clauses-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlDeleteReturningModifiedClausesRejected
{
    private const string CheckId = "partiql-delete-returning-modified-clauses-rejected";

    private static string MessageFor(string clause) =>
        $"Invalid returning clause: RETURNING {clause}. "
        + "Only RETURNING ALL OLD * is allowed in DELETE statements.";

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

    private static async Task<Dictionary<string, AttributeValue>?> Get(IAmazonDynamoDB ddb, string table, Dictionary<string, AttributeValue> key)
    {
        var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        return item is { Count: > 0 } ? item : null;
    }

    /// <summary>
    /// DELETE ... RETURNING MODIFIED NEW * and RETURNING MODIFIED OLD * are each refused with a
    /// ValidationException naming the clause, deleting nothing; RETURNING ALL OLD * on the same item
    /// still removes it and returns the removed item.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ql-reference.delete.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-delete-returning-modified-clauses-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-pqldelmod-");
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

        var keyNew = Item(("pk", S("p1")), ("sk", S("s1")));
        var itemNew = Item(("pk", S("p1")), ("sk", S("s1")), ("name", S("Ada")), ("qty", N("3")));
        await ddb.PutItemAsync(table, itemNew);

        var keyOld = Item(("pk", S("p2")), ("sk", S("s2")));
        var itemOld = Item(("pk", S("p2")), ("sk", S("s2")), ("name", S("Bea")), ("qty", N("5")));
        await ddb.PutItemAsync(table, itemOld);

        const string whereNew = "WHERE pk = 'p1' AND sk = 's1'";
        var (code, message) = await Error(() => ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"DELETE FROM \"{table}\" {whereNew} RETURNING MODIFIED NEW *",
        }));
        Ensure(code == "ValidationException", $"DELETE ... RETURNING MODIFIED NEW * must be refused with ValidationException, got {Py.Repr(code)}");
        var expectedNew = MessageFor("MODIFIED NEW *");
        Ensure(message == expectedNew, $"expected message {Py.Repr(expectedNew)}, got {Py.Repr(message)}");
        var keptNew = await Get(ddb, table, keyNew);
        Ensure(Py.Equal(keptNew, itemNew), $"the refused DELETE must leave the item intact; it now reads {Py.Repr(keptNew)}");

        const string whereOld = "WHERE pk = 'p2' AND sk = 's2'";
        (code, message) = await Error(() => ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"DELETE FROM \"{table}\" {whereOld} RETURNING MODIFIED OLD *",
        }));
        Ensure(code == "ValidationException", $"DELETE ... RETURNING MODIFIED OLD * must be refused with ValidationException, got {Py.Repr(code)}");
        var expectedOld = MessageFor("MODIFIED OLD *");
        Ensure(message == expectedOld, $"expected message {Py.Repr(expectedOld)}, got {Py.Repr(message)}");
        var keptOld = await Get(ddb, table, keyOld);
        Ensure(Py.Equal(keptOld, itemOld), $"the refused DELETE must leave the item intact; it now reads {Py.Repr(keptOld)}");

        // Positive control: the valid RETURNING form of the same DELETE removes the item.
        var removed = await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"DELETE FROM \"{table}\" {whereNew} RETURNING ALL OLD *" });
        Ensure(
            Py.Equal(removed.Items, new[] { itemNew }),
            $"DELETE ... RETURNING ALL OLD * must return the removed item {Py.Repr(new[] { itemNew })}, got {Py.Repr(removed.Items)}");
        var gone = await Get(ddb, table, keyNew);
        Ensure(gone is null, $"the valid DELETE must remove the item, a consistent read found {Py.Repr(gone)}");
    }
}
