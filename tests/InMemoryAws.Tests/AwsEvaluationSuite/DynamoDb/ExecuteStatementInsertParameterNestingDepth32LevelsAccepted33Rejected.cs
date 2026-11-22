// Translated from behavioral/dynamodb/check_execute_statement_insert_parameter_nesting_depth_32_levels_accepted_33_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.execute-statement-insert-parameter-nesting-depth-32-levels-accepted-33-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ExecuteStatementInsertParameterNestingDepth32LevelsAccepted33Rejected
{
    private const string CheckId = "execute-statement-insert-parameter-nesting-depth-32-levels-accepted-33-rejected";
    private const string ErrValidation = "ValidationException";

    /// <summary>A chain of <paramref name="levels"/> maps around one scalar leaf, <c>levels + 1</c> levels deep in total.</summary>
    private static AttributeValue Nest(int levels)
    {
        var value = S("leaf");
        for (var i = 0; i < levels; i++)
        {
            value = M(("k", value));
        }

        return value;
    }

    /// <summary>The error code and message <paramref name="fn"/> raises; an unexpected success raises too.</summary>
    private static async Task<(string? Code, string? Message)> CodeAndMessage<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
    }

    /// <summary>
    /// ExecuteStatement's INSERT INTO ... VALUE accepts a 32-level nested Map parameter and
    /// rejects a 33-level one with ValidationException, the same depth limit PutItem enforces, and
    /// the rejected row is not written.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Limits.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.execute-statement-insert-parameter-nesting-depth-32-levels-accepted-33-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-pqlnest-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(async () =>
        {
            try
            {
                await ddb.DeleteTableAsync(table);
            }
            catch (ResourceNotFoundException)
            {
            }
        });
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 60);

        async Task<List<Dictionary<string, AttributeValue>>> Read(string pk) =>
            (await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
            {
                Statement = $"SELECT * FROM \"{table}\" WHERE pk = ?",
                Parameters = [S(pk)],
                ConsistentRead = true,
            })).Items;

        // Positive control: 31 wrapping maps + the leaf = 32 levels, the deepest accepted document,
        // bound through a PartiQL INSERT's Parameters list.
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"INSERT INTO \"{table}\" VALUE {{'pk':?, 'v':?}}",
            Parameters = [S("deepest"), Nest(31)],
        });
        var got = await Read("deepest");
        Ensure(got.Count == 1, $"the 32-level control row must be written, got {Py.Repr(got)}");
        AttributeValue? cursor = got[0]["v"];
        for (var level = 0; level < 31; level++)
        {
            Ensure(cursor is not null && cursor.M is not null, $"stored document must preserve all 31 map levels; level {level} was {Py.Repr(cursor)}");
            cursor = cursor.M.GetValueOrDefault("k");
        }

        Ensure(Py.Equal(cursor, S("leaf")), $"the leaf at depth 32 must round-trip, got {Py.Repr(cursor)}");

        // One level deeper is 33 and must be refused, with nothing written.
        var (code, message) = await CodeAndMessage(() => ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"INSERT INTO \"{table}\" VALUE {{'pk':?, 'v':?}}",
            Parameters = [S("too-deep"), Nest(32)],
        }));
        Ensure(code == ErrValidation, $"a 33-level INSERT parameter must be {ErrValidation}, got {Py.Repr(code)}");
        Ensure(message == "Nesting Levels have exceeded supported limits", $"expected AWS's exact ValidationException message, got {Py.Repr(message)}");
        Ensure((await Read("too-deep")).Count == 0, "a row rejected for nesting depth must not be stored");
    }
}
