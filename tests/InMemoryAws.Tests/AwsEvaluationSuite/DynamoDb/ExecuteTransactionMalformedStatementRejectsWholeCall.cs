// Translated from behavioral/dynamodb/check_execute_transaction_malformed_statement_rejects_whole_call.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.execute-transaction-malformed-statement-rejects-whole-call</summary>
[Category("AwsEvaluationSuite")]
public sealed class ExecuteTransactionMalformedStatementRejectsWholeCall
{
    private const string ErrValidation = "ValidationException";
    private const string ErrTransactionCanceled = "TransactionCanceledException";

    /// <summary>
    /// ExecuteTransaction with one syntactically malformed statement rejects the whole call
    /// with ValidationException, never TransactionCanceledException, and no statement in the
    /// transaction — malformed or well-formed — takes effect.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_ExecuteTransaction.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.execute-transaction-malformed-statement-rejects-whole-call")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableName = FidelityContext.UniqueName("sim2real-behav-ddb-etxn-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags =
            [
                new Tag { Key = "sim2real:managed", Value = "true" },
                new Tag { Key = "sim2real:prefix", Value = "sim2real-behav" },
            ],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(tableName));
        await WaitForTableActive(ddb, tableName);

        var wellFormed = $"INSERT INTO \"{tableName}\" VALUE {{'pk':'well-formed','sk':'s1'}}";
        var malformed = $"INSRT INTO \"{tableName}\" VALUE {{'pk':'malformed','sk':'s1'}}";

        var (code, message) = await ErrorCodeAndMessage(() => ddb.ExecuteTransactionAsync(new ExecuteTransactionRequest
        {
            TransactStatements = [new ParameterizedStatement { Statement = malformed }, new ParameterizedStatement { Statement = wellFormed }],
        }));
        Ensure(code == ErrValidation, $"expected {ErrValidation}, got {Py.Repr(code)} (never {ErrTransactionCanceled})");
        const string expectedMessage =
            "Validation failed in TransactStatements[0]: "
            + "Statement wasn't well formed, can't be processed: Unexpected token after expression";
        Ensure(message == expectedMessage, $"expected message {Py.Repr(expectedMessage)}, got {Py.Repr(message)}");

        var unwrittenMalformed = await ddb.GetItemAsync(new GetItemRequest { TableName = tableName, Key = Item(("pk", S("malformed")), ("sk", S("s1"))), ConsistentRead = true });
        Ensure(unwrittenMalformed.Item is null, $"malformed statement must not have written, got {Py.Repr(unwrittenMalformed.Item)}");
        var unwrittenSibling = await ddb.GetItemAsync(new GetItemRequest { TableName = tableName, Key = Item(("pk", S("well-formed")), ("sk", S("s1"))), ConsistentRead = true });
        Ensure(unwrittenSibling.Item is null, $"the well-formed sibling must not have taken effect either, got {Py.Repr(unwrittenSibling.Item)}");

        // Positive control: the identical well-formed statement alone succeeds, proving the
        // rejection above is attributable to the malformed statement, not a broken table or API.
        await ddb.ExecuteTransactionAsync(new ExecuteTransactionRequest { TransactStatements = [new ParameterizedStatement { Statement = wellFormed }] });
        var written = await ddb.GetItemAsync(new GetItemRequest { TableName = tableName, Key = Item(("pk", S("well-formed")), ("sk", S("s1"))), ConsistentRead = true });
        Ensure(written.Item is not null, $"expected the positive control to have written, got {Py.Repr(written.Item)}");
    }

    /// <summary>(Error.Code, Error.Message) of the ClientError fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> ErrorCodeAndMessage<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected ExecuteTransaction to be rejected, but it SUCCEEDED with {Py.Repr(result)}");
        }

        return (error.Code, error.Message);
    }

    private static async Task<string?> WaitForTableActive(IAmazonDynamoDB ddb, string tableName, double timeoutSeconds = 120.0, double intervalSeconds = 2.0)
    {
        var clock = FidelityTime.StartNew();
        string? status = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            status = (await ddb.DescribeTableAsync(tableName)).Table.TableStatus?.Value;
            if (status == "ACTIVE")
            {
                return status;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"table {tableName} never went ACTIVE in {timeoutSeconds}s (last={status})");
    }
}
