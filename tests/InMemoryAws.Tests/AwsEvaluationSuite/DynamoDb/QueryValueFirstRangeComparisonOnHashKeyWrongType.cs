// Translated from behavioral/dynamodb/check_query_value_first_range_comparison_on_hash_key_wrong_type.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-value-first-range-comparison-on-hash-key-wrong-type</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryValueFirstRangeComparisonOnHashKeyWrongType
{
    private const string Check = "query-value-first-range-comparison-on-hash-key-wrong-type";
    private const string ErrValidation = "ValidationException";
    private const string NotSupported = "Query key condition not supported";
    private const string SchemaTypeMismatch = "One or more parameter values were invalid: Condition parameter type does not match schema type";

    private static string OperandTypeMismatch(string op, string operandType) =>
        "Invalid KeyConditionExpression: Incorrect operand type for operator or function; " +
        $"operator or function: {op}, operand type: {operandType}";

    /// <summary>
    /// A Query range comparison on a hash key is reported as unsupported when its
    /// operand type matches the key's schema, as a schema-type mismatch for a type the
    /// operator still accepts, else by operand type with the operator named as written.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-value-first-range-comparison-on-hash-key-wrong-type")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-hkvf-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 60);
        foreach (var pk in new[] { "a", "b", "c" })
        {
            await ddb.PutItemAsync(table, Item(("pk", S(pk))));
        }

        Task<(string? Code, string? Message)> QueryError(string kce, AttributeValue operand) => Error(() => ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = kce,
            ExpressionAttributeValues = Item((":v", operand)),
        }));

        // Each pair is a value-first range comparison against its logical mirror, key-first,
        // both against the HASH key of a table with no sort key.
        (string ValueFirstKce, string ValueFirstOp, string KeyFirstKce, string KeyFirstOp)[] pairs =
        [
            (":v >= pk", ">=", "pk <= :v", "<="),
            (":v <= pk", "<=", "pk >= :v", ">="),
            (":v > pk", ">", "pk < :v", "<"),
            (":v < pk", "<", "pk > :v", ">"),
        ];
        foreach (var (valueFirstKce, valueFirstOp, keyFirstKce, keyFirstOp) in pairs)
        {
            // Operand type matches the key's own schema type (S): the operator itself is the
            // only problem, so AWS reports the condition as unsupported rather than citing a
            // type at all, for both the value-first form and its key-first mirror.
            var (code, message) = await QueryError(valueFirstKce, S("x"));
            Ensure(code == ErrValidation, $"{Py.Repr(valueFirstKce)}: expected {ErrValidation}, got {code} ({Py.Repr(message)})");
            Ensure(message == NotSupported, $"{Py.Repr(valueFirstKce)}: expected message {Py.Repr(NotSupported)}, got {Py.Repr(message)}");
            var (code2, message2) = await QueryError(keyFirstKce, S("x"));
            Ensure(code2 == ErrValidation, $"{Py.Repr(keyFirstKce)}: expected {ErrValidation}, got {code2} ({Py.Repr(message2)})");
            Ensure(message2 == NotSupported, $"{Py.Repr(keyFirstKce)}: expected message {Py.Repr(NotSupported)}, got {Py.Repr(message2)}");

            // Operand type is N: the operator accepts N for other key types, so AWS reports a
            // schema-type mismatch instead of the unsupported-operator message above, on both
            // the value-first form and its key-first mirror.
            var (code3, message3) = await QueryError(valueFirstKce, N("1"));
            Ensure(code3 == ErrValidation, $"{Py.Repr(valueFirstKce)}/N: expected {ErrValidation}, got {code3} ({Py.Repr(message3)})");
            Ensure(
                message3 == SchemaTypeMismatch,
                $"{Py.Repr(valueFirstKce)}/N: expected message {Py.Repr(SchemaTypeMismatch)}, got {Py.Repr(message3)}");
            var (code4, message4) = await QueryError(keyFirstKce, N("1"));
            Ensure(code4 == ErrValidation, $"{Py.Repr(keyFirstKce)}/N: expected {ErrValidation}, got {code4} ({Py.Repr(message4)})");
            Ensure(
                message4 == SchemaTypeMismatch,
                $"{Py.Repr(keyFirstKce)}/N: expected message {Py.Repr(SchemaTypeMismatch)}, got {Py.Repr(message4)}");

            // BOOL is illegal for every one of these operators regardless of which side it
            // sits on, so the operand-type error names the operator exactly as written.
            var (code5, message5) = await QueryError(valueFirstKce, Bool(true));
            Ensure(code5 == ErrValidation, $"{Py.Repr(valueFirstKce)}/BOOL: expected {ErrValidation}, got {code5} ({Py.Repr(message5)})");
            var expected5 = OperandTypeMismatch(valueFirstOp, "BOOL");
            Ensure(message5 == expected5, $"{Py.Repr(valueFirstKce)}/BOOL: expected message {Py.Repr(expected5)}, got {Py.Repr(message5)}");
            var (code6, message6) = await QueryError(keyFirstKce, Bool(true));
            Ensure(code6 == ErrValidation, $"{Py.Repr(keyFirstKce)}/BOOL: expected {ErrValidation}, got {code6} ({Py.Repr(message6)})");
            var expected6 = OperandTypeMismatch(keyFirstOp, "BOOL");
            Ensure(message6 == expected6, $"{Py.Repr(keyFirstKce)}/BOOL: expected message {Py.Repr(expected6)}, got {Py.Repr(message6)}");
            Ensure(
                message5 != message6,
                $"value-first {Py.Repr(valueFirstKce)} and its mirror {Py.Repr(keyFirstKce)} reported the same " +
                $"BOOL-operand message {Py.Repr(message5)}; the operator token should differ between them");
        }

        // Positive control: an equality key condition on the same hash key still selects the
        // expected item after every rejection above.
        var control = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :p",
            ConsistentRead = true,
            ExpressionAttributeValues = Item((":p", S("b"))),
        });
        var pks = control.Items.Select(it => it["pk"].S).Order(StringComparer.Ordinal).ToList();
        Ensure(pks.SequenceEqual(["b"]), $"'pk = :p' with p='b' selected {Py.Repr(pks)}, expected ['b']");
    }

    /// <summary>The AWS error (code, message) <paramref name="fn"/> raises. Raises when the call SUCCEEDS.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be rejected, but it SUCCEEDED: {Py.Repr(result)}");
    }
}
