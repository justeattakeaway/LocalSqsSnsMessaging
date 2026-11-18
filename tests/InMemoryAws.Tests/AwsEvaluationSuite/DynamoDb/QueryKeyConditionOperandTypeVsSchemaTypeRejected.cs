// Translated from behavioral/dynamodb/check_query_key_condition_operand_type_vs_schema_type_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-key-condition-operand-type-vs-schema-type-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryKeyConditionOperandTypeVsSchemaTypeRejected
{
    private const string Check = "query-key-condition-operand-type-vs-schema-type-rejected";
    private const string ErrValidation = "ValidationException";

    private const string SchemaTypeMismatch =
        "One or more parameter values were invalid: Condition parameter type does not match schema type";

    private static readonly (string Operator, string Expr)[] Cells =
    [
        ("begins_with", "pk = :p AND begins_with(sk, :v)"),
        ("<", "pk = :p AND sk < :v"),
        ("BETWEEN", "pk = :p AND sk BETWEEN :v AND :v"),
        ("=", "pk = :p AND sk = :v"),
    ];

    private static string OperandTypeMismatch(string op, string operandType) =>
        "Invalid KeyConditionExpression: Incorrect operand type for operator or function; " +
        $"operator or function: {op}, operand type: {operandType}";

    /// <summary>The AWS error (code, message) fn() raises. Raises when the call SUCCEEDS.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be rejected, but it SUCCEEDED: {result}");
        }

        return (error.Code, error.Message);
    }

    private static async Task<string> MakeTable(FidelityContext ctx, IAmazonDynamoDB ddb, ScalarAttributeType skType)
    {
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", skType)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 30);
        return table;
    }

    private static AttributeValue Operand(string operandType) => operandType switch
    {
        "S" => S("x"),
        "N" => N("1"),
        "BOOL" => Bool(true),
        _ => throw new KeyNotFoundException(operandType),
    };

    /// <summary>
    /// A Query key-condition operand whose type is illegal for the operator itself is reported as
    /// an operand-type error; one that is operator-legal but wrong for the key's own schema type is
    /// reported as a schema-type error instead. Neither case writes anything to the table.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-key-condition-operand-type-vs-schema-type-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableS = await MakeTable(ctx, ddb, ScalarAttributeType.S);
        var tableN = await MakeTable(ctx, ddb, ScalarAttributeType.N);

        // Positive control per table: the correctly typed form is accepted on an empty table.
        foreach (var (table, skType) in new[] { (tableS, "S"), (tableN, "N") })
        {
            var control = await ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                KeyConditionExpression = "pk = :p AND sk = :s",
                ExpressionAttributeValues = Item((":p", S("p")), (":s", Operand(skType))),
            });
            Ensure(control.Count == 0, $"positive control on the {skType} sort-key table: expected 0 items, got {control.Count}");
        }

        async Task RunCell(string table, string expr, string operandType, string expectedCode, string expectedMessage, string label)
        {
            var (code, message) = await Error(() => ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                KeyConditionExpression = expr,
                ExpressionAttributeValues = Item((":p", S("p")), (":v", Operand(operandType))),
            }));
            Ensure(code == expectedCode, $"{label}: expected {expectedCode}, got {code} ({Py.Repr(message)})");
            Ensure(message == expectedMessage, $"{label}: expected message {Py.Repr(expectedMessage)}, got {Py.Repr(message)}");
        }

        // --- S sort-key table: wrong operand types are N and BOOL ---
        foreach (var (op, expr) in Cells)
        {
            foreach (var operandType in new[] { "N", "BOOL" })
            {
                var isOperatorIllegal = (op == "begins_with" && operandType == "N") ||
                    (op is "begins_with" or "<" or "BETWEEN" && operandType == "BOOL");
                var expectedCode = ErrValidation;
                var expectedMessage = isOperatorIllegal ? OperandTypeMismatch(op, operandType) : SchemaTypeMismatch;
                await RunCell(tableS, expr, operandType, expectedCode, expectedMessage, $"S-sortkey table, operator={op}, operand={operandType}");
            }
        }

        // --- N sort-key table: wrong operand types are S and BOOL ---
        foreach (var (op, expr) in Cells)
        {
            foreach (var operandType in new[] { "S", "BOOL" })
            {
                var isOperatorIllegal = op is "begins_with" or "<" or "BETWEEN" && operandType == "BOOL";
                var expectedCode = ErrValidation;
                var expectedMessage = isOperatorIllegal ? OperandTypeMismatch(op, operandType) : SchemaTypeMismatch;
                await RunCell(tableN, expr, operandType, expectedCode, expectedMessage, $"N-sortkey table, operator={op}, operand={operandType}");
            }
        }

        // Read back: none of the rejected cells, on either table, wrote anything.
        foreach (var table in new[] { tableS, tableN })
        {
            var final = await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true });
            Ensure(final.Count == 0, $"table {Py.Repr(table)} has {final.Count} item(s) after only rejected Query calls; expected 0");
        }
    }
}
