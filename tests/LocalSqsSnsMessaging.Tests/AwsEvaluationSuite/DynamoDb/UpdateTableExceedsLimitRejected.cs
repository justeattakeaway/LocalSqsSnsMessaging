// Translated from behavioral/dynamodb/check_update_table_exceeds_limit_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-table-exceeds-limit-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateTableExceedsLimitRejected
{
    /// <summary>
    /// UpdateTable with two GSI creates in one call is rejected with LimitExceededException and its exact message,
    /// and leaves only gsi-pos, which a single-create UpdateTable added first; one GSI change per call is the limit
    /// clients rely on.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-table-exceeds-limit-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableName = FidelityContext.UniqueName("sim2real-behav-ddb-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => SafeDeleteTable(ddb, tableName));
        await WaitTableActive(ddb, tableName);

        // Positive control: a lone GSI create action in its own UpdateTable call
        // must be ACCEPTED (no exception) - this is what tells the negative below
        // apart from a target that rejects every GSI-via-UpdateTable call.
        await ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = tableName,
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("gsiPos", ScalarAttributeType.S),
            ],
            GlobalSecondaryIndexUpdates =
            [
                new GlobalSecondaryIndexUpdate
                {
                    Create = new CreateGlobalSecondaryIndexAction
                    {
                        IndexName = "gsi-pos",
                        KeySchema = [new KeySchemaElement("gsiPos", KeyType.HASH)],
                        Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
                    },
                },
            ],
        });

        var err = await Error(() => ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = tableName,
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("gsiA", ScalarAttributeType.S),
                new AttributeDefinition("gsiB", ScalarAttributeType.S),
            ],
            GlobalSecondaryIndexUpdates =
            [
                new GlobalSecondaryIndexUpdate
                {
                    Create = new CreateGlobalSecondaryIndexAction
                    {
                        IndexName = "gsi-a",
                        KeySchema = [new KeySchemaElement("gsiA", KeyType.HASH)],
                        Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
                    },
                },
                new GlobalSecondaryIndexUpdate
                {
                    Create = new CreateGlobalSecondaryIndexAction
                    {
                        IndexName = "gsi-b",
                        KeySchema = [new KeySchemaElement("gsiB", KeyType.HASH)],
                        Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
                    },
                },
            ],
        }));
        Ensure(err.Code == "LimitExceededException", $"expected LimitExceededException, got {err}");
        const string expected = "Subscriber limit exceeded: Only 1 online index can be created or deleted simultaneously per table";
        Ensure(err.Message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(err.Message)}");

        // The rejected request applied neither create: the table is ACTIVE (or UPDATING while gsi-pos
        // backfills) with only gsi-pos.
        var desc = (await ddb.DescribeTableAsync(tableName)).Table;
        Ensure(
            desc.TableStatus == TableStatus.ACTIVE || desc.TableStatus == TableStatus.UPDATING,
            $"expected ACTIVE or UPDATING, got {Py.Repr(desc.TableStatus?.Value)}");
        var gsiNames = (desc.GlobalSecondaryIndexes ?? []).Select(i => i.IndexName).ToHashSet(StringComparer.Ordinal);
        Ensure(
            gsiNames.SetEquals(["gsi-pos"]),
            $"expected only the positive-control gsi-pos to be present, found {"{" + string.Join(", ", gsiNames.Select(Py.Repr)) + "}"}");
    }

    /// <summary>
    /// Call fn(); return its botocore Error. An unexpected SUCCESS raises, because an
    /// over-permissive target ACCEPTING a call real AWS rejects is the strongest signal available.
    /// </summary>
    private static async Task<AwsError> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"real AWS rejects this call, but it SUCCEEDED and returned {result}");
    }

    private static async Task<TableDescription> WaitTableActive(IAmazonDynamoDB ddb, string tableName, double timeoutS = 120.0, double intervalS = 2.0)
    {
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var desc = (await ddb.DescribeTableAsync(tableName)).Table;
            if (desc.TableStatus == TableStatus.ACTIVE)
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"table {tableName} never became ACTIVE");
    }

    /// <summary>Delete the table once no index is still building; DeleteTable refuses it before then.</summary>
    private static async Task SafeDeleteTable(IAmazonDynamoDB ddb, string tableName, double timeoutS = 600.0, double intervalS = 5.0)
    {
        try
        {
            var clock = FidelityTime.StartNew();
            while (true)
            {
                var desc = (await ddb.DescribeTableAsync(tableName)).Table;
                var busy = (desc.GlobalSecondaryIndexes ?? []).Where(i => i.IndexStatus != IndexStatus.ACTIVE).ToList();
                if (desc.TableStatus == TableStatus.ACTIVE && busy.Count == 0)
                {
                    break;
                }

                if (clock.Elapsed >= TimeSpan.FromSeconds(timeoutS))
                {
                    throw Fail($"table {tableName} indexes never settled for cleanup");
                }

                await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
            }

            await ddb.DeleteTableAsync(tableName);
        }
        catch (ResourceNotFoundException)
        {
        }
    }
}
