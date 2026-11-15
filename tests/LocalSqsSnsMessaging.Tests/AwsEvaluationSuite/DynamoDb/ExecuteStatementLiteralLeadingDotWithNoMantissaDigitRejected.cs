// Translated from behavioral/dynamodb/check_execute_statement_literal_leading_dot_with_no_mantissa_digit_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.execute-statement-literal-leading-dot-with-no-mantissa-digit-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ExecuteStatementLiteralLeadingDotWithNoMantissaDigitRejected
{
    private const string CheckId = "execute-statement-literal-leading-dot-with-no-mantissa-digit-rejected";
    private const string ErrValidation = "ValidationException";
    private const string UnexpectedTermMessage = "Statement wasn't well formed, can't be processed: Unexpected term";

    /// <summary>
    /// A leading-dot number literal with no mantissa digit at all, just a dot immediately
    /// followed by an exponent marker, is rejected, while a leading-dot literal that carries a
    /// mantissa digit is accepted and canonicalized.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ql-reference.insert.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.execute-statement-literal-leading-dot-with-no-mantissa-digit-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableName = FidelityContext.UniqueName("sim2real-behav-ddb-tbl-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(tableName));
        await ddb.WaitForTableExists(tableName, delaySeconds: 2, maxAttempts: 30);

        // Positive control: a leading-dot literal that carries a mantissa digit is accepted and
        // canonicalized with the implied leading zero filled in.
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"INSERT INTO \"{tableName}\" VALUE {{'pk': 'ctrl', 'n': .5}}" });
        var control = (await ddb.GetItemAsync(new GetItemRequest { TableName = tableName, Key = Item(("pk", S("ctrl"))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(control["n"], N("0.5")), $"literal .5 must store as canonical N 0.5, got {Py.Repr(control["n"])}");

        // A leading dot with no digit anywhere in the mantissa is rejected at the lexical level.
        var (code, message) = await Error(() => ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"INSERT INTO \"{tableName}\" VALUE {{'pk': 'bad', 'n': .e5}}",
        }));
        Ensure(code == ErrValidation, $"a mantissa-less leading dot must be {ErrValidation}, got {Py.Repr(code)}");
        Ensure(message == UnexpectedTermMessage, $"expected AWS's exact message, got {Py.Repr(message)}");
        var leaked = await ddb.GetItemAsync(new GetItemRequest { TableName = tableName, Key = Item(("pk", S("bad"))), ConsistentRead = true });
        Ensure(leaked.Item is null, $"the rejected INSERT must not have written an item, found {Py.Repr(leaked.Item)}");
    }

    /// <summary>(Error.Code, Error.Message) of the ClientError fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected ExecuteStatement to be rejected, but it succeeded: {Py.Repr(result)}");
        }

        return (error.Code, error.Message);
    }
}
