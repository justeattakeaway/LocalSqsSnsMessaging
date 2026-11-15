// Translated from behavioral/dynamodb/check_update_table_creates_at_most_one_index_per_call.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-table-creates-at-most-one-index-per-call</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateTableCreatesAtMostOneIndexPerCall
{
    /// <summary>
    /// Only one secondary index may be created per UpdateTable call, so a request carrying two Create
    /// actions is refused outright and neither index appears.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-table-creates-at-most-one-index-per-call")]
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
        var err = await Error(
            () => ddb.UpdateTableAsync(new UpdateTableRequest
            {
                TableName = table,
                AttributeDefinitions =
                [
                    new AttributeDefinition("g1", ScalarAttributeType.S),
                    new AttributeDefinition("g2", ScalarAttributeType.S),
                ],
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
                    new GlobalSecondaryIndexUpdate
                    {
                        Create = new CreateGlobalSecondaryIndexAction
                        {
                            IndexName = "idx2",
                            KeySchema = [new KeySchemaElement("g2", KeyType.HASH)],
                            Projection = new Projection { ProjectionType = ProjectionType.ALL },
                        },
                    },
                ],
            }),
            "an UpdateTable creating two indexes at once");
        // LimitExceededException, NOT ValidationException: the request is well formed, it just asks
        // for more concurrent index work than DynamoDB will do. Measured against real AWS.
        Ensure(err.Code == "LimitExceededException", $"expected LimitExceededException, got {err.Code}");
        const string expected = "Subscriber limit exceeded: Only 1 online index can be created or deleted simultaneously per table";
        Ensure(err.Message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(err.Message)}");
        Ensure(
            (await ddb.DescribeTableAsync(table)).Table.GlobalSecondaryIndexes is not { Count: > 0 },
            "a refused multi-create must leave the table with no index at all");
    }

    /// <summary>The Error from a call that MUST be refused.</summary>
    private static async Task<AwsError> Error(Func<Task> call, string what)
    {
        var error = await FidelityContext.CaptureError(call);
        return error ?? throw Fail($"{what}: the call was ACCEPTED; real AWS rejects it");
    }
}
