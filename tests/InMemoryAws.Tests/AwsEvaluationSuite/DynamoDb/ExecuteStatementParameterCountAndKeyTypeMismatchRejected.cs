// Translated from behavioral/dynamodb/check_execute_statement_parameter_count_and_key_type_mismatch_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.execute-statement-parameter-count-and-key-type-mismatch-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ExecuteStatementParameterCountAndKeyTypeMismatchRejected
{
    private const string CheckId = "execute-statement-parameter-count-and-key-type-mismatch-rejected";
    private const string ErrValidation = "ValidationException";

    /// <summary>
    /// ExecuteStatement rejects a Parameters list with fewer entries than the statement's '?'
    /// placeholders, and a parameter of the wrong type for a key, both as ValidationException
    /// with nothing written or changed; a matched, correctly typed call is unaffected.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_ExecuteStatement.html#DDB-ExecuteStatement-request-Parameters
    /// </summary>
    [Test]
    [DisplayName("dynamodb.execute-statement-parameter-count-and-key-type-mismatch-rejected")]
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
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 30);

        async Task<List<Dictionary<string, AttributeValue>>> Read(string pk) =>
            (await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
            {
                Statement = $"SELECT * FROM \"{t}\" WHERE pk = ?",
                Parameters = [S(pk)],
                ConsistentRead = true,
            })).Items;

        // Positive control: a Parameters list matching the placeholders, with the key's own type.
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"INSERT INTO \"{t}\" VALUE {{'pk':?, 'val':?}}",
            Parameters = [S("ctrl"), S("v0")],
        });
        var control = await Read("ctrl");
        Ensure(control.Count == 1, $"expected the control item to be written, got {Py.Repr(control)}");
        Ensure(Py.Equal(control[0]["val"], S("v0")), $"expected the control val, got {Py.Repr(control[0]["val"])}");

        // Fewer Parameters entries than the statement's placeholders.
        var (code, message) = await CodeAndMessage(() => ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"INSERT INTO \"{t}\" VALUE {{'pk':?, 'val':?}}",
            Parameters = [S("fewer")],
        }));
        Ensure(code == ErrValidation, $"fewer Parameters than placeholders must be {ErrValidation}, got {Py.Repr(code)}");
        Ensure(message == "Number of parameters in request and statement don't match.", $"expected AWS's exact message, got {Py.Repr(message)}");
        Ensure((await Read("fewer")).Count == 0, "a rejected INSERT must not have written an item");

        // A parameter of the wrong type for the key.
        (code, message) = await CodeAndMessage(() => ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"UPDATE \"{t}\" SET val = ? WHERE pk = ?",
            Parameters = [S("new"), N("5")],
        }));
        Ensure(code == ErrValidation, $"a wrong-typed key parameter must be {ErrValidation}, got {Py.Repr(code)}");
        Ensure(
            message == "Key attribute's data type should match its data type in table's schema: Key pk",
            $"expected AWS's exact message, got {Py.Repr(message)}");
        var unaffected = await Read("ctrl");
        Ensure(Py.Equal(unaffected[0]["val"], S("v0")), $"the control item must be unaffected by the rejected UPDATE, got {Py.Repr(unaffected[0]["val"])}");
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
