// Translated from behavioral/dynamodb/check_create_table_with_gsi.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.create-table-with-gsi</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateTableWithGsi
{
    private const string CheckId = "create-table-with-gsi";

    /// <summary>CreateTable with a GSI returns the index in DescribeTable.</summary>
    [Test]
    [DisplayName("dynamodb.create-table-with-gsi")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var gsi = new List<GlobalSecondaryIndex>
        {
            new()
            {
                IndexName = "gsi-email",
                KeySchema = [new KeySchemaElement("email", KeyType.HASH)],
                Projection = new Projection { ProjectionType = ProjectionType.ALL },
            },
        };
        var table = FidelityContext.UniqueName("sim2real-behav-");
        var ks = new List<KeySchemaElement> { new("pk", KeyType.HASH) };
        var ad = new List<AttributeDefinition> { new("pk", ScalarAttributeType.S) };
        foreach (var ix in gsi)
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
            GlobalSecondaryIndexes = gsi,
            Tags = [new Tag { Key = "sim2real-behav", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        var desc = (await ddb.DescribeTableAsync(table)).Table;
        var gsis = (desc.GlobalSecondaryIndexes ?? []).ToDictionary(g => g.IndexName, StringComparer.Ordinal);
        var names = gsis.Keys.Order(StringComparer.Ordinal).ToList();
        Ensure(names.SequenceEqual(["gsi-email"]), $"expected exactly ['gsi-email'], got {Py.Repr(names)}");
        Ensure(gsis["gsi-email"].IndexStatus == IndexStatus.ACTIVE, $"expected ACTIVE, got {gsis["gsi-email"].IndexStatus?.Value}");
        Ensure(
            gsis["gsi-email"].Projection.ProjectionType == ProjectionType.ALL,
            $"expected ALL, got {gsis["gsi-email"].Projection.ProjectionType?.Value}");
        var keySchema = KeySchema(gsis["gsi-email"].KeySchema);
        Ensure(
            Py.Equal(keySchema, KeySchema([new KeySchemaElement("email", KeyType.HASH)])),
            $"unexpected KeySchema {Py.Repr(keySchema)}");
    }

    private static List<Dictionary<string, string>> KeySchema(List<KeySchemaElement>? elements) =>
        (elements ?? []).Select(e => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AttributeName"] = e.AttributeName,
            ["KeyType"] = e.KeyType?.Value ?? "",
        }).ToList();
}
