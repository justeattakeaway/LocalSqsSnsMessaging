// Translated from behavioral/dynamodb/check_execute_statement_number_precision_in_update_set_and_key_equality.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.execute-statement-number-precision-in-update-set-and-key-equality</summary>
[Category("AwsEvaluationSuite")]
public sealed class ExecuteStatementNumberPrecisionInUpdateSetAndKeyEquality
{
    private const string CheckId = "execute-statement-number-precision-in-update-set-and-key-equality";
    private const string ErrValidation = "ValidationException";
    private const string OverflowMessage = "Attempting to store more than 38 significant digits in a Number";

    /// <summary>
    /// ExecuteStatement enforces the 38-significant-digit N limit for a value assigned in
    /// UPDATE SET and for one compared in a key equality; a 38-digit N round-trips exactly
    /// through both positions.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/HowItWorks.NamingRulesDataTypes.html#HowItWorks.DataTypes.Number
    /// </summary>
    [Test]
    [DisplayName("dynamodb.execute-statement-number-precision-in-update-set-and-key-equality")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.N)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 30);

        var at38 = new string('9', 38);
        var over39 = new string('9', 39);

        async Task<Dictionary<string, AttributeValue>> ReadControl()
        {
            var items = (await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
            {
                Statement = $"SELECT * FROM \"{t}\" WHERE pk = ?",
                Parameters = [N(at38)],
                ConsistentRead = true,
            })).Items;
            Ensure(items.Count == 1, $"expected exactly the control item, got {Py.Repr(items)}");
            return items[0];
        }

        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"INSERT INTO \"{t}\" VALUE {{'pk':?, 'counter':?}}",
            Parameters = [N(at38), N("0")],
        });
        var control = await ReadControl();
        Ensure(Py.Equal(control["pk"], N(at38)), $"a 38-digit N key must round-trip digit-for-digit, got {Py.Repr(control["pk"])}");
        Ensure(Py.Equal(control["counter"], N("0")), $"expected the control counter to be 0, got {Py.Repr(control["counter"])}");

        // A 39-digit value assigned in UPDATE SET is refused and leaves the attribute untouched.
        var (code, message) = await CodeAndMessage(() => ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"UPDATE \"{t}\" SET counter = ? WHERE pk = ?",
            Parameters = [N(over39), N(at38)],
        }));
        Ensure(code == ErrValidation, $"a 39-digit SET value must be {ErrValidation}, got {Py.Repr(code)}");
        Ensure(message == OverflowMessage, $"expected AWS's exact message, got {Py.Repr(message)}");
        var unaffected = await ReadControl();
        Ensure(Py.Equal(unaffected["counter"], N("0")), $"a rejected SET value must leave the attribute untouched, got {Py.Repr(unaffected["counter"])}");

        // A 39-digit value compared in the key equality is refused the same way, and writes nothing.
        (code, message) = await CodeAndMessage(() => ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"UPDATE \"{t}\" SET counter = ? WHERE pk = ?",
            Parameters = [N("1"), N(over39)],
        }));
        Ensure(code == ErrValidation, $"a 39-digit key equality value must be {ErrValidation}, got {Py.Repr(code)}");
        Ensure(message == OverflowMessage, $"expected AWS's exact message, got {Py.Repr(message)}");
        var stillUnaffected = await ReadControl();
        Ensure(Py.Equal(stillUnaffected["counter"], N("0")), $"a rejected key equality must change nothing, got {Py.Repr(stillUnaffected["counter"])}");

        // The 38-digit control value round-trips exactly through both the SET and the key equality.
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"UPDATE \"{t}\" SET counter = ? WHERE pk = ?",
            Parameters = [N(at38), N(at38)],
        });
        var rt = await ReadControl();
        Ensure(Py.Equal(rt["counter"], N(at38)), $"a 38-digit SET value must round-trip digit-for-digit, got {Py.Repr(rt["counter"])}");
    }

    /// <summary>The botocore error code and message <paramref name="fn"/> raises; an unexpected success raises too.</summary>
    private static async Task<(string? Code, string? Message)> CodeAndMessage<T>(Func<Task<T>> fn)
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
