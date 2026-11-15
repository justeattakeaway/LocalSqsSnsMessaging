// Translated from behavioral/dynamodb/check_keys_only_and_all_projections_are_exempt_from_the_attribute_budget.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.keys-only-and-all-projections-are-exempt-from-the-attribute-budget</summary>
[Category("AwsEvaluationSuite")]
public sealed class KeysOnlyAndAllProjectionsAreExemptFromTheAttributeBudget
{
    /// <summary>Poll DescribeTable until the table is ACTIVE with no index still mutating.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, string table, int deadlineSeconds = 240)
    {
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(deadlineSeconds))
        {
            var t = (await ddb.DescribeTableAsync(table)).Table;
            var busy = (t.GlobalSecondaryIndexes ?? []).Where(i => i.IndexStatus != IndexStatus.ACTIVE).ToList();
            if (t.TableStatus == TableStatus.ACTIVE && busy.Count == 0)
            {
                return t;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }

        throw Fail($"{table} never settled to ACTIVE within {deadlineSeconds}s");
    }

    /// <summary>
    /// The 100-attribute budget counts only user-specified INCLUDE attributes, so any number of
    /// KEYS_ONLY and ALL indexes is accepted -- the exemption, asserted positively.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.keys-only-and-all-projections-are-exempt-from-the-attribute-budget")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        var ads = new List<AttributeDefinition> { new("pk", ScalarAttributeType.S) };
        var gsis = new List<GlobalSecondaryIndex>();
        for (var i = 0; i < 6; i++)
        {
            ads.Add(new AttributeDefinition($"g{i}", ScalarAttributeType.S));
            gsis.Add(new GlobalSecondaryIndex
            {
                IndexName = $"idx{i}",
                KeySchema = [new KeySchemaElement($"g{i}", KeyType.HASH)],
                Projection = new Projection { ProjectionType = i % 2 == 1 ? ProjectionType.ALL : ProjectionType.KEYS_ONLY },
            });
        }

        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = ads,
            GlobalSecondaryIndexes = gsis,
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        var t = await WaitActive(ddb, table);
        var got = (t.GlobalSecondaryIndexes ?? []).Select(i => i.IndexName).Order(StringComparer.Ordinal).ToList();
        Ensure(got.SequenceEqual(Enumerable.Range(0, 6).Select(i => $"idx{i}")), $"expected all six indexes, got {Py.Repr(got)}");
    }
}
