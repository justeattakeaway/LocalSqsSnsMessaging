// Translated from behavioral/dynamodb/check_global_table_settings_index_not_found.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.global-table-settings-index-not-found</summary>
[Category("AwsEvaluationSuite")]
public sealed class GlobalTableSettingsIndexNotFound
{
    private const string CheckId = "global-table-settings-index-not-found";

    private const string LegacyVersionMessage =
        "One or more parameter values were invalid: DynamoDB global tables version 2017.11.29 is not "
        + "supported. We recommend using DynamoDB global tables version 2019.11.21, instead of version "
        + "2017.11.29 (Legacy).";

    /// <summary>
    /// For an ACTIVE table, CreateGlobalTable raises the version ValidationException, UpdateGlobalTableSettings naming
    /// a missing index raises GlobalTableNotFoundException, each with the exact message, and ListGlobalTables
    /// does not list the table.
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.DynamoDbGlobalTables, "the legacy (2017.11.29) global-table operations are not emulated: CreateGlobalTable answers UnknownOperationException, not the version-support ValidationException")]
    [DisplayName("dynamodb.global-table-settings-index-not-found")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName("sim2real-behav-gt-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("gsi_pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PROVISIONED,
            ProvisionedThroughput = new ProvisionedThroughput { ReadCapacityUnits = 5, WriteCapacityUnits = 5 },
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = "real-gsi",
                    KeySchema = [new KeySchemaElement("gsi_pk", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                    ProvisionedThroughput = new ProvisionedThroughput { ReadCapacityUnits = 5, WriteCapacityUnits = 5 },
                },
            ],
            StreamSpecification = new StreamSpecification { StreamEnabled = true, StreamViewType = StreamViewType.NEW_AND_OLD_IMAGES },
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 250);

        // Positive control: the account and this table are otherwise healthy before Global Tables
        // enters the picture at all -- so a later rejection cannot be "everything is denied".
        var described = (await ddb.DescribeTableAsync(name)).Table;
        Ensure(described.TableStatus == TableStatus.ACTIVE, Py.Repr(described.TableStatus));

        var (createCode, createMessage) = await ErrorCodeAndMessage(() => ddb.CreateGlobalTableAsync(new CreateGlobalTableRequest
        {
            GlobalTableName = name,
            ReplicationGroup = [new Replica { RegionName = ctx.Region }],
        }));
        Ensure(createCode == "ValidationException", $"expected ValidationException, got {createCode}: {createMessage}");
        Ensure(createMessage == LegacyVersionMessage, $"expected message {Py.Repr(LegacyVersionMessage)}, got {Py.Repr(createMessage)}");

        // The index-focused call, pointed at a fake index anyway, still fails deterministically with
        // GlobalTableNotFoundException -- nothing here is tracked by the legacy control plane -- rather
        // than ever reaching the per-index validation IndexNotFoundException would come from.
        var (updateCode, updateMessage) = await ErrorCodeAndMessage(() => ddb.UpdateGlobalTableSettingsAsync(new UpdateGlobalTableSettingsRequest
        {
            GlobalTableName = name,
            GlobalTableGlobalSecondaryIndexSettingsUpdate =
            [
                new GlobalTableGlobalSecondaryIndexSettingsUpdate { IndexName = "totally-fake-gsi", ProvisionedWriteCapacityUnits = 5 },
            ],
        }));
        Ensure(updateCode == "GlobalTableNotFoundException", $"expected GlobalTableNotFoundException, got {updateCode}: {updateMessage}");
        var expectedNotFound = $"Global table with name: '{name}' does not exist.";
        Ensure(updateMessage == expectedNotFound, $"expected message {Py.Repr(expectedNotFound)}, got {Py.Repr(updateMessage)}");

        // Effect absent, via an independent read at the destination: this table was never registered
        // as a global table.
        var listed = (await ddb.ListGlobalTablesAsync(new ListGlobalTablesRequest())).GlobalTables ?? [];
        Ensure(!listed.Any(gt => gt.GlobalTableName == name), Py.Repr(listed.Select(gt => gt.GlobalTableName).ToList()));
    }

    private static async Task<(string? Code, string Message)> ErrorCodeAndMessage<T>(Func<Task<T>> fn)
        where T : class
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is null)
        {
            throw Fail("expected a ClientError but the call succeeded");
        }

        return (error.Code, error.Message ?? string.Empty);
    }
}
