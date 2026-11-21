// Translated from behavioral/dynamodb/check_update_table_gsi_update_shape_validation_precedes_table_lookup_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-table-gsi-update-shape-validation-precedes-table-lookup-rejected</summary>
/// <remarks>
/// botocore leaves out a member set to <c>None</c>, so the Python's null capacity members go on the
/// wire as an empty <c>ProvisionedThroughput</c>, as the .NET SDK sends one with neither member set.
/// </remarks>
[Category("AwsEvaluationSuite")]
public sealed class UpdateTableGsiUpdateShapeValidationPrecedesTableLookupRejected
{
    private const string CheckId = "update-table-gsi-update-shape-validation-precedes-table-lookup-rejected";

    // Fields, not literals: the SDK's analyzers refuse out-of-range literal capacities.
    private static readonly long MinusOne = -1;
    private static readonly long Zero = 0;

    // Fields, not literals: the SDK's analyzers refuse a literal index name that breaks the model's pattern or length.
    private static readonly string BadName = "bad name!";
    private static readonly string ShortName = "cd";

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

    /// <summary>
    /// UpdateTable folds a pattern-invalid name, null capacity members, a length-invalid name
    /// and a missing capacity bound into one ValidationException, and answers out-of-range GSI
    /// capacity values with ValidationException even against a table that does not exist.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-table-gsi-update-shape-validation-precedes-table-lookup-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        // Config(parameter_validation=False): the .NET SDK doesn't validate client-side.
        var lax = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-gsishape-");
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
        await WaitActive(ddb, table);

        // Positive control: a well-formed UpdateTable call on this table is accepted and takes
        // effect, round-tripped back off before cleanup needs to delete the table.
        await ddb.UpdateTableAsync(new UpdateTableRequest { TableName = table, DeletionProtectionEnabled = true });
        var desc = await WaitActive(ddb, table);
        Ensure(desc.DeletionProtectionEnabled == true, $"the positive-control update left DeletionProtectionEnabled={Py.Repr(desc.DeletionProtectionEnabled)}");
        await ddb.UpdateTableAsync(new UpdateTableRequest { TableName = table, DeletionProtectionEnabled = false });
        desc = await WaitActive(ddb, table);
        Ensure(desc.DeletionProtectionEnabled == false, $"turning deletion protection back off left DeletionProtectionEnabled={Py.Repr(desc.DeletionProtectionEnabled)}");

        // Break exactly one thing: two Update entries whose shape botocore would normally refuse
        // client-side -- a pattern-invalid name with null capacity members, and a length-invalid
        // name missing one capacity bound -- sent past client validation so AWS answers them.
        var (code, msg) = await Error(() => lax.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = table,
            GlobalSecondaryIndexUpdates =
            [
                new GlobalSecondaryIndexUpdate
                {
                    Update = new UpdateGlobalSecondaryIndexAction { IndexName = BadName, ProvisionedThroughput = new ProvisionedThroughput() },
                },
                new GlobalSecondaryIndexUpdate
                {
                    Update = new UpdateGlobalSecondaryIndexAction { IndexName = ShortName, ProvisionedThroughput = new ProvisionedThroughput { ReadCapacityUnits = 5 } },
                },
            ],
        }));
        var expectedFold = (
            "ValidationException",
            "5 validation errors detected: Value 'bad name!' at "
            + "'globalSecondaryIndexUpdates.1.member.update.indexName' failed to satisfy constraint: "
            + "Member must satisfy regular expression pattern: [a-zA-Z0-9_.-]+; Value null at "
            + "'globalSecondaryIndexUpdates.1.member.update.provisionedThroughput.writeCapacityUnits' "
            + "failed to satisfy constraint: Member must not be null; Value null at "
            + "'globalSecondaryIndexUpdates.1.member.update.provisionedThroughput.readCapacityUnits' "
            + "failed to satisfy constraint: Member must not be null; Value 'cd' at "
            + "'globalSecondaryIndexUpdates.2.member.update.indexName' failed to satisfy constraint: "
            + "Member must have length greater than or equal to 3; Value null at "
            + "'globalSecondaryIndexUpdates.2.member.update.provisionedThroughput.writeCapacityUnits' "
            + "failed to satisfy constraint: Member must not be null");
        Ensure((code, msg) == expectedFold, $"the two malformed Update entries gave {Py.Repr((code, msg))}, expected {Py.Repr(expectedFold)}");

        // Break the other thing: a GSI update whose capacity values violate the model's own
        // minimum, aimed at a table that was never created. The shape violation is answered, not
        // the table's absence.
        var absentTable = FidelityContext.UniqueName("sim2real-behav-ddb-gsishape-notable-");
        (code, msg) = await Error(() => lax.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = absentTable,
            GlobalSecondaryIndexUpdates =
            [
                new GlobalSecondaryIndexUpdate
                {
                    Update = new UpdateGlobalSecondaryIndexAction
                    {
                        IndexName = "whatever",
                        ProvisionedThroughput = new ProvisionedThroughput { ReadCapacityUnits = MinusOne, WriteCapacityUnits = Zero },
                    },
                },
            ],
        }));
        var expectedShape = (
            "ValidationException",
            "2 validation errors detected: Value '0' at "
            + "'globalSecondaryIndexUpdates.1.member.update.provisionedThroughput.writeCapacityUnits' "
            + "failed to satisfy constraint: Member must have value greater than or equal to 1; "
            + "Value '-1' at "
            + "'globalSecondaryIndexUpdates.1.member.update.provisionedThroughput.readCapacityUnits' "
            + "failed to satisfy constraint: Member must have value greater than or equal to 1");
        Ensure(
            (code, msg) == expectedShape,
            $"the out-of-range capacity against a nonexistent table gave {Py.Repr((code, msg))}, expected {Py.Repr(expectedShape)}");

        // Neither rejected call changed the real table.
        desc = (await ddb.DescribeTableAsync(table)).Table;
        Ensure(desc.GlobalSecondaryIndexes is not { Count: > 0 }, $"the table unexpectedly has GSIs {Py.Repr(desc.GlobalSecondaryIndexes)}");
        Ensure(desc.DeletionProtectionEnabled == false, $"the table's DeletionProtectionEnabled drifted to {Py.Repr(desc.DeletionProtectionEnabled)}");
    }
}
