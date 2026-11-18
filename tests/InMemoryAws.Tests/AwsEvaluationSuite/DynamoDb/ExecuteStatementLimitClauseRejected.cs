// Translated from behavioral/dynamodb/check_execute_statement_limit_clause_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.execute-statement-limit-clause-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ExecuteStatementLimitClauseRejected
{
    private const string CheckId = "execute-statement-limit-clause-rejected";

    /// <summary>
    /// ExecuteStatement rejects a PartiQL SELECT carrying a LIMIT clause, a SQL clause
    /// DynamoDB's PartiQL grammar does not support, naming the clause and its exact position.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.execute-statement-limit-clause-rejected")]
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
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(t, Item(("pk", S("item"))));

        // Positive control: the same SELECT, with no LIMIT clause, is well-formed PartiQL.
        var control = await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"SELECT * FROM \"{t}\"" });
        var controlPks = control.Items.Select(i => i["pk"].S).ToList();
        Ensure(
            controlPks.SequenceEqual(["item"]),
            $"the unlimited SELECT returned {Py.Repr(control.Items)}, expected the one seeded item");

        var statement = $"SELECT * FROM \"{t}\" LIMIT 1";
        var offset = statement.IndexOf("LIMIT", StringComparison.Ordinal);
        var expectedMessage = $"Unsupported clause: LIMIT at 1:{offset + 7}:1";
        var (code, message) = await Error(() => ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = statement }));
        Ensure(code == "ValidationException", $"expected ValidationException, got {Py.Repr(code)}");
        Ensure(message == expectedMessage, $"expected {Py.Repr(expectedMessage)}, got {Py.Repr(message)}");
    }

    /// <summary>(Error.Code, Error.Message) of the ClientError fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
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
