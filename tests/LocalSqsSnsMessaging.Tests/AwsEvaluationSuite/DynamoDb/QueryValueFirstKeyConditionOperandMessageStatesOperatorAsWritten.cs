// Translated from behavioral/dynamodb/check_query_value_first_key_condition_operand_message_states_operator_as_written.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-value-first-key-condition-operand-message-states-operator-as-written</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryValueFirstKeyConditionOperandMessageStatesOperatorAsWritten
{
    private const string Check = "query-value-first-key-condition-operand-message-states-operator-as-written";
    private const string ErrValidation = "ValidationException";

    private static string OperandTypeMismatch(string op, string operandType) =>
        "Invalid KeyConditionExpression: Incorrect operand type for operator or function; " +
        $"operator or function: {op}, operand type: {operandType}";

    /// <summary>The AWS error (code, message) <paramref name="fn"/> raises. Raises when the call SUCCEEDS.</summary>
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

    /// <summary>
    /// A Query key-condition ordering comparison with the value written on the left
    /// (:v >= sk) reports an operand-type mismatch with the operator exactly as written in
    /// the expression, not its logical mirror (sk &lt;= :v reads back as '&lt;=', not '>=').
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-value-first-key-condition-operand-message-states-operator-as-written")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-qvf-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 60);
        foreach (var sk in new[] { "a", "b", "c" })
        {
            await ddb.PutItemAsync(table, Item(("pk", S("p")), ("sk", S(sk))));
        }

        Task<(string? Code, string? Message)> QueryError(string kce) => Error(() => ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = kce,
            ExpressionAttributeValues = Item((":p", S("p")), (":v", Bool(true))),
        }));

        // Each pair compares a value-first form against its logical mirror, key-first. A
        // BOOL operand is illegal for every one of these operators regardless of which side
        // it sits on, so the only thing that can differ between the two messages in a pair
        // is which operator token the parser reports — and it reports the one written.
        var pairs = new[]
        {
            ("pk = :p AND :v >= sk", ">=", "pk = :p AND sk <= :v", "<="),
            ("pk = :p AND :v <= sk", "<=", "pk = :p AND sk >= :v", ">="),
            ("pk = :p AND :v > sk", ">", "pk = :p AND sk < :v", "<"),
            ("pk = :p AND :v < sk", "<", "pk = :p AND sk > :v", ">"),
        };
        foreach (var (valueFirstKce, valueFirstOp, keyFirstKce, keyFirstOp) in pairs)
        {
            var (code, message) = await QueryError(valueFirstKce);
            Ensure(code == ErrValidation, $"{Py.Repr(valueFirstKce)}: expected {ErrValidation}, got {code} ({Py.Repr(message)})");
            var expected = OperandTypeMismatch(valueFirstOp, "BOOL");
            Ensure(message == expected, $"{Py.Repr(valueFirstKce)}: expected message {Py.Repr(expected)}, got {Py.Repr(message)}");
            var (code2, message2) = await QueryError(keyFirstKce);
            Ensure(code2 == ErrValidation, $"{Py.Repr(keyFirstKce)}: expected {ErrValidation}, got {code2} ({Py.Repr(message2)})");
            var expected2 = OperandTypeMismatch(keyFirstOp, "BOOL");
            Ensure(message2 == expected2, $"{Py.Repr(keyFirstKce)}: expected message {Py.Repr(expected2)}, got {Py.Repr(message2)}");
            Ensure(
                message != message2,
                $"value-first {Py.Repr(valueFirstKce)} and its mirror {Py.Repr(keyFirstKce)} reported the same message {Py.Repr(message)}; the operator token should differ between them");
        }

        // Positive control: the identical value-first form, correctly typed, still selects the
        // expected range after every rejection above.
        var control = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :p AND :lo <= sk",
            ConsistentRead = true,
            ExpressionAttributeValues = Item((":p", S("p")), (":lo", S("b"))),
        });
        var sks = control.Items.Select(it => it["sk"].S).Order(StringComparer.Ordinal).ToList();
        Ensure(sks.SequenceEqual(["b", "c"]), $"':lo <= sk' with lo='b' selected {Py.Repr(sks)}, expected ['b', 'c']");
    }
}
