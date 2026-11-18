// Translated from behavioral/dynamodb/check_update_table_gsi_attribute_definitions_track_live_keys.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-table-gsi-attribute-definitions-track-live-keys</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateTableGsiAttributeDefinitionsTrackLiveKeys
{
    private const double WaitS = 1500.0;

    /// <summary>
    /// UpdateTable's GlobalSecondaryIndexUpdates leaves AttributeDefinitions as exactly the
    /// attributes a live key still uses after a GSI create and delete, and rejects a GSI keyed on
    /// an undeclared attribute and a throughput update that changes nothing.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-table-gsi-attribute-definitions-track-live-keys")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;

        // --- Provisioned-throughput no-op rejection, with a positive control. ---
        var provTable = FidelityContext.UniqueName("sim2real-behav-ddb-prov-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = provTable,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PROVISIONED,
            ProvisionedThroughput = new ProvisionedThroughput { ReadCapacityUnits = 5, WriteCapacityUnits = 5 },
        });
        ctx.OnCleanup(DeleteTable(ddb, provTable));
        await WaitTableSettled(ddb, provTable);

        // Positive control: changing the throughput to a genuinely different value succeeds.
        await ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = provTable,
            ProvisionedThroughput = new ProvisionedThroughput { ReadCapacityUnits = 7, WriteCapacityUnits = 7 },
        });
        var settled = await WaitTableSettled(ddb, provTable);
        var pt = settled.ProvisionedThroughput;
        Ensure(
            pt.ReadCapacityUnits == 7 && pt.WriteCapacityUnits == 7,
            $"expected 7/7 after the throughput update, got {pt.ReadCapacityUnits}/{pt.WriteCapacityUnits}");

        var (code, message) = await Error(() => ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = provTable,
            ProvisionedThroughput = new ProvisionedThroughput { ReadCapacityUnits = 7, WriteCapacityUnits = 7 },
        }));
        Ensure(code == "ValidationException", $"expected ValidationException, got {code}: {Py.Repr(message)}");
        const string expectedNoopMsg =
            "The provisioned throughput for the table will not change. The requested value equals "
            + "the current value. Current ReadCapacityUnits provisioned for the table: 7. Requested "
            + "ReadCapacityUnits: 7. Current WriteCapacityUnits provisioned for the table: 7. Requested "
            + "WriteCapacityUnits: 7. Refer to the Amazon DynamoDB Developer Guide for current limits "
            + "and how to request higher limits.";
        Ensure(message == expectedNoopMsg, $"expected message {Py.Repr(expectedNoopMsg)}, got {Py.Repr(message)}");

        // --- GSI create/delete tracks AttributeDefinitions; undeclared-key rejection. ---
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-gsi-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(DeleteTable(ddb, table));
        await WaitTableSettled(ddb, table);

        // Create a GSI while also declaring an AttributeDefinition no key (base or index) uses.
        await ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = table,
            AttributeDefinitions =
            [
                new AttributeDefinition("g1", ScalarAttributeType.S),
                new AttributeDefinition("unused", ScalarAttributeType.S),
            ],
            GlobalSecondaryIndexUpdates =
            [
                new GlobalSecondaryIndexUpdate
                {
                    Create = new CreateGlobalSecondaryIndexAction
                    {
                        IndexName = "gsi-main",
                        KeySchema = [new KeySchemaElement("g1", KeyType.HASH)],
                        Projection = new Projection { ProjectionType = ProjectionType.ALL },
                    },
                },
            ],
        });
        var created = await WaitTableSettled(ddb, table);
        var createdAttrs = Attrs(created);
        Ensure(
            Py.Equal(createdAttrs, Map(("pk", "S"), ("g1", "S"))),
            $"expected AttributeDefinitions to hold only the live keys {{'pk': 'S', 'g1': 'S'}}, got {Py.Repr(createdAttrs)}");

        // Rejection: a second GSI keyed on an attribute never declared in AttributeDefinitions.
        (code, message) = await Error(() => ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = table,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            GlobalSecondaryIndexUpdates =
            [
                new GlobalSecondaryIndexUpdate
                {
                    Create = new CreateGlobalSecondaryIndexAction
                    {
                        IndexName = "gsi-undeclared",
                        KeySchema = [new KeySchemaElement("undeclared_attr", KeyType.HASH)],
                        Projection = new Projection { ProjectionType = ProjectionType.ALL },
                    },
                },
            ],
        }));
        Ensure(code == "ValidationException", $"expected ValidationException, got {code}: {Py.Repr(message)}");
        const string expectedUndeclaredMsg =
            "One or more parameter values were invalid: Some index key attributes are not defined "
            + "in AttributeDefinitions. Keys: [undeclared_attr], AttributeDefinitions: [pk]";
        Ensure(message == expectedUndeclaredMsg, $"expected message {Py.Repr(expectedUndeclaredMsg)}, got {Py.Repr(message)}");

        // The rejected create must not have touched the table: same index, same attributes.
        var afterReject = (await ddb.DescribeTableAsync(table)).Table;
        var gsiNames = (afterReject.GlobalSecondaryIndexes ?? []).Select(g => g.IndexName).ToList();
        Ensure(gsiNames.SequenceEqual(["gsi-main"]), $"rejected create must leave the index set alone, got {Py.Repr(gsiNames)}");
        var afterRejectAttrs = Attrs(afterReject);
        Ensure(
            Py.Equal(afterRejectAttrs, Map(("pk", "S"), ("g1", "S"))),
            $"rejected create must leave AttributeDefinitions alone, got {Py.Repr(afterRejectAttrs)}");

        // Delete the GSI; the attribute it alone used drops out of AttributeDefinitions too.
        await ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = table,
            GlobalSecondaryIndexUpdates = [new GlobalSecondaryIndexUpdate { Delete = new DeleteGlobalSecondaryIndexAction { IndexName = "gsi-main" } }],
        });
        var deleted = await WaitTableSettled(ddb, table);
        Ensure(
            deleted.GlobalSecondaryIndexes is null || deleted.GlobalSecondaryIndexes.Count == 0,
            $"expected no indexes after delete, got {Py.Repr(deleted.GlobalSecondaryIndexes?.Select(g => g.IndexName).ToList())}");
        var deletedAttrs = Attrs(deleted);
        Ensure(
            Py.Equal(deletedAttrs, Map(("pk", "S"))),
            $"expected AttributeDefinitions to shrink back to the base key {{'pk': 'S'}}, got {Py.Repr(deletedAttrs)}");
    }

    private static Dictionary<string, string> Attrs(TableDescription table) =>
        table.AttributeDefinitions.ToDictionary(a => a.AttributeName, a => a.AttributeType.Value, StringComparer.Ordinal);

    private static Dictionary<string, string> Map(params (string Name, string Type)[] entries) =>
        entries.ToDictionary(e => e.Name, e => e.Type, StringComparer.Ordinal);

    /// <summary>(Error.Code, Error.Message) of the ClientError fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be rejected, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }

    /// <summary>Poll DescribeTable until the table and every index are ACTIVE.</summary>
    private static async Task<TableDescription> WaitTableSettled(IAmazonDynamoDB ddb, string name, double timeoutS = WaitS, double intervalS = 5.0)
    {
        var clock = FidelityTime.StartNew();
        TableDescription? table = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            table = (await ddb.DescribeTableAsync(name)).Table;
            var busy = (table.GlobalSecondaryIndexes ?? []).Where(i => i.IndexStatus != IndexStatus.ACTIVE).ToList();
            if (table.TableStatus == TableStatus.ACTIVE && busy.Count == 0)
            {
                return table;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"{name} never settled to ACTIVE within {timeoutS}s; last={table?.TableStatus?.Value}");
    }

    private static Func<Task> DeleteTable(IAmazonDynamoDB ddb, string name) => async () =>
    {
        try
        {
            await ddb.DeleteTableAsync(name);
        }
        catch (ResourceNotFoundException)
        {
        }
    };
}
