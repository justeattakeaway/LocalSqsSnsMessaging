// Translated from behavioral/dynamodb/check_execute_statement_ordering_comparison_rejects_parameterized_boolean_operand.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.execute-statement-ordering-comparison-rejects-parameterized-boolean-operand</summary>
[Category("AwsEvaluationSuite")]
public sealed class ExecuteStatementOrderingComparisonRejectsParameterizedBooleanOperand
{
    private const string CheckId = "execute-statement-ordering-comparison-rejects-parameterized-boolean-operand";

    /// <summary>
    /// ExecuteStatement rejects a '&lt;' comparison whose operand is a Boolean value bound through
    /// the Parameters list, with the same ValidationException an inline literal operand gets, and a
    /// rejected UPDATE's WHERE clause writes nothing.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ql-operators.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.execute-statement-ordering-comparison-rejects-parameterized-boolean-operand")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var (ddb, table) = await Table(ctx);

        var item = Item(("pk", S("row")), ("num", N("5")));
        await ddb.PutItemAsync(table, item);

        // Positive control: the same parameterized comparator with an orderable (Number) operand
        // succeeds, so a rejection below is attributable to the Boolean operand, not to '?' binding.
        var control = await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"SELECT * FROM \"{table}\" WHERE pk = ? AND num < ?",
            Parameters = [S("row"), N("10")],
            ConsistentRead = true,
        });
        Ensure(Py.Equal(control.Items, new[] { item }), $"positive control returned {Py.Repr(control.Items)}, expected [{Py.Repr(item)}]");

        var (code, message) = await Error(() => ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"SELECT * FROM \"{table}\" WHERE pk = ? AND num < ?",
            Parameters = [S("row"), Bool(true)],
            ConsistentRead = true,
        }));
        Ensure(code == "ValidationException", $"'<' against a parameterized BOOL operand gave code {Py.Repr(code)}");
        const string expected = "Incorrect operand type for operator or function; operator or function: <, operand type: BOOL";
        Ensure(message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(message)}");

        (code, message) = await Error(() => ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"UPDATE \"{table}\" SET other = ? WHERE pk = ? AND num < ?",
            Parameters = [N("1"), S("row"), Bool(true)],
        }));
        Ensure(code == "ValidationException", $"UPDATE with a rejected WHERE clause gave code {Py.Repr(code)}");
        Ensure(message == expected, $"UPDATE: expected message {Py.Repr(expected)}, got {Py.Repr(message)}");

        var after = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("row"))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(after, item), $"item must be unchanged after the rejected statement, got {Py.Repr(after)}");
    }

    private static async Task<(IAmazonDynamoDB Ddb, string Name)> Table(FidelityContext ctx)
    {
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName("sim2real-behav-pqlboolparam-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 250);
        return (ddb, name);
    }

    /// <summary>(Error.Code, Error.Message) of the ClientError fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
        }

        return (error.Code, error.Message);
    }
}
