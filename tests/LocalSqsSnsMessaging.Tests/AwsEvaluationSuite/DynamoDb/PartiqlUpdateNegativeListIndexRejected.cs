// Translated from behavioral/dynamodb/check_partiql_update_negative_list_index_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-update-negative-list-index-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlUpdateNegativeListIndexRejected
{
    private const string CheckId = "partiql-update-negative-list-index-rejected";

    /// <summary>The <c>(Code, Message)</c> <c>fn</c> raises. An unexpected success raises too, naming it.</summary>
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
    /// UPDATE ... SET on a negative list index is refused with the index named in the message
    /// and writes nothing; the identical statement against index 0 succeeds.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-update-negative-list-index-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbneg-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 1, maxAttempts: 60);

        var key = Item(("pk", S("r1")));
        await ddb.PutItemAsync(table, Item(("pk", S("r1")), ("tags", L(S("a"), S("b")))));

        var stmt = $"UPDATE \"{table}\" SET tags[-1] = ? WHERE pk = ? RETURNING MODIFIED NEW *";
        var wantCol = stmt.IndexOf("-1]", StringComparison.Ordinal) + 2;
        (string? Code, string? Message) want = ("ValidationException", $"List index is not within the allowable range; index: [-1] at 1:{wantCol}:1");
        var (code, message) = await Error(() => ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = stmt,
            Parameters = [S("y"), S("r1")],
        }));
        Ensure((code, message) == want, $"SET tags[-1] answered {Py.Repr((code, message))}, expected {Py.Repr(want)}");

        var unchanged = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(Py.Equal(unchanged["tags"], L(S("a"), S("b"))), $"a refused SET must write nothing; tags is {Py.Repr(unchanged["tags"])}");

        // Positive control: the identical statement against a non-negative index succeeds.
        var ok = (await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"UPDATE \"{table}\" SET tags[0] = ? WHERE pk = ? RETURNING MODIFIED NEW *",
            Parameters = [S("y"), S("r1")],
        })).Items;
        Ensure(Py.Equal(ok, new[] { Item(("tags", L(S("y")))) }), $"SET tags[0] returned {Py.Repr(ok)}");
        var stored = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(Py.Equal(stored["tags"], L(S("y"), S("b"))), $"stored tags after SET tags[0] is {Py.Repr(stored["tags"])}");
    }
}
