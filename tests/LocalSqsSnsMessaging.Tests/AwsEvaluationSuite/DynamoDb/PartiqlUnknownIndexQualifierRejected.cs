// Translated from behavioral/dynamodb/check_partiql_unknown_index_qualifier_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-unknown-index-qualifier-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlUnknownIndexQualifierRejected
{
    private static async Task<AwsError> ErrorCode(Func<Task> call, string what)
    {
        var error = await FidelityContext.CaptureError(call);
        return error ?? throw Fail($"{what}: the call was ACCEPTED; real AWS rejects it");
    }

    /// <summary>
    /// PartiQL SELECT naming an index the table does not have is refused with ValidationException,
    /// not answered as an empty result set. A FROM path of more than two components, and one with an
    /// empty component, are refused the same way before the index name is even looked up.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-unknown-index-qualifier-rejected")]
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
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("gpk", ScalarAttributeType.S),
                new AttributeDefinition("lsk", ScalarAttributeType.N),
            ],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = "gsi1",
                    KeySchema = [new KeySchemaElement("gpk", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = "lsi1",
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("lsk", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        var err = await ErrorCode(
            () => ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"SELECT * FROM \"{table}\".\"nope\" WHERE pk = 'a'" }),
            "a PartiQL statement naming a nonexistent index");
        Ensure(err.Code == "ValidationException", $"expected ValidationException, got {err.Code}");
        var expected = "The table does not have the specified index";
        Ensure(err.Message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(err.Message)}");

        err = await ErrorCode(
            () => ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"SELECT * FROM \"{table}\".\"gsi1\".\"extra\" WHERE gpk = 'g'" }),
            "a PartiQL FROM path of three components");
        Ensure(err.Code == "ValidationException", $"expected ValidationException, got {err.Code}");
        expected = "A path may contain at most 2 components in the FROM clause";
        Ensure(err.Message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(err.Message)}");

        err = await ErrorCode(
            () => ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"SELECT * FROM \"{table}\".\"\" WHERE pk = 'a'" }),
            "a PartiQL FROM path with an empty index component");
        Ensure(err.Code == "ValidationException", $"expected ValidationException, got {err.Code}");
        expected = "Path component cannot be an empty string";
        Ensure(err.Message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(err.Message)}");
    }
}
