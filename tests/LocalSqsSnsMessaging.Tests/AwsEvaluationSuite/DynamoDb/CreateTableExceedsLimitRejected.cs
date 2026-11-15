// Translated from behavioral/dynamodb/check_create_table_exceeds_limit_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.create-table-exceeds-limit-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateTableExceedsLimitRejected
{
    private const string CheckId = "create-table-exceeds-limit-rejected";

    /// <summary>
    /// UpdateTable adding a 21st global secondary index to a table at the default quota of 20 fails with
    /// LimitExceededException and AWS's exact message, and the index is not added.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.create-table-exceeds-limit-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableName = FidelityContext.UniqueName("sim2real-behav-ddb-");

        const int numInitialGsis = 20; // default per-table quota is 20 global secondary indexes
        var attributeDefinitions = new List<AttributeDefinition> { new("pk", ScalarAttributeType.S) };
        var gsis = new List<GlobalSecondaryIndex>();
        for (var i = 0; i < numInitialGsis; i++)
        {
            var attr = string.Create(CultureInfo.InvariantCulture, $"gsi{i}pk");
            attributeDefinitions.Add(new AttributeDefinition(attr, ScalarAttributeType.S));
            gsis.Add(new GlobalSecondaryIndex
            {
                IndexName = string.Create(CultureInfo.InvariantCulture, $"gsi{i}"),
                KeySchema = [new KeySchemaElement(attr, KeyType.HASH)],
                Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
            });
        }

        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = attributeDefinitions,
            GlobalSecondaryIndexes = gsis,
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => SafeDeleteTable(ddb, tableName));
        await WaitAllGsiActive(ddb, tableName);

        // The table is already at the 20-GSI quota; adding a 21st must be
        // rejected wholesale.
        var (code, message) = await Error(() => ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = tableName,
            AttributeDefinitions = [new AttributeDefinition("gsi20pk", ScalarAttributeType.S)],
            GlobalSecondaryIndexUpdates =
            [
                new GlobalSecondaryIndexUpdate
                {
                    Create = new CreateGlobalSecondaryIndexAction
                    {
                        IndexName = "gsi20",
                        KeySchema = [new KeySchemaElement("gsi20pk", KeyType.HASH)],
                        Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
                    },
                },
            ],
        }));
        Ensure(code == "LimitExceededException", $"expected LimitExceededException, got {Py.Repr(code)}");
        const string expectedMsg = "Subscriber limit exceeded: Number of global secondary indexes exceeds per-table limit of 20";
        Ensure(message == expectedMsg, $"expected message {Py.Repr(expectedMsg)}, got {Py.Repr(message)}");

        // The over-limit 21st index must not have actually been created.
        var desc = (await ddb.DescribeTableAsync(tableName)).Table;
        var gsiNames = (desc.GlobalSecondaryIndexes ?? []).Select(g => g.IndexName).ToHashSet(StringComparer.Ordinal);
        Ensure(
            !gsiNames.Contains("gsi20"),
            $"expected the rejected 21st GSI to not exist, found indexes {{{string.Join(", ", gsiNames.Order(StringComparer.Ordinal).Select(n => Py.Repr(n)))}}}");
    }

    /// <summary>
    /// Call fn() and return the (Code, Message) of the ClientError it raises; an accepted call
    /// is itself a failure.
    /// </summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected a ClientError, but the call succeeded: {result}");
    }

    private static async Task<TableDescription> WaitAllGsiActive(IAmazonDynamoDB ddb, string tableName, double timeoutSeconds = 600.0, double intervalSeconds = 5.0)
    {
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed.TotalSeconds < timeoutSeconds)
        {
            var desc = (await ddb.DescribeTableAsync(tableName)).Table;
            var gsis = desc.GlobalSecondaryIndexes ?? [];
            if (desc.TableStatus == "ACTIVE" && gsis.All(g => g.IndexStatus == "ACTIVE"))
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"table {tableName} and its GSIs never became ACTIVE");
    }

    private static async Task SafeDeleteTable(IAmazonDynamoDB ddb, string tableName)
    {
        var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(tableName));
        if (error is not null && error.Code != "ResourceNotFoundException")
        {
            throw error.Exception;
        }
    }
}
