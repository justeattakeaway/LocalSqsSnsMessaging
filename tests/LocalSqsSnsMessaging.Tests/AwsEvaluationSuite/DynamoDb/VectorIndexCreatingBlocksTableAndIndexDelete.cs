// Translated from behavioral/dynamodb/check_vector_index_creating_blocks_table_and_index_delete.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.vector-index-creating-blocks-table-and-index-delete</summary>
[Category("AwsEvaluationSuite")]
public sealed class VectorIndexCreatingBlocksTableAndIndexDelete
{
    private const string Check = "vector-index-creating-blocks-table-and-index-delete";
    private const string Index = "late-euc";

    /// <summary>
    /// AWS has been seen taking several minutes to settle a single-item vector index; the ceiling is
    /// generous on purpose and never asserted as a duration.
    /// </summary>
    private const double SettleBudgetS = 1500;

    private static readonly double[] Zero4 = [0.0, 0.0, 0.0, 0.0];

    /// <summary>
    /// Right after UpdateTable adds a vector index, DeleteTable and an UpdateTable deleting that
    /// index both raise ResourceInUseException with the exact message; once the index settles ACTIVE,
    /// DescribeTable no longer carries a Backfilling key, and DeleteTable then succeeds.
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.DynamoDbVectorSearch, "vector indexes are not emulated: UpdateTable ignores VectorIndexUpdates and answers that no update was given")]
    [DisplayName("dynamodb.vector-index-creating-blocks-table-and-index-delete")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbvec-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        // DeleteTable is refused while a vector index is being created, so a plain delete registered
        // here would leak the table whenever the check fails mid-allocation.
        ctx.OnCleanup(() => DeleteTableWhenSettled(ddb, table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = Item(("pk", S("only")), ("emb", new AttributeValue { L = Vec(Zero4) })) });

        var resp = await ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = table,
            VectorIndexUpdates =
            [
                new VectorIndexUpdate
                {
                    Create = new CreateVectorIndexAction
                    {
                        IndexName = Index,
                        VectorAttribute = new VectorAttributeDefinition { AttributeName = "emb" },
                        Dimensions = 4,
                        DistanceFunction = VectorDistanceFunction.EUCLIDEAN,
                        Projection = new Projection { ProjectionType = ProjectionType.ALL },
                    },
                },
            ],
        });
        var created = (resp.TableDescription.VectorIndexes ?? []).First(v => v.IndexName == Index);
        Ensure(
            created.IndexStatus == IndexStatus.CREATING,
            $"a just-requested vector index must be CREATING, UpdateTable described {Describe(created)}");

        // Assert the refusals right away, with no wait: the allocation window itself is never pinned.
        var deleteTableErr = await Error(() => ddb.DeleteTableAsync(table), "DeleteTable");
        Ensure(
            deleteTableErr.Code == "ResourceInUseException",
            $"DeleteTable while the vector index is CREATING: expected ResourceInUseException, got {deleteTableErr}");
        const string deleteTableMsg =
            "Attempt to change a resource which is still in use: Cannot delete table while "
            + "indexes are being created, updated, or deleted.";
        Ensure(
            deleteTableErr.Message == deleteTableMsg,
            $"DeleteTable while CREATING: expected message {Py.Repr(deleteTableMsg)}, got {Py.Repr(deleteTableErr.Message)}");

        var deleteIndexErr = await Error(
            () => ddb.UpdateTableAsync(new UpdateTableRequest
            {
                TableName = table,
                VectorIndexUpdates = [new VectorIndexUpdate { Delete = new DeleteVectorIndexAction { IndexName = Index } }],
            }),
            "UpdateTable deleting the still-CREATING vector index");
        Ensure(
            deleteIndexErr.Code == "ResourceInUseException",
            $"deleting the vector index while it is CREATING: expected ResourceInUseException, got {deleteIndexErr}");
        var deleteIndexMsg =
            "Attempt to change a resource which is still in use: Index creation is in resource "
            + "allocation phase. Retry deletion during backfilling phase or when the index is active. "
            + $"Table: {table} Index: {Index}";
        Ensure(
            deleteIndexErr.Message == deleteIndexMsg,
            $"deleting the vector index while CREATING: expected message {Py.Repr(deleteIndexMsg)}, got {Py.Repr(deleteIndexErr.Message)}");

        // Both refusals must have left the table and its index untouched.
        var afterRefusals = (await ddb.DescribeTableAsync(table)).Table;
        var names = (afterRefusals.VectorIndexes ?? []).Select(v => v.IndexName).ToList();
        Ensure(names.SequenceEqual([Index]), $"the refused deletes must leave exactly the vector index {Py.Repr(Index)}, got {Py.Repr(names)}");

        var final = await PollToActive(ddb, table);
        Ensure(final.Backfilling is null, $"an ACTIVE vector index must not carry a Backfilling key, got {Describe(final)}");

        // Positive control: once ACTIVE, DeleteTable succeeds normally.
        await ddb.DeleteTableAsync(table);
        await WaitTableDeleted(ddb, table);
        var gone = await Error(() => ddb.DescribeTableAsync(table), "DescribeTable after delete");
        Ensure(gone.Code == "ResourceNotFoundException", gone.ToString());
        var goneMsg = $"Requested resource not found: Table: {table} not found";
        Ensure(gone.Message == goneMsg, $"expected message {Py.Repr(goneMsg)}, got {Py.Repr(gone.Message)}");
    }

    /// <summary><c>[{"N": repr(float(x))} for x in values]</c>.</summary>
    private static List<AttributeValue> Vec(double[] values) =>
        [.. values.Select(x => N(x.ToString("0.0###############", CultureInfo.InvariantCulture)))];

    private static string Describe(VectorIndexDescription v) =>
        $"{{'IndexName': {Py.Repr(v.IndexName)}, 'IndexStatus': {Py.Repr(v.IndexStatus?.Value)}, 'Backfilling': {Py.Repr(v.Backfilling)}}}";

    /// <summary>The service error <paramref name="call"/> raises; an unexpected success raises.</summary>
    private static async Task<AwsError> Error<T>(Func<Task<T>> call, string what)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(call);
        return error ?? throw Fail($"{what}: the call was accepted, expected a refusal; got {result}");
    }

    /// <summary>Poll DescribeTable to the vector index's terminal ACTIVE state and return its description.</summary>
    private static async Task<VectorIndexDescription> PollToActive(IAmazonDynamoDB ddb, string table, double timeoutS = SettleBudgetS)
    {
        var clock = FidelityTime.StartNew();
        VectorIndexDescription? last = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var tbl = (await ddb.DescribeTableAsync(table)).Table;
            var cur = (tbl.VectorIndexes ?? []).Where(v => v.IndexName == Index).ToList();
            Ensure(cur.Count > 0, $"the vector index vanished while being created: {Py.Repr(tbl.VectorIndexes?.Select(v => v.IndexName).ToList())}");
            last = cur[0];
            if (last.IndexStatus == IndexStatus.ACTIVE && tbl.TableStatus == TableStatus.ACTIVE)
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"the vector index never became ACTIVE within {timeoutS}s: {(last is null ? "None" : Describe(last))}");
    }

    private static async Task WaitTableDeleted(IAmazonDynamoDB ddb, string table, double timeoutS = 90)
    {
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var error = await FidelityContext.CaptureError(() => ddb.DescribeTableAsync(table));
            if (error is not null)
            {
                if (error.Code == "ResourceNotFoundException")
                {
                    return;
                }

                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }

        throw Fail($"table {table} was not deleted within {timeoutS}s");
    }

    private static async Task DeleteTableWhenSettled(IAmazonDynamoDB ddb, string table, double timeoutS = SettleBudgetS)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(table));
            if (error is null || error.Code == "ResourceNotFoundException")
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed > TimeSpan.FromSeconds(timeoutS))
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(5));
        }
    }
}
