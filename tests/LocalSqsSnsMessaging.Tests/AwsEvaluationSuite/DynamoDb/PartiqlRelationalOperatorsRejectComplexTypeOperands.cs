// Translated from behavioral/dynamodb/check_partiql_relational_operators_reject_complex_type_operands.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-relational-operators-reject-complex-type-operands</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlRelationalOperatorsRejectComplexTypeOperands
{
    private const string CheckId = "partiql-relational-operators-reject-complex-type-operands";

    private static readonly (string Op, string Type, string Clause)[] Legs =
    [
        ("<", "L", "val < [1, 2]"),
        ("<=", "M", "val <= {'x': 1}"),
        (">", "SS", "val > <<'a', 'b'>>"),
        (">=", "BOOL", "val >= true"),
        ("BETWEEN", "NULL", "val BETWEEN NULL AND 5"),
    ];

    /// <summary>
    /// Call fn(); return (code, message). An unexpected SUCCESS raises, since an emulator
    /// accepting a comparison real AWS refuses is the strongest signal available.
    /// </summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the statement to be rejected, but it SUCCEEDED with {Py.Repr(result)}");
    }

    /// <summary>
    /// Each relational comparator (&lt;, &lt;=, &gt;, &gt;=, BETWEEN) rejects a List, Map, String Set,
    /// Boolean or Null literal operand with ValidationException, and a rejected UPDATE/DELETE
    /// WHERE clause writes nothing.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-relational-operators-reject-complex-type-operands")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-pqlrel-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 60);

        var item = Item(("pk", S("item")), ("val", N("5")));
        await ddb.PutItemAsync(table, item);

        // Positive control: the same comparator over two comparable numbers succeeds, so a rejection
        // below is attributable to the operand's type, not to '<' itself being broken.
        var control = await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"SELECT * FROM \"{table}\" WHERE val < 10", ConsistentRead = true });
        Ensure(Py.Equal(control.Items, new[] { item }), $"positive control returned {Py.Repr(control.Items)}, expected [{Py.Repr(item)}]");

        foreach (var (op, typ, clause) in Legs)
        {
            var (code, message) = await Error(() => ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"SELECT * FROM \"{table}\" WHERE {clause}" }));
            Ensure(code == "ValidationException", $"{op} against a {typ} operand gave code {Py.Repr(code)}");
            var expected = $"Incorrect operand type for operator or function; operator or function: {op}, operand type: {typ}";
            Ensure(message == expected, $"{op} against a {typ} operand: expected message {Py.Repr(expected)}, got {Py.Repr(message)}");
        }

        // A rejected WHERE clause in UPDATE and DELETE statements writes nothing: the item comes
        // back exactly as stored, with no attribute added and no deletion performed.
        {
            var (code, message) = await Error(() => ddb.ExecuteStatementAsync(new ExecuteStatementRequest
            {
                Statement = $"UPDATE \"{table}\" SET other = 1 WHERE pk = 'item' AND val < [1, 2]",
            }));
            Ensure(code == "ValidationException", $"UPDATE with a rejected WHERE clause gave code {Py.Repr(code)}");
            const string expected = "Incorrect operand type for operator or function; operator or function: <, operand type: L";
            Ensure(message == expected, $"UPDATE: expected message {Py.Repr(expected)}, got {Py.Repr(message)}");
        }

        {
            var (code, message) = await Error(() => ddb.ExecuteStatementAsync(new ExecuteStatementRequest
            {
                Statement = $"DELETE FROM \"{table}\" WHERE pk = 'item' AND val <= true",
            }));
            Ensure(code == "ValidationException", $"DELETE with a rejected WHERE clause gave code {Py.Repr(code)}");
            const string expected = "Incorrect operand type for operator or function; operator or function: <=, operand type: BOOL";
            Ensure(message == expected, $"DELETE: expected message {Py.Repr(expected)}, got {Py.Repr(message)}");
        }

        var after = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("item"))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(after, item), $"item must be unchanged after both rejected statements, got {Py.Repr(after)}");
    }
}
