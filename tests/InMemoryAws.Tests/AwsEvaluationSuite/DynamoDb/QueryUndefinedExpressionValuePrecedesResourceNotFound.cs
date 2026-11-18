// Translated from behavioral/dynamodb/check_query_undefined_expression_value_precedes_resource_not_found.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-undefined-expression-value-precedes-resource-not-found</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryUndefinedExpressionValuePrecedesResourceNotFound
{
    private const string Check = "query-undefined-expression-value-precedes-resource-not-found";
    private const string ErrValidation = "ValidationException";
    private const string ErrNotFound = "ResourceNotFoundException";

    /// <summary>The (code, message) <paramref name="fn"/> is refused with. An unexpected success raises, naming it.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }

    /// <summary>
    /// A Query naming an absent table is ResourceNotFoundException, but an undefined expression
    /// attribute value in its KeyConditionExpression is a ValidationException that preempts the
    /// resource lookup, even alongside a wrong-typed operand and a third ANDed clause.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-undefined-expression-value-precedes-resource-not-found")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;

        // Positive control: a real table answers the same expression shape correctly, so the
        // refusals below are about the absent table and the expression, not the wiring.
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 1, maxAttempts: 60);
        await ddb.PutItemAsync(table, Item(("pk", S("x")), ("sk", S("y"))));
        var control = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :pk AND sk = :sk",
            ExpressionAttributeValues = Item((":pk", S("x")), (":sk", S("y"))),
        });
        Ensure(control.Count == 1, $"positive control: expected the one put item, got {control.Count}");

        var absent = FidelityContext.UniqueName("sim2real-behav-absent-");

        // Baseline: a well-formed expression against the absent table is ResourceNotFoundException.
        var (code, message) = await Error(() => ddb.QueryAsync(new QueryRequest
        {
            TableName = absent,
            KeyConditionExpression = "pk = :pk AND sk = :sk",
            ExpressionAttributeValues = Item((":pk", S("x")), (":sk", S("y"))),
        }));
        Ensure(code == ErrNotFound, $"a well-formed expression against an absent table gave {Py.Repr(code)}");
        Ensure(message == "Requested resource not found", $"a well-formed expression against an absent table: message {Py.Repr(message)}");

        // A wrong-typed operand (a List where a key condition value must be scalar) plus a third
        // ANDed clause, still against the absent table and with every placeholder defined: the
        // existence check still wins, so neither defect alone preempts it.
        (code, message) = await Error(() => ddb.QueryAsync(new QueryRequest
        {
            TableName = absent,
            KeyConditionExpression = "pk = :pk AND sk = :sk AND extra = :extra",
            ExpressionAttributeValues = Item((":pk", L(S("x"))), (":sk", S("y")), (":extra", S("z"))),
        }));
        Ensure(
            code == ErrNotFound,
            $"a wrong-typed operand plus a third clause against an absent table gave {Py.Repr(code)}, expected the existence check to still win");
        Ensure(
            message == "Requested resource not found",
            $"a wrong-typed operand plus a third clause against an absent table: message {Py.Repr(message)}");

        // The same wrong-typed operand and third clause, but now :extra is never defined in
        // ExpressionAttributeValues: the undefined placeholder is caught during expression parsing,
        // before the table is even looked up, so it is this ValidationException that AWS reports.
        (code, message) = await Error(() => ddb.QueryAsync(new QueryRequest
        {
            TableName = absent,
            KeyConditionExpression = "pk = :pk AND sk = :sk AND extra = :extra",
            ExpressionAttributeValues = Item((":pk", L(S("x"))), (":sk", S("y"))),
        }));
        Ensure(
            code == ErrValidation,
            $"a wrong-typed operand, an undefined placeholder and a third clause against an absent table gave {Py.Repr(code)}, expected the undefined placeholder to preempt the existence check");
        const string expected =
            "Invalid KeyConditionExpression: An expression attribute value used in expression is not " +
            "defined; attribute value: :extra";
        Ensure(message == expected, $"expected the message {Py.Repr(expected)}, got {Py.Repr(message)}");
    }
}
