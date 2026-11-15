// Translated from behavioral/dynamodb/check_table_with_sort_key.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.table-with-sort-key</summary>
[Category("AwsEvaluationSuite")]
public sealed class TableWithSortKey
{
    private const string CheckId = "table-with-sort-key";

    /// <summary>CreateTable with composite key (hash + range) is described correctly.</summary>
    [Test]
    [DisplayName("dynamodb.table-with-sort-key")]
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
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        var desc = (await ddb.DescribeTableAsync(table)).Table;
        var keySchema = desc.KeySchema.ToDictionary(ks => ks.AttributeName, ks => ks.KeyType.Value, StringComparer.Ordinal);
        var expected = new Dictionary<string, string>(StringComparer.Ordinal) { ["pk"] = "HASH", ["sk"] = "RANGE" };
        Ensure(Py.Equal(keySchema, expected), $"expected composite key schema pk=HASH/sk=RANGE, got {Py.Repr(keySchema)}");
    }
}
