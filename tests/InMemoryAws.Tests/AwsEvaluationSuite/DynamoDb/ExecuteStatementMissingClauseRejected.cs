// Translated from behavioral/dynamodb/check_execute_statement_missing_clause_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.execute-statement-missing-clause-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ExecuteStatementMissingClauseRejected
{
    private const string SelectNoFromMessage = "Statement wasn't well formed, can't be processed: Expected FROM after SELECT list";
    private const string UpdateNoSetRemoveMessage = "Statement wasn't well formed, can't be processed: Expected data manipulation";
    private const string InsertNoValueMessage = "Statement wasn't well formed, can't be processed: Unexpected term";
    private const string DeleteNoWhereMessage = "Where clause does not contain a mandatory equality on all key attributes";
    private const string NonDmlLeadingMessage = "Unsupported operation: DropTable";

    /// <summary>
    /// ExecuteStatement rejects SELECT without FROM, UPDATE without SET/REMOVE, INSERT
    /// without VALUE, DELETE without WHERE and a non-DML leading keyword, and writes nothing.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ql-reference.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.execute-statement-missing-clause-rejected")]
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

        // Positive control / seed: a well-formed INSERT succeeds.
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"INSERT INTO \"{tableName}\" VALUE {{'pk': 'seed', 'n': 1}}" });
        var seed = await ItemsAt(ddb, tableName, "seed");
        Ensure(seed.Count > 0 && Py.Equal(seed[0]["n"], N("1")), $"seed item missing or wrong after a well-formed INSERT: {Py.Repr(seed)}");

        // SELECT without FROM.
        await AssertRejected(ddb, "SELECT *", SelectNoFromMessage);

        // UPDATE without SET or REMOVE.
        await AssertRejected(ddb, $"UPDATE \"{tableName}\" WHERE pk='seed'", UpdateNoSetRemoveMessage);
        seed = await ItemsAt(ddb, tableName, "seed");
        Ensure(Py.Equal(seed[0]["n"], N("1")), $"a rejected UPDATE without SET/REMOVE changed n to {Py.Repr(seed[0].GetValueOrDefault("n"))}");

        // INSERT without VALUE.
        await AssertRejected(ddb, $"INSERT INTO \"{tableName}\"", InsertNoValueMessage);

        // DELETE without WHERE.
        await AssertRejected(ddb, $"DELETE FROM \"{tableName}\"", DeleteNoWhereMessage);
        Ensure((await ItemsAt(ddb, tableName, "seed")).Count > 0, "a rejected DELETE without WHERE removed the seed item");

        // A non-DML leading keyword.
        await AssertRejected(ddb, $"DROP TABLE \"{tableName}\"", NonDmlLeadingMessage);
        var status = (await ddb.DescribeTableAsync(tableName)).Table.TableStatus?.Value;
        Ensure(status == "ACTIVE", $"table status {Py.Repr(status)} after a rejected DROP TABLE");
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

    private static async Task AssertRejected(IAmazonDynamoDB ddb, string statement, string expectedMessage)
    {
        var (code, message) = await Error(() => ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = statement }));
        Ensure(code == "ValidationException", $"expected ValidationException, got {code}: {Py.Repr(message)}");
        Ensure(message == expectedMessage, $"unexpected message {Py.Repr(message)}");
    }
}
