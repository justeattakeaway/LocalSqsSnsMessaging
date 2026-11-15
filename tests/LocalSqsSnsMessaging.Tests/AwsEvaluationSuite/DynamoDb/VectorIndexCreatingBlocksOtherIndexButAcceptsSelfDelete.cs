// Translated from behavioral/dynamodb/check_vector_index_creating_blocks_other_index_but_accepts_self_delete.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.vector-index-creating-blocks-other-index-but-accepts-self-delete</summary>
[Category("AwsEvaluationSuite")]
public sealed class VectorIndexCreatingBlocksOtherIndexButAcceptsSelfDelete
{
    private const string Check = "vector-index-creating-blocks-other-index-but-accepts-self-delete";
    private const int NItems = 40;

    private static readonly double[] UnitX = [1.0, 0.0, 0.0, 0.0];

    /// <summary><c>[{"N": repr(float(x))} for x in values]</c>.</summary>
    private static List<AttributeValue> Vec(double[] values) =>
        [.. values.Select(x => N(x.ToString("0.0###############", CultureInfo.InvariantCulture)))];

    /// <summary>
    /// While a vector index is being created, UpdateTable adding a second vector index is refused
    /// with LimitExceededException; once the first leaves resource allocation, deleting it is accepted
    /// and the index disappears.
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.DynamoDbVectorSearch, "vector indexes are not emulated: UpdateTable ignores VectorIndexUpdates and answers that no update was given")]
    [DisplayName("dynamodb.vector-index-creating-blocks-other-index-but-accepts-self-delete")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-vlc-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(DeleteTableWhenSettled(ddb, table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        for (var i = 0; i < NItems; i++)
        {
            await ddb.PutItemAsync(new PutItemRequest
            {
                TableName = table,
                Item = Item(("pk", S($"item-{i.ToString("000", CultureInfo.InvariantCulture)}")), ("emb", new AttributeValue { L = Vec(UnitX) })),
            });
        }

        var scanned = (await ddb.ScanAsync(new ScanRequest { TableName = table, Select = Select.COUNT, ConsistentRead = true })).Count;
        Ensure(scanned == NItems, $"expected {NItems} items before adding the index, got {scanned}");

        var addResp = await ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = table,
            VectorIndexUpdates =
            [
                new VectorIndexUpdate
                {
                    Create = new CreateVectorIndexAction
                    {
                        IndexName = "vidx-a",
                        VectorAttribute = new VectorAttributeDefinition { AttributeName = "emb" },
                        Dimensions = 4,
                        DistanceFunction = VectorDistanceFunction.EUCLIDEAN,
                        Projection = new Projection { ProjectionType = ProjectionType.ALL },
                    },
                },
            ],
        });
        var added = (addResp.TableDescription.VectorIndexes ?? []).ToDictionary(v => v.IndexName, StringComparer.Ordinal);
        Ensure(
            added.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(["vidx-a"]),
            $"UpdateTable described indexes {Py.Repr(added.Keys.Order(StringComparer.Ordinal).ToList())}, expected only vidx-a");
        Ensure(
            added["vidx-a"].IndexStatus == IndexStatus.CREATING,
            $"a just-requested vector index must be CREATING, was {Py.Repr(added["vidx-a"].IndexStatus?.Value)}");

        // Immediately: a second UpdateTable touching a DIFFERENT vector index while vidx-a builds.
        var err = await Error(
            () => ddb.UpdateTableAsync(new UpdateTableRequest
            {
                TableName = table,
                VectorIndexUpdates =
                [
                    new VectorIndexUpdate
                    {
                        Create = new CreateVectorIndexAction
                        {
                            IndexName = "vidx-b",
                            VectorAttribute = new VectorAttributeDefinition { AttributeName = "emb" },
                            Dimensions = 4,
                            DistanceFunction = VectorDistanceFunction.COSINE,
                            Projection = new Projection { ProjectionType = ProjectionType.ALL },
                        },
                    },
                ],
            }),
            "UpdateTable adding vidx-b while vidx-a is building");
        Ensure(err.Code == "LimitExceededException", $"expected LimitExceededException, got {Py.Repr(err.Code)} ({Py.Repr(err.Message)})");
        const string expectedMsg =
            "Subscriber limit exceeded: Only 1 online index can be created or deleted "
            + "simultaneously per table";
        Ensure(err.Message == expectedMsg, $"expected message {Py.Repr(expectedMsg)}, got {Py.Repr(err.Message)}");
        var afterRefusal = (await ddb.DescribeTableAsync(table)).Table;
        var namesAfterRefusal = (afterRefusal.VectorIndexes ?? []).Select(v => v.IndexName).Order(StringComparer.Ordinal).ToList();
        Ensure(namesAfterRefusal.SequenceEqual(["vidx-a"]), $"a refused add must leave the index set unchanged, got {Py.Repr(namesAfterRefusal)}");

        // Deleting vidx-a is refused while it is in resource allocation, so wait until DescribeTable
        // reports it Backfilling or ACTIVE before the self-delete.
        var allocClock = FidelityTime.StartNew();
        while (true)
        {
            var vidx = ((await ddb.DescribeTableAsync(table)).Table.VectorIndexes ?? []).First(v => v.IndexName == "vidx-a");
            if (vidx.Backfilling == true || vidx.IndexStatus == IndexStatus.ACTIVE)
            {
                break;
            }

            Ensure(allocClock.Elapsed < TimeSpan.FromSeconds(600), $"vidx-a never left resource allocation: {vidx.IndexStatus?.Value}");
            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        var deleteResp = await ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = table,
            VectorIndexUpdates = [new VectorIndexUpdate { Delete = new DeleteVectorIndexAction { IndexName = "vidx-a" } }],
        });
        var deleted = (deleteResp.TableDescription.VectorIndexes ?? []).ToDictionary(v => v.IndexName, StringComparer.Ordinal);
        Ensure(
            deleted.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(["vidx-a"]),
            $"UpdateTable self-delete described indexes {Py.Repr(deleted.Keys.Order(StringComparer.Ordinal).ToList())}, expected only vidx-a");
        Ensure(
            deleted["vidx-a"].IndexStatus == IndexStatus.DELETING,
            $"a just-accepted self-delete must report vidx-a DELETING, got {Py.Repr(deleted["vidx-a"].IndexStatus?.Value)}");

        var clock = FidelityTime.StartNew();
        TableDescription? final = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(600))
        {
            var tbl = (await ddb.DescribeTableAsync(table)).Table;
            var vidxs = tbl.VectorIndexes ?? [];
            if (vidxs.Count == 0 && tbl.TableStatus == TableStatus.ACTIVE)
            {
                final = tbl;
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        Ensure(
            final is not null,
            $"vidx-a was never fully removed within 600s; last seen {Py.Repr((await ddb.DescribeTableAsync(table)).Table.VectorIndexes?.Select(v => v.IndexName).ToList())}");

        var codeAfter = await Error(
            () => ddb.SearchVectorsAsync(new SearchVectorsRequest
            {
                TableName = table, IndexName = "vidx-a", TopK = 3, SearchVector = Vec(UnitX),
            }),
            "SearchVectors against the now-deleted vidx-a");
        Ensure(codeAfter.Code == "ValidationException", $"expected ValidationException searching deleted vidx-a, got {Py.Repr(codeAfter.Code)}");
        const string expectedSearchMsg = "The table does not have the specified index: vidx-a";
        Ensure(codeAfter.Message == expectedSearchMsg, $"expected message {Py.Repr(expectedSearchMsg)}, got {Py.Repr(codeAfter.Message)}");

        var scannedAfter = (await ddb.ScanAsync(new ScanRequest { TableName = table, Select = Select.COUNT, ConsistentRead = true })).Count;
        Ensure(scannedAfter == NItems, $"the base table lost items across the index lifecycle: expected {NItems}, got {scannedAfter}");
    }

    /// <summary>The Error a rejected call raises; an unexpected success raises instead.</summary>
    private static async Task<AwsError> Error<T>(Func<Task<T>> fn, string what)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"{what}: the call was accepted, expected a refusal: {result}");
    }

    private static Func<Task> DeleteTableWhenSettled(IAmazonDynamoDB ddb, string table, double timeoutS = 600) => async () =>
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

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    };
}
