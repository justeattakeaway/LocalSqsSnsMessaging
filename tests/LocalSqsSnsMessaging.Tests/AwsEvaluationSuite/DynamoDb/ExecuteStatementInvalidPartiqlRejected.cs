// Translated from behavioral/dynamodb/check_execute_statement_invalid_partiql_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.execute-statement-invalid-partiql-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ExecuteStatementInvalidPartiqlRejected
{
    /// <summary>ExecuteStatement MUST reject syntactically invalid PartiQL with ValidationException.</summary>
    [Test]
    [DisplayName("dynamodb.execute-statement-invalid-partiql-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableName = await CreateSimpleTable(ddb, ctx);

        // Positive control: well-formed PartiQL against the same table succeeds, so a rejection
        // below is attributable to the malformed syntax rather than ExecuteStatement being broken
        // or blocked outright.
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"SELECT * FROM \"{tableName}\"" });

        var (code, message) = await ErrorCodeAndMessage(() => ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"SELCT * FROM \"{tableName}\"" }));
        Ensure(code == "ValidationException", $"expected ValidationException for malformed PartiQL, got {Py.Repr(code)}");
        const string expectedMessage = "Statement wasn't well formed, can't be processed: Expected data manipulation";
        Ensure(message == expectedMessage, $"expected message {Py.Repr(expectedMessage)}, got {Py.Repr(message)}");

        // Each further statement breaks the grammar a different way; AWS's parser names the break
        // in its message, so a different mistake must produce a different exact message.
        (string Statement, string Expected)[] otherMalformedStatements =
        [
            ($"SELECT * FROM \"{tableName}\" FOO", "Aliasing is not supported"),
            ($"SELECT * FROM \"{tableName}\" WHERE pk = 'abc", "Statement wasn't well formed, can't be processed: "),
            ("SELECT *", "Statement wasn't well formed, can't be processed: Expected FROM after SELECT list"),
            (
                $"SELECT * FROM \"{tableName}\";;",
                "Statement wasn't well formed, can't be processed: Unexpected token after semicolon. "
                + "(Only one query is allowed.)"
            ),
            ($"SELECT * FROM \"{tableName}\" WHERE", "Statement wasn't well formed, can't be processed: Unexpected term"),
        ];
        foreach (var (statement, expected) in otherMalformedStatements)
        {
            (code, message) = await ErrorCodeAndMessage(() => ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = statement }));
            Ensure(code == "ValidationException", $"expected ValidationException for {Py.Repr(statement)}, got {Py.Repr(code)}");
            Ensure(message == expected, $"for {Py.Repr(statement)}: expected message {Py.Repr(expected)}, got {Py.Repr(message)}");
        }
    }

    /// <summary>
    /// Call fn(); return the botocore Error.Code and Error.Message if a ClientError is raised. An
    /// unexpected SUCCESS raises, because an emulator ACCEPTING syntax real AWS refuses is the
    /// strongest signal available.
    /// </summary>
    private static async Task<(string? Code, string? Message)> ErrorCodeAndMessage<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected ExecuteStatement to be rejected, but it SUCCEEDED with {Py.Repr(result)}");
        }

        return (error.Code, error.Message);
    }

    /// <summary>
    /// Poll DescribeTable until ACTIVE. CreateTable is eventually consistent, so a
    /// transient ResourceNotFoundException counts as "not yet", never a failure.
    /// </summary>
    private static async Task<string?> WaitForTableActive(IAmazonDynamoDB ddb, string tableName, double timeoutSeconds = 90.0, double intervalSeconds = 1.0)
    {
        var clock = FidelityTime.StartNew();
        string? status = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            var (resp, error) = await FidelityContext.Capture(() => ddb.DescribeTableAsync(tableName));
            if (error is not null)
            {
                if (error.Code != "ResourceNotFoundException")
                {
                    throw error.Exception;
                }

                status = "PENDING";
            }
            else
            {
                status = resp!.Table.TableStatus?.Value;
            }

            if (status == "ACTIVE")
            {
                return status;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"table {tableName} never went ACTIVE in {timeoutSeconds}s (last={status})");
    }

    /// <summary>Create a pk (S) + sk (S) composite-key table, register teardown, wait ACTIVE.</summary>
    private static async Task<string> CreateSimpleTable(IAmazonDynamoDB ddb, FidelityContext ctx)
    {
        var tableName = FidelityContext.UniqueName("sim2real-behav-ddb-tbl-");
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
        return tableName;
    }
}
