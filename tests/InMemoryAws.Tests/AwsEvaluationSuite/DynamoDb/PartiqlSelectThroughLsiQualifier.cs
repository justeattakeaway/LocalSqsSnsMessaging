// Translated from behavioral/dynamodb/check_partiql_select_through_lsi_qualifier.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-select-through-lsi-qualifier</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlSelectThroughLsiQualifier
{
    private static async Task<AwsError> ErrorCode(Func<Task> call, string what)
    {
        var error = await FidelityContext.CaptureError(call);
        return error ?? throw Fail($"{what}: the call was ACCEPTED; real AWS rejects it");
    }

    /// <summary>
    /// PartiQL SELECT through an LSI qualifier honours the index projection: a KEYS_ONLY LSI
    /// returns only the table and index keys, and a WHERE on an attribute the index does not
    /// project is refused rather than silently filtered.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-select-through-lsi-qualifier")]
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
        await ddb.PutItemAsync(table, Item(("pk", S("a")), ("sk", S("1")), ("gpk", S("g1")), ("lsk", N("5")), ("extra", S("e"))));
        var res = await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"SELECT * FROM \"{table}\".\"lsi1\" WHERE pk = 'a'" });
        var items = res.Items ?? [];
        Ensure(items.Count == 1, $"expected one item through the LSI, got {items.Count}");
        var got = items[0].Keys.Order(StringComparer.Ordinal).ToList();
        Ensure(got.SequenceEqual(["lsk", "pk", "sk"]), $"KEYS_ONLY must project only the keys, got {Py.Repr(got)}");

        var err = await ErrorCode(
            () => ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"SELECT * FROM \"{table}\".\"lsi1\" WHERE pk = 'a' AND extra = 'e'" }),
            "a PartiQL WHERE filtering on an attribute the KEYS_ONLY LSI does not project");
        Ensure(err.Code == "ValidationException", $"expected ValidationException, got {err.Code}");
        const string expected = "One or more parameter values were invalid: Secondary index lsi1 does not project one or more filter attributes: [extra]";
        Ensure(err.Message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(err.Message)}");
    }
}
