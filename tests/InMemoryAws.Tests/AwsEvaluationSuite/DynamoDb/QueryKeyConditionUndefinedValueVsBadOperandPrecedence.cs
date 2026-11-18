// Translated from behavioral/dynamodb/check_query_key_condition_undefined_value_vs_bad_operand_precedence.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-key-condition-undefined-value-vs-bad-operand-precedence</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryKeyConditionUndefinedValueVsBadOperandPrecedence
{
    private const string Check = "query-key-condition-undefined-value-vs-bad-operand-precedence";
    private const string ErrValidation = "ValidationException";
    private const string ErrNotFound = "ResourceNotFoundException";

    private const string UndefinedValue =
        "Invalid KeyConditionExpression: An expression attribute value used in expression is not " +
        "defined; attribute value: :undef";

    private const string BadOperand =
        "Invalid KeyConditionExpression: Incorrect operand type for operator or function; " +
        "operator or function: begins_with, operand type: N";

    private const string ThirdCondition =
        "The number of query conditions (3) exceeds the number of key attributes defined in the " +
        "schema (2)";

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
    /// A KeyConditionExpression's undefined placeholder and its wrong-typed begins_with operand
    /// are reported in the order they are written; the operand error alone still beats a third
    /// ANDed clause and a missing table.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-key-condition-undefined-value-vs-bad-operand-precedence")]
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
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(table, Item(("pk", S("x")), ("sk", S("y")), ("extra", S("z"))));

        Task<QueryResponse> Query(string tableName, string kce, Dictionary<string, AttributeValue> values) =>
            ddb.QueryAsync(new QueryRequest { TableName = tableName, KeyConditionExpression = kce, ExpressionAttributeValues = values });

        // Positive control: the plain two-clause form the cases below are variations of.
        var control = await Query(table, "pk = :pk AND sk = :sk", Item((":pk", S("x")), (":sk", S("y"))));
        Ensure(control.Count == 1, $"positive control: expected the one put item, got {control.Count}");

        // Order A: the undefined placeholder (:undef, in the pk clause) is written before the
        // wrong-typed begins_with operand (:bad, defined but N where the sort key is S).
        var (code, message) = await Error(() => Query(table, "pk = :undef AND begins_with(sk, :bad)", Item((":bad", N("1")))));
        Ensure(code == ErrValidation, $"order A (undefined first): expected {ErrValidation}, got {code} ({Py.Repr(message)})");
        Ensure(message == UndefinedValue, $"order A (undefined first): expected {Py.Repr(UndefinedValue)}, got {Py.Repr(message)}");

        // Order B: the identical two defects, with the begins_with clause written first.
        (code, message) = await Error(() => Query(table, "begins_with(sk, :bad) AND pk = :undef", Item((":bad", N("1")))));
        Ensure(code == ErrValidation, $"order B (operand first): expected {ErrValidation}, got {code} ({Py.Repr(message)})");
        Ensure(message == BadOperand, $"order B (operand first): expected {Py.Repr(BadOperand)}, got {Py.Repr(message)}");

        // The wrong-typed operand alone (every placeholder defined) still beats a third ANDed
        // clause naming a real, non-key attribute.
        (code, message) = await Error(() => Query(
            table,
            "pk = :pk AND begins_with(sk, :bad) AND extra = :extra",
            Item((":pk", S("x")), (":bad", N("1")), (":extra", S("z")))));
        Ensure(code == ErrValidation, $"operand vs third clause: expected {ErrValidation}, got {code} ({Py.Repr(message)})");
        Ensure(message == BadOperand, $"operand vs third clause: expected {Py.Repr(BadOperand)}, got {Py.Repr(message)}");

        // Confirm the third-clause message itself still exists on this table when the operand is
        // well-typed, so the case above is shown to be about the operand, not about this table
        // tolerating a third clause.
        (code, message) = await Error(() => Query(
            table,
            "pk = :pk AND sk = :sk AND extra = :extra",
            Item((":pk", S("x")), (":sk", S("y")), (":extra", S("z")))));
        Ensure(code == ErrValidation, $"well-typed third clause: expected {ErrValidation}, got {code} ({Py.Repr(message)})");
        Ensure(message == ThirdCondition, $"well-typed third clause: expected {Py.Repr(ThirdCondition)}, got {Py.Repr(message)}");

        // The wrong-typed operand alone (no undefined placeholder, no third clause) still beats a
        // missing table.
        var absent = FidelityContext.UniqueName("sim2real-behav-absent-");
        (code, message) = await Error(() => Query(absent, "pk = :pk AND begins_with(sk, :bad)", Item((":pk", S("x")), (":bad", N("1")))));
        Ensure(code == ErrValidation, $"operand vs missing table: expected {ErrValidation}, got {code} ({Py.Repr(message)})");
        Ensure(message == BadOperand, $"operand vs missing table: expected {Py.Repr(BadOperand)}, got {Py.Repr(message)}");

        // Baseline: the well-formed form against that same absent table name is the existence error.
        (code, message) = await Error(() => Query(absent, "pk = :pk AND sk = :sk", Item((":pk", S("x")), (":sk", S("y")))));
        Ensure(code == ErrNotFound, $"well-formed call against missing table: expected {ErrNotFound}, got {code} ({Py.Repr(message)})");
        Ensure(message == "Requested resource not found", $"well-formed call against missing table: expected message, got {Py.Repr(message)}");

        // Final control: the real table still answers correctly after every rejection above.
        var final = await Query(table, "pk = :pk AND sk = :sk", Item((":pk", S("x")), (":sk", S("y"))));
        Ensure(final.Count == 1, $"final control: expected the one put item, got {final.Count}");
    }
}
