// Translated from behavioral/dynamodb/check_partiql_delete_returning_all_new_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-delete-returning-all-new-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlDeleteReturningAllNewRejected
{
    private const string CheckId = "partiql-delete-returning-all-new-rejected";
    private const string ExpectedMessage = "Invalid returning clause: RETURNING ALL NEW *. Only RETURNING ALL OLD * is allowed in DELETE statements.";

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
    /// DELETE ... RETURNING ALL NEW * is refused with a ValidationException and deletes nothing, while
    /// RETURNING ALL OLD * on the same item removes it and returns the removed item.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ql-reference.delete.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-delete-returning-all-new-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-pqldelrej-");
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

        Dictionary<string, AttributeValue> Key() => Item(("pk", S("p1")), ("sk", S("s1")));
        var item = Item(("pk", S("p1")), ("sk", S("s1")), ("name", S("Ada")), ("qty", N("3")));
        await ddb.PutItemAsync(table, item);
        const string where = "WHERE pk = 'p1' AND sk = 's1'";

        var (code, message) = await Error(() => ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"DELETE FROM \"{table}\" {where} RETURNING ALL NEW *" }));
        Ensure(code == "ValidationException", $"DELETE ... RETURNING ALL NEW * must be refused with ValidationException, got {Py.Repr(code)}");
        Ensure(message == ExpectedMessage, $"expected message {Py.Repr(ExpectedMessage)}, got {Py.Repr(message)}");
        var kept = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
        Ensure(Py.Equal(kept, item), $"the refused DELETE must leave the item intact; it now reads {Py.Repr(kept)}");

        // Positive control: the valid RETURNING form of the same DELETE removes the item.
        var removed = await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"DELETE FROM \"{table}\" {where} RETURNING ALL OLD *" });
        Ensure(Py.Equal(removed.Items, new[] { item }), $"DELETE ... RETURNING ALL OLD * must return the removed item {Py.Repr(new[] { item })}, got {Py.Repr(removed.Items)}");
        var gone = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
        Ensure(gone is null, $"the valid DELETE must remove the item, a consistent read found {Py.Repr(gone)}");
    }
}
