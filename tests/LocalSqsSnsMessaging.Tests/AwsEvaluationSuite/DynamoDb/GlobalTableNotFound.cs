// Translated from behavioral/dynamodb/check_global_table_not_found.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.global-table-not-found</summary>
[Category("AwsEvaluationSuite")]
public sealed class GlobalTableNotFound
{
    private const string LegacyVersionMessage =
        "One or more parameter values were invalid: DynamoDB global tables version 2017.11.29 is not "
        + "supported. We recommend using DynamoDB global tables version 2019.11.21, instead of version "
        + "2017.11.29 (Legacy).";

    /// <summary>
    /// Describe/UpdateGlobalTable and Describe/UpdateGlobalTableSettings return
    /// GlobalTableNotFoundException for an unregistered name; the control is either a working real
    /// 2017.11.29 global table or CreateGlobalTable's version-support ValidationException.
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.DynamoDbGlobalTables, "the legacy (2017.11.29) global-table operations are not emulated: CreateGlobalTable answers UnknownOperationException, not the version-support ValidationException")]
    [DisplayName("dynamodb.global-table-not-found")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var realName = FidelityContext.UniqueName("sim2real-behav-gt-");
        var fakeName = FidelityContext.UniqueName("sim2real-behav-no-such-global-table-");

        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = realName,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            StreamSpecification = new StreamSpecification { StreamEnabled = true, StreamViewType = StreamViewType.NEW_AND_OLD_IMAGES },
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(realName));
        await ddb.WaitForTableExists(realName, delaySeconds: 2, maxAttempts: 250);

        // CreateGlobalTable is the one call here that may legitimately succeed (where legacy Global
        // Tables is still creatable), so it is caught explicitly rather than through the helper.
        var createError = await FidelityContext.CaptureError(() => ddb.CreateGlobalTableAsync(new CreateGlobalTableRequest
        {
            GlobalTableName = realName,
            ReplicationGroup = [new Replica { RegionName = ctx.Region }],
        }));
        var createCode = createError?.Code;
        var createMessage = createError?.Message;
        if (createError is null)
        {
            // Version 2017.11.29 is still creatable here: the calls succeeding on the real global
            // table rule out a blanket GlobalTableNotFoundException.
            await WaitActive(ddb, realName);
            var realDescribe = (await ddb.DescribeGlobalTableAsync(new DescribeGlobalTableRequest { GlobalTableName = realName })).GlobalTableDescription;
            Ensure(realDescribe.GlobalTableName == realName, Py.Repr(realDescribe.GlobalTableName));
            await ddb.DescribeGlobalTableSettingsAsync(new DescribeGlobalTableSettingsRequest { GlobalTableName = realName });
            await ddb.UpdateGlobalTableSettingsAsync(new UpdateGlobalTableSettingsRequest { GlobalTableName = realName, GlobalTableBillingMode = BillingMode.PAY_PER_REQUEST });
        }
        else
        {
            // Version 2017.11.29 is withdrawn here: CreateGlobalTable's distinct version-support
            // ValidationException is the control against a blanket GlobalTableNotFoundException.
            Ensure(createCode == "ValidationException", $"expected ValidationException (legacy Global Tables version withdrawn), got {createCode}");
            Ensure(createMessage == LegacyVersionMessage, $"expected message {Py.Repr(LegacyVersionMessage)}, got {Py.Repr(createMessage)}");
        }

        // DescribeGlobalTable/UpdateGlobalTable prefix the not-found text; the two settings
        // operations do not.
        var expectedNotFound = $"Global table not found: Global table with name: '{fakeName}' does not exist.";
        var expectedSettingsNotFound = $"Global table with name: '{fakeName}' does not exist.";

        var (describeCode, describeMessage) = await ErrorCodeAndMessage(() => ddb.DescribeGlobalTableAsync(new DescribeGlobalTableRequest { GlobalTableName = fakeName }));
        Ensure(describeCode == "GlobalTableNotFoundException", $"expected GlobalTableNotFoundException, got {describeCode}");
        Ensure(describeMessage == expectedNotFound, $"expected message {Py.Repr(expectedNotFound)}, got {Py.Repr(describeMessage)}");

        // A "Create" replica action can hit the 2017.11.29 version-support ValidationException
        // first; "Delete" still resolves the existence check.
        var (updateCode, updateMessage) = await ErrorCodeAndMessage(() => ddb.UpdateGlobalTableAsync(new UpdateGlobalTableRequest
        {
            GlobalTableName = fakeName,
            ReplicaUpdates = [new ReplicaUpdate { Delete = new DeleteReplicaAction { RegionName = ctx.Region } }],
        }));
        Ensure(updateCode == "GlobalTableNotFoundException", $"expected GlobalTableNotFoundException, got {updateCode}");
        Ensure(updateMessage == expectedNotFound, $"expected message {Py.Repr(expectedNotFound)}, got {Py.Repr(updateMessage)}");

        if (createError is null)
        {
            // The real global table's replication group is untouched by the failed update.
            var realReplicationGroup = (await ddb.DescribeGlobalTableAsync(new DescribeGlobalTableRequest { GlobalTableName = realName })).GlobalTableDescription.ReplicationGroup;
            var regions = realReplicationGroup.Select(r => r.RegionName).ToList();
            Ensure(regions.SequenceEqual([ctx.Region]), Py.Repr(regions));
        }

        var (settingsDescribeCode, settingsDescribeMessage) = await ErrorCodeAndMessage(() => ddb.DescribeGlobalTableSettingsAsync(new DescribeGlobalTableSettingsRequest { GlobalTableName = fakeName }));
        Ensure(settingsDescribeCode == "GlobalTableNotFoundException", $"expected GlobalTableNotFoundException, got {settingsDescribeCode}");
        Ensure(
            settingsDescribeMessage == expectedSettingsNotFound,
            $"expected message {Py.Repr(expectedSettingsNotFound)}, got {Py.Repr(settingsDescribeMessage)}");

        var (settingsUpdateCode, settingsUpdateMessage) = await ErrorCodeAndMessage(() => ddb.UpdateGlobalTableSettingsAsync(new UpdateGlobalTableSettingsRequest
        {
            GlobalTableName = fakeName,
            GlobalTableBillingMode = BillingMode.PAY_PER_REQUEST,
        }));
        Ensure(settingsUpdateCode == "GlobalTableNotFoundException", $"expected GlobalTableNotFoundException, got {settingsUpdateCode}");
        Ensure(
            settingsUpdateMessage == expectedSettingsNotFound,
            $"expected message {Py.Repr(expectedSettingsNotFound)}, got {Py.Repr(settingsUpdateMessage)}");
    }

    /// <summary>Return fn()'s error code and message; an unexpected success raises AssertionError.</summary>
    private static async Task<(string? Code, string? Message)> ErrorCodeAndMessage<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected a ClientError, but the call SUCCEEDED and returned {Py.Repr(result)}");
        }

        return (error.Code, error.Message);
    }

    private static async Task WaitActive(IAmazonDynamoDB ddb, string name, double timeoutSeconds = 180.0, double intervalSeconds = 3.0)
    {
        var clock = FidelityTime.StartNew();
        string? status = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            status = (await ddb.DescribeGlobalTableAsync(new DescribeGlobalTableRequest { GlobalTableName = name })).GlobalTableDescription.GlobalTableStatus?.Value;
            if (status == "ACTIVE")
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"global table {name} never reached ACTIVE, last status={Py.Repr(status)}");
    }
}
