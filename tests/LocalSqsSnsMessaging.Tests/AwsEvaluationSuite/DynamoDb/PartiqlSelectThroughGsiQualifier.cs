// Translated from behavioral/dynamodb/check_partiql_select_through_gsi_qualifier.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-select-through-gsi-qualifier</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlSelectThroughGsiQualifier
{
    /// <summary>
    /// PartiQL SELECT addressing a GSI as "table"."index" reads through the index and returns the
    /// projected item, not a table scan.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-select-through-gsi-qualifier")]
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
        var res = await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"SELECT * FROM \"{table}\".\"gsi1\" WHERE gpk = 'g1'" });
        var items = res.Items ?? [];
        Ensure(items.Count == 1, $"expected exactly the one indexed item, got {items.Count}");
        Ensure(Py.Equal(items[0]["pk"], S("a")), $"expected pk=a, got {Py.Repr(items[0].GetValueOrDefault("pk"))}");
        Ensure(Py.Equal(items[0].GetValueOrDefault("extra"), S("e")), "an ALL-projection GSI must return the non-key attribute");
    }
}
