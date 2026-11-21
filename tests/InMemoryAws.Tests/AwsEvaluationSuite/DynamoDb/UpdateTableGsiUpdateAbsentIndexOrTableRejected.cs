// Translated from behavioral/dynamodb/check_update_table_gsi_update_absent_index_or_table_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-table-gsi-update-absent-index-or-table-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateTableGsiUpdateAbsentIndexOrTableRejected
{
    private const string CheckId = "update-table-gsi-update-absent-index-or-table-rejected";
    private const long HugeCapacity = 999999999;
    private const string AbsentIndex = "absent-index-name";

    /// <summary>The (Code, Message) fn() raises. An unexpected success raises, naming what came back.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
    }

    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, string table, double deadlineS = 240)
    {
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed.TotalSeconds < deadlineS)
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            if (desc.TableStatus == TableStatus.ACTIVE)
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }

        throw Fail($"{table} never became ACTIVE within {deadlineS}s: {desc}");
    }

    private static List<GlobalSecondaryIndexUpdate> UpdateOf(string indexName, long rcu, long wcu) =>
    [
        new GlobalSecondaryIndexUpdate
        {
            Update = new UpdateGlobalSecondaryIndexAction
            {
                IndexName = indexName,
                ProvisionedThroughput = new ProvisionedThroughput { ReadCapacityUnits = rcu, WriteCapacityUnits = wcu },
            },
        },
    ];

    /// <summary>
    /// UpdateTable naming a GSI the table does not have is ResourceNotFoundException for that
    /// index even with an out-of-range capacity value, and naming a table that does not exist at
    /// all is ResourceNotFoundException for the table instead -- the table lookup runs first.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-table-gsi-update-absent-index-or-table-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-gsiabs-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(async () =>
        {
            try
            {
                await ddb.DeleteTableAsync(table);
            }
            catch (ResourceNotFoundException)
            {
            }
        });
        var desc = await WaitActive(ddb, table);
        Ensure(desc.GlobalSecondaryIndexes is not { Count: > 0 }, $"the new table unexpectedly has GSIs {Py.Repr(desc.GlobalSecondaryIndexes)}");

        // Positive control: a well-formed UpdateTable call on this table is accepted and takes
        // effect, then rolled back so cleanup can delete the table normally.
        await ddb.UpdateTableAsync(new UpdateTableRequest { TableName = table, DeletionProtectionEnabled = true });
        desc = await WaitActive(ddb, table);
        Ensure(desc.DeletionProtectionEnabled == true, $"the positive-control update left DeletionProtectionEnabled={Py.Repr(desc.DeletionProtectionEnabled)}");
        await ddb.UpdateTableAsync(new UpdateTableRequest { TableName = table, DeletionProtectionEnabled = false });
        desc = await WaitActive(ddb, table);
        Ensure(desc.DeletionProtectionEnabled == false, $"turning deletion protection back off left DeletionProtectionEnabled={Py.Repr(desc.DeletionProtectionEnabled)}");

        // Break exactly one thing: Update a GSI the table never had, with an out-of-range RCU.
        var (code, msg) = await Error(() => ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = table,
            GlobalSecondaryIndexUpdates = UpdateOf(AbsentIndex, HugeCapacity, 5),
        }));
        var expectedRcu = ("ResourceNotFoundException", $"Requested resource not found: Index {AbsentIndex} for table {table}");
        Ensure(
            (code, msg) == expectedRcu,
            $"Update of absent index {Py.Repr(AbsentIndex)} with a huge RCU gave {Py.Repr((code, msg))}, expected {Py.Repr(expectedRcu)}");

        // Same, with the huge value on WriteCapacityUnits instead.
        (code, msg) = await Error(() => ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = table,
            GlobalSecondaryIndexUpdates = UpdateOf(AbsentIndex, 5, HugeCapacity),
        }));
        var expectedWcu = ("ResourceNotFoundException", $"Requested resource not found: Index {AbsentIndex} for table {table}");
        Ensure(
            (code, msg) == expectedWcu,
            $"Update of absent index {Py.Repr(AbsentIndex)} with a huge WCU gave {Py.Repr((code, msg))}, expected {Py.Repr(expectedWcu)}");

        // Break the other thing: the TABLE itself does not exist; the table lookup wins over the
        // index lookup and the capacity value, even though the same absent index name is used.
        var absentTable = FidelityContext.UniqueName("sim2real-behav-ddb-gsiabs-notable-");
        (code, msg) = await Error(() => ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = absentTable,
            GlobalSecondaryIndexUpdates = UpdateOf(AbsentIndex, HugeCapacity, 5),
        }));
        var expectedTable = ("ResourceNotFoundException", $"Requested resource not found: Table: {absentTable} not found");
        Ensure((code, msg) == expectedTable, $"Update against a nonexistent table gave {Py.Repr((code, msg))}, expected {Py.Repr(expectedTable)}");

        // None of the three refusals changed the real table.
        desc = (await ddb.DescribeTableAsync(table)).Table;
        Ensure(desc.GlobalSecondaryIndexes is not { Count: > 0 }, $"after the refused Updates the table has GSIs {Py.Repr(desc.GlobalSecondaryIndexes)}");
        Ensure(desc.DeletionProtectionEnabled == false, $"after the refused Updates DeletionProtectionEnabled drifted to {Py.Repr(desc.DeletionProtectionEnabled)}");
    }
}
