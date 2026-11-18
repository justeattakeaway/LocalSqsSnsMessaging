// Translated from behavioral/dynamodb/check_create_table_with_lsi.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.create-table-with-lsi</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateTableWithLsi
{
    /// <summary>
    /// CreateTable with an LSI on a base table that has a sort key is described back with
    /// the LSI's own KeySchema and Projection, not merely echoed by name.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.create-table-with-lsi")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var lsi = new List<LocalSecondaryIndex>
        {
            new()
            {
                IndexName = "lsi-created",
                KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("created_at", KeyType.RANGE)],
                Projection = new Projection { ProjectionType = ProjectionType.ALL },
            },
        };
        var table = FidelityContext.UniqueName("sim2real-behav-");
        var ks = new List<KeySchemaElement> { new("pk", KeyType.HASH) };
        var ad = new List<AttributeDefinition> { new("pk", ScalarAttributeType.S) };
        ks.Add(new KeySchemaElement("sk", KeyType.RANGE));
        ad.Add(new AttributeDefinition("sk", ScalarAttributeType.S));
        foreach (var ix in lsi)
        {
            foreach (var ke in ix.KeySchema)
            {
                if (!ad.Any(a => a.AttributeName == ke.AttributeName))
                {
                    ad.Add(new AttributeDefinition(ke.AttributeName, ScalarAttributeType.S));
                }
            }
        }

        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = ks,
            AttributeDefinitions = ad,
            BillingMode = BillingMode.PAY_PER_REQUEST,
            LocalSecondaryIndexes = lsi,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        var desc = (await ddb.DescribeTableAsync(table)).Table;
        var lsis = (desc.LocalSecondaryIndexes ?? []).ToDictionary(i => i.IndexName, StringComparer.Ordinal);
        Ensure(lsis.ContainsKey("lsi-created"), $"expected lsi-created in {Py.Repr(lsis.Keys.Order(StringComparer.Ordinal).ToList())}");
        var described = lsis["lsi-created"];
        Ensure(
            Py.Equal(KeySchema(described.KeySchema), KeySchema(lsi[0].KeySchema)),
            $"expected KeySchema {Py.Repr(KeySchema(lsi[0].KeySchema))}, got {Py.Repr(KeySchema(described.KeySchema))}");
        Ensure(
            described.Projection.ProjectionType == ProjectionType.ALL,
            $"expected ProjectionType ALL, got {described.Projection.ProjectionType?.Value}");
    }

    private static List<Dictionary<string, string>> KeySchema(List<KeySchemaElement>? elements) =>
        (elements ?? []).Select(e => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AttributeName"] = e.AttributeName,
            ["KeyType"] = e.KeyType?.Value ?? "",
        }).ToList();
}
