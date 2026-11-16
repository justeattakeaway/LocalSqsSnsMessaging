// Translated from behavioral/dynamodb/check_on_demand_table_accepts_a_new_index_with_no_throughput.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.on-demand-table-accepts-a-new-index-with-no-throughput</summary>
[Category("AwsEvaluationSuite")]
public sealed class OnDemandTableAcceptsANewIndexWithNoThroughput
{
    private static readonly string[] Idx1 = ["idx1"];

    /// <summary>
    /// The other half of the same rule: on an on-demand table a new index needs NO throughput, and it
    /// inherits the base table's capacity mode.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.on-demand-table-accepts-a-new-index-with-no-throughput")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        await ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = table,
            AttributeDefinitions = [new AttributeDefinition("g1", ScalarAttributeType.S)],
            GlobalSecondaryIndexUpdates =
            [
                new GlobalSecondaryIndexUpdate
                {
                    Create = new CreateGlobalSecondaryIndexAction
                    {
                        IndexName = "idx1",
                        KeySchema = [new KeySchemaElement("g1", KeyType.HASH)],
                        Projection = new Projection { ProjectionType = ProjectionType.ALL },
                    },
                },
            ],
        });
        // Deliberately NOT waiting for ACTIVE: a real GSI backfill runs 5-15 minutes, past this
        // check's whole budget (measured: >240s). The claim under test is the state the create
        // ENTERS, observable at once -- and exactly what an engine with no in-progress state misses.
        var t = (await ddb.DescribeTableAsync(table)).Table;
        var idx = t.GlobalSecondaryIndexes ?? [];
        Ensure(idx.Select(i => i.IndexName).SequenceEqual(Idx1), $"expected index idx1, got {Py.Repr(idx.Select(i => i.IndexName).ToList())}");
        Ensure(idx[0].IndexStatus?.Value == "CREATING", $"a new index starts CREATING rather than appearing ACTIVE at once, got {idx[0].IndexStatus?.Value ?? "None"}");
        Ensure(
            t.BillingModeSummary.BillingMode == BillingMode.PAY_PER_REQUEST,
            $"the table must stay on-demand, got {Py.Repr(new Dictionary<string, object?> { ["BillingMode"] = t.BillingModeSummary?.BillingMode })}");
    }
}
