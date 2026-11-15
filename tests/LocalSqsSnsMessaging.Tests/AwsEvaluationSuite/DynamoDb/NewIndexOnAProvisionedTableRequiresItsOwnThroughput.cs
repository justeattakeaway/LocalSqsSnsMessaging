// Translated from behavioral/dynamodb/check_new_index_on_a_provisioned_table_requires_its_own_throughput.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.new-index-on-a-provisioned-table-requires-its-own-throughput</summary>
[Category("AwsEvaluationSuite")]
public sealed class NewIndexOnAProvisionedTableRequiresItsOwnThroughput
{
    /// <summary>The Error dict from a call that MUST be refused.</summary>
    private static async Task<AwsError> Error(Func<Task> call, string what)
    {
        var error = await FidelityContext.CaptureError(call);
        return error ?? throw Fail($"{what}: the call was ACCEPTED; real AWS rejects it");
    }

    /// <summary>
    /// ProvisionedThroughput is required for a new index ONLY when the base table is provisioned --
    /// conditional requiredness, which a target treating the field as always-optional gets wrong.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.new-index-on-a-provisioned-table-requires-its-own-throughput")]
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
            BillingMode = BillingMode.PROVISIONED,
            ProvisionedThroughput = new ProvisionedThroughput { ReadCapacityUnits = 5, WriteCapacityUnits = 5 },
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        var err = await Error(
            () => ddb.UpdateTableAsync(new UpdateTableRequest
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
            }),
            "a new index on a provisioned table with no ProvisionedThroughput");
        Ensure(err.Code == "ValidationException", $"expected ValidationException, got {err.Code}");
        const string expected = "One or more parameter values were invalid: Both ReadCapacityUnits and WriteCapacityUnits must be specified for index: idx1";
        Ensure(err.Message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(err.Message)}");
        Ensure((await ddb.DescribeTableAsync(table)).Table.GlobalSecondaryIndexes is not { Count: > 0 }, "the refused create must add no index");
    }
}
