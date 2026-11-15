// Translated from behavioral/dynamodb/check_execute_transaction_mixed_read_and_write_statements_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.execute-transaction-mixed-read-and-write-statements-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ExecuteTransactionMixedReadAndWriteStatementsRejected
{
    private const string CheckId = "execute-transaction-mixed-read-and-write-statements-rejected";

    /// <summary>
    /// An ExecuteTransaction mixing a read statement (SELECT) with a write statement (UPDATE) is
    /// rejected before either statement executes; the item the UPDATE targeted is left untouched.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.execute-transaction-mixed-read-and-write-statements-rejected")]
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
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 30);

        await ddb.PutItemAsync(table, Item(("pk", S("p1")), ("sk", S("s1")), ("data", S("original"))));

        var (code, message) = await Error(() => ddb.ExecuteTransactionAsync(new ExecuteTransactionRequest
        {
            TransactStatements =
            [
                new ParameterizedStatement { Statement = $"SELECT * FROM \"{table}\" WHERE pk=? AND sk=?", Parameters = [S("p1"), S("s1")] },
                new ParameterizedStatement { Statement = $"UPDATE \"{table}\" SET data=? WHERE pk=? AND sk=?", Parameters = [S("changed"), S("p1"), S("s1")] },
            ],
        }));
        Ensure(code == "ValidationException", $"expected ValidationException, got {Py.Repr(code)}");
        const string expectedMessage = "ExecuteTransaction API does not support both read and write operations in the same request.";
        Ensure(message == expectedMessage, $"unexpected message: {Py.Repr(message)}");

        // Positive control: the targeted item is unchanged after the rejected call.
        var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("p1")), ("sk", S("s1"))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(item["data"], S("original")), $"a rejected mixed transaction must not apply its UPDATE: {Py.Repr(item)}");
    }

    /// <summary>(Error.Code, Error.Message) of the ClientError fn() raises; a success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be rejected, but it succeeded: {Py.Repr(result)}");
        }

        return (error.Code, error.Message);
    }
}
