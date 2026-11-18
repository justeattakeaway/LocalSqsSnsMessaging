// Translated from behavioral/dynamodb/check_execute_statement_extra_token_after_complete_statement_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.execute-statement-extra-token-after-complete-statement-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ExecuteStatementExtraTokenAfterCompleteStatementRejected
{
    private const string ExtraTokenMessage = "Statement wasn't well formed, can't be processed: Unexpected token after expression";

    /// <summary>
    /// ExecuteStatement rejects SELECT, INSERT, UPDATE and DELETE each carrying one extra
    /// token after an otherwise-complete statement, with ValidationException, and writes nothing.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_ExecuteStatement.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.execute-statement-extra-token-after-complete-statement-rejected")]
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
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(tableName));
        await WaitActive(ddb, tableName);

        // Positive control: a well-formed INSERT with no trailing token succeeds.
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"INSERT INTO \"{tableName}\" VALUE {{'pk': 'seed', 'n': 1}}" });
        var seed = await ItemsAt(ddb, tableName, "seed");
        Ensure(seed.Count > 0 && Py.Equal(seed[0]["n"], N("1")), $"seed item missing or wrong after a well-formed INSERT: {Py.Repr(seed)}");

        // SELECT: a trailing token is rejected.
        await AssertRejected(ddb, $"SELECT * FROM \"{tableName}\" WHERE pk='seed' EXTRA");

        // INSERT: the attempted new item never appears.
        await AssertRejected(ddb, $"INSERT INTO \"{tableName}\" VALUE {{'pk': 'new'}} EXTRA");
        Ensure((await ItemsAt(ddb, tableName, "new")).Count == 0, "a rejected INSERT with a trailing token still wrote an item");

        // UPDATE: the seed item's attribute stays at its pre-update value.
        await AssertRejected(ddb, $"UPDATE \"{tableName}\" SET n = 2 WHERE pk='seed' EXTRA");
        seed = await ItemsAt(ddb, tableName, "seed");
        Ensure(Py.Equal(seed[0]["n"], N("1")), $"a rejected UPDATE with a trailing token changed n to {Py.Repr(seed[0].GetValueOrDefault("n"))}");

        // DELETE: the seed item survives.
        await AssertRejected(ddb, $"DELETE FROM \"{tableName}\" WHERE pk='seed' EXTRA");
        Ensure((await ItemsAt(ddb, tableName, "seed")).Count > 0, "a rejected DELETE with a trailing token removed the item");
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

    /// <summary>Poll DescribeTable until ACTIVE.</summary>
    private static async Task<string?> WaitActive(IAmazonDynamoDB ddb, string tableName, double timeoutSeconds = 90.0, double intervalSeconds = 1.0)
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

    private static async Task<List<Dictionary<string, AttributeValue>>> ItemsAt(IAmazonDynamoDB ddb, string tableName, string pk) =>
        (await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"SELECT * FROM \"{tableName}\" WHERE pk='{pk}'" })).Items;

    private static async Task AssertRejected(IAmazonDynamoDB ddb, string statement)
    {
        var (code, message) = await Error(() => ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = statement }));
        Ensure(code == "ValidationException", $"expected ValidationException, got {code}: {Py.Repr(message)}");
        Ensure(message == ExtraTokenMessage, $"unexpected message {Py.Repr(message)}");
    }
}
