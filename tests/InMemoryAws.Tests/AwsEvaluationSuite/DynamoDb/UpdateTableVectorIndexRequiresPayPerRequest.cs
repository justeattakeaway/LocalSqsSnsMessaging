// Translated from behavioral/dynamodb/check_update_table_vector_index_requires_pay_per_request.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-table-vector-index-requires-pay-per-request</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateTableVectorIndexRequiresPayPerRequest
{
    private const string Check = "update-table-vector-index-requires-pay-per-request";

    private static readonly (string Code, string Message) NoVectorIndex = (
        "ValidationException",
        "One or more parameter values were invalid: Vector indexes are only "
        + "supported for PAY_PER_REQUEST tables");

    /// <summary>
    /// UpdateTable refuses adding a vector index to a PROVISIONED table and refuses switching a
    /// table that already has a vector index to PROVISIONED; each leaves the table unchanged.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-table-vector-index-requires-pay-per-request")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;

        // A PROVISIONED table may not gain a vector index.
        var provisioned = FidelityContext.UniqueName("sim2real-behav-ddbvecprov-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = provisioned,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PROVISIONED,
            ProvisionedThroughput = new ProvisionedThroughput { ReadCapacityUnits = 1, WriteCapacityUnits = 1 },
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(provisioned), "ResourceNotFoundException"));
        await WaitActive(ddb, provisioned);

        await ExpectError(
            () => ddb.UpdateTableAsync(new UpdateTableRequest
            {
                TableName = provisioned,
                VectorIndexUpdates =
                [
                    new VectorIndexUpdate
                    {
                        Create = new CreateVectorIndexAction
                        {
                            IndexName = "idx",
                            VectorAttribute = new VectorAttributeDefinition { AttributeName = "emb" },
                            Dimensions = 8,
                            DistanceFunction = VectorDistanceFunction.COSINE,
                            Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
                        },
                    },
                ],
            }),
            NoVectorIndex.Code,
            NoVectorIndex.Message);

        var tbl = (await ddb.DescribeTableAsync(provisioned)).Table;
        Ensure(
            tbl.VectorIndexes is null || tbl.VectorIndexes.Count == 0,
            $"the refused UpdateTable left a vector index on {provisioned}: {Py.Repr(tbl.VectorIndexes?.Select(v => v.IndexName).ToList())}");
        Ensure(tbl.ProvisionedThroughput.ReadCapacityUnits == 1, $"expected ReadCapacityUnits 1, got {Pt(tbl)}");
        Ensure(tbl.ProvisionedThroughput.WriteCapacityUnits == 1, $"expected WriteCapacityUnits 1, got {Pt(tbl)}");

        // Positive control: the same table still accepts an ordinary UpdateTable.
        await ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = provisioned,
            ProvisionedThroughput = new ProvisionedThroughput { ReadCapacityUnits = 2, WriteCapacityUnits = 2 },
        });
        var clock = FidelityTime.StartNew();
        tbl = await WaitActive(ddb, provisioned);
        while (tbl.ProvisionedThroughput.ReadCapacityUnits != 2)
        {
            Ensure(clock.Elapsed < TimeSpan.FromSeconds(240), $"the throughput bump never settled: {Pt(tbl)}");
            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
            tbl = await WaitActive(ddb, provisioned);
        }

        Ensure(tbl.ProvisionedThroughput.WriteCapacityUnits == 2, $"expected WriteCapacityUnits 2 after the bump settled, got {Pt(tbl)}");

        // A table that already carries a vector index may not switch to PROVISIONED.
        var vectored = FidelityContext.UniqueName("sim2real-behav-ddbvecprov-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = vectored,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            VectorIndexes =
            [
                new VectorIndex
                {
                    IndexName = "idx2",
                    VectorAttribute = new VectorAttributeDefinition { AttributeName = "emb" },
                    Dimensions = 8,
                    DistanceFunction = VectorDistanceFunction.COSINE,
                    Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
                },
            ],
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(vectored), "ResourceNotFoundException"));
        await WaitActive(ddb, vectored);

        await ExpectError(
            () => ddb.UpdateTableAsync(new UpdateTableRequest
            {
                TableName = vectored,
                BillingMode = BillingMode.PROVISIONED,
                ProvisionedThroughput = new ProvisionedThroughput { ReadCapacityUnits = 1, WriteCapacityUnits = 1 },
            }),
            NoVectorIndex.Code,
            NoVectorIndex.Message);

        tbl = (await ddb.DescribeTableAsync(vectored)).Table;
        Ensure(
            tbl.BillingModeSummary?.BillingMode == BillingMode.PAY_PER_REQUEST,
            $"the refused UpdateTable switched billing mode: {Py.Repr(tbl.BillingModeSummary?.BillingMode?.Value)}");
        var names = (tbl.VectorIndexes ?? []).Select(v => v.IndexName).ToList();
        Ensure(names.SequenceEqual(["idx2"]), $"the refused UpdateTable changed the vector indexes: {Py.Repr(names)}");
    }

    private static string Pt(TableDescription tbl) =>
        $"{{'ReadCapacityUnits': {tbl.ProvisionedThroughput?.ReadCapacityUnits}, 'WriteCapacityUnits': {tbl.ProvisionedThroughput?.WriteCapacityUnits}}}";

    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }

    private static async Task ExpectError<T>(Func<Task<T>> fn, string code, string message)
        where T : class
    {
        var (gotCode, gotMessage) = await Error(fn);
        Ensure(gotCode == code, $"expected error code {Py.Repr(code)}, got {Py.Repr(gotCode)} ({Py.Repr(gotMessage)})");
        Ensure(gotMessage == message, $"expected message {Py.Repr(message)}, got {Py.Repr(gotMessage)}");
    }

    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };

    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, string table, double deadlineS = 240)
    {
        var clock = FidelityTime.StartNew();
        var tbl = (await ddb.DescribeTableAsync(table)).Table;
        while (tbl.TableStatus != TableStatus.ACTIVE)
        {
            Ensure(clock.Elapsed < TimeSpan.FromSeconds(deadlineS), $"{table} never became ACTIVE: {tbl.TableStatus?.Value}");
            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
            tbl = (await ddb.DescribeTableAsync(table)).Table;
        }

        return tbl;
    }
}
