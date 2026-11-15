// Translated from behavioral/dynamodb/check_partiql_from_clause_path_arity_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-from-clause-path-arity-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlFromClausePathArityRejected
{
    private const string CheckId = "partiql-from-clause-path-arity-rejected";

    /// <summary>(Error.Code, Error.Message) the ClientError fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the statement to be rejected, but it succeeded: {Py.Repr(result)}");
    }

    /// <summary>
    /// A PartiQL FROM path with 3 components, or an empty index component, is a top-level
    /// ValidationException with no effect on the item or on a subsequent well-formed statement.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ql-reference.select.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-from-clause-path-arity-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 30);
        await ddb.PutItemAsync(table, Item(("pk", S("a")), ("sk", S("1")), ("val", S("orig"))));

        Task<ExecuteStatementResponse> Execute(string statement) => ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = statement });

        var threeComponent = $"SELECT * FROM \"{table}\".\"idx\".\"extra\" WHERE pk = 'a' AND sk = '1'";
        var (code, message) = await Error(() => Execute(threeComponent));
        Ensure(code == "ValidationException", $"3-component SELECT: expected ValidationException, got {code}");
        const string expectedArityMsg = "A path may contain at most 2 components in the FROM clause";
        Ensure(message == expectedArityMsg, $"3-component SELECT: unexpected message {Py.Repr(message)}");

        var emptyIndex = $"SELECT * FROM \"{table}\".\"\" WHERE pk = 'a' AND sk = '1'";
        (code, message) = await Error(() => Execute(emptyIndex));
        Ensure(code == "ValidationException", $"empty-index SELECT: expected ValidationException, got {code}");
        const string expectedEmptyMsg = "Path component cannot be an empty string";
        Ensure(message == expectedEmptyMsg, $"empty-index SELECT: unexpected message {Py.Repr(message)}");

        var threeComponentDelete = $"DELETE FROM \"{table}\".\"idx\".\"extra\" WHERE pk = 'a' AND sk = '1'";
        (code, message) = await Error(() => Execute(threeComponentDelete));
        Ensure(code == "ValidationException", $"3-component DELETE: expected ValidationException, got {code}");
        Ensure(message == expectedArityMsg, $"3-component DELETE: unexpected message {Py.Repr(message)}");

        var emptyIndexDelete = $"DELETE FROM \"{table}\".\"\" WHERE pk = 'a' AND sk = '1'";
        (code, message) = await Error(() => Execute(emptyIndexDelete));
        Ensure(code == "ValidationException", $"empty-index DELETE: expected ValidationException, got {code}");
        Ensure(message == expectedEmptyMsg, $"empty-index DELETE: unexpected message {Py.Repr(message)}");

        var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("a")), ("sk", S("1"))), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(item, Item(("pk", S("a")), ("sk", S("1")), ("val", S("orig")))),
            $"the rejected DELETE statements must leave the item unchanged, got {Py.Repr(item)}");

        // Positive control: the table's data plane is unaffected by the four rejections.
        var stillWorks = (await Execute($"SELECT * FROM \"{table}\" WHERE pk = 'a' AND sk = '1'")).Items;
        Ensure(
            Py.Equal(stillWorks, new[] { Item(("pk", S("a")), ("sk", S("1")), ("val", S("orig"))) }),
            $"a well-formed statement after the rejections should still succeed, got {Py.Repr(stillWorks)}");
    }
}
