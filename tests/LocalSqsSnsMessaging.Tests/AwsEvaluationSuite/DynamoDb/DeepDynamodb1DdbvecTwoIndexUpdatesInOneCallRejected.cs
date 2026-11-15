// Translated from behavioral/dynamodb/check_deep_dynamodb_1_ddbvec_two_index_updates_in_one_call_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.deep-dynamodb-1-ddbvec-two-index-updates-in-one-call-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeepDynamodb1DdbvecTwoIndexUpdatesInOneCallRejected
{
    private const string CheckId = "deep-dynamodb-1-ddbvec-two-index-updates-in-one-call-rejected";
    private static readonly double[] Query = [1.0, 0.0, 0.0, 0.0];

    private static VectorIndexUpdate Create(string name) => new()
    {
        Create = new CreateVectorIndexAction
        {
            IndexName = name,
            VectorAttribute = new VectorAttributeDefinition { AttributeName = "emb" },
            Dimensions = 4,
            DistanceFunction = "COSINE",
            Projection = new Projection { ProjectionType = ProjectionType.ALL },
        },
    };

    /// <summary>
    /// UpdateTable takes ONE vector-index create or delete per call; two are LimitExceededException.
    /// The refused call leaves the index set unchanged; a single Delete is applied, after which a
    /// search on that index is a ValidationException.
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.DynamoDbVectorSearch, "CreateTable accepts VectorIndexes but drops them: DescribeTable reports none")]
    [DisplayName("dynamodb.deep-dynamodb-1-ddbvec-two-index-updates-in-one-call-rejected")]
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
            VectorIndexes =
            [
                new VectorIndex
                {
                    IndexName = "idx-orig",
                    VectorAttribute = new VectorAttributeDefinition { AttributeName = "emb" },
                    Dimensions = 4,
                    DistanceFunction = "COSINE",
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });

        // DeleteTable is refused while a vector index is being created or deleted.
        ctx.OnCleanup(() => DeleteTableWhenSettled(ddb, table));
        await WaitVectorTableActive(ddb, table);
        await ddb.PutItemAsync(table, Item(("pk", S("only")), ("emb", new AttributeValue { L = Vec(Query) })));

        async Task<List<string>> Search(string idx) =>
            ((await ddb.SearchVectorsAsync(new SearchVectorsRequest
            {
                TableName = table,
                IndexName = idx,
                TopK = 3,
                SearchVector = Vec(Query),
            })).SearchResults ?? []).Select(r => r.Item["pk"].S).ToList();

        var got = new List<string>();
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(90))
        {
            got = await Search("idx-orig");
            if (got.Count > 0)
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }

        Ensure(got.SequenceEqual(["only"]), $"idx-orig returned {Py.Repr(got)} before the updates");
        var attempts = new (string What, List<VectorIndexUpdate> Updates)[]
        {
            ("two Creates", [Create("idx-new1"), Create("idx-new2")]),
            ("Create + Delete", [Create("idx-new1"), new VectorIndexUpdate { Delete = new DeleteVectorIndexAction { IndexName = "idx-orig" } }]),
        };
        foreach (var (what, updates) in attempts)
        {
            var err = await Error(
                () => ddb.UpdateTableAsync(new UpdateTableRequest { TableName = table, VectorIndexUpdates = updates }),
                $"UpdateTable with {what}");
            Ensure(err.Code == "LimitExceededException", $"{what}: expected LimitExceededException, got {Py.Repr(ErrDict(err))}");
            const string expectedMessage = "Subscriber limit exceeded: Only 1 online index can be created or "
                + "deleted simultaneously per table";
            Ensure(err.Message == expectedMessage, $"{what}: expected message {Py.Repr(expectedMessage)}, got {Py.Repr(err.Message)}");
            var described = (await ddb.DescribeTableAsync(table)).Table;
            var names = (described.VectorIndexes ?? []).Select(v => v.IndexName).Order(StringComparer.Ordinal).ToList();
            Ensure(names.SequenceEqual(["idx-orig"]), $"{what}: refused update still changed the indexes: {Py.Repr(names)}");
            Ensure(
                described.VectorIndexes![0].IndexStatus?.ToString() == "ACTIVE",
                $"{what}: idx-orig left ACTIVE: {Py.Repr(IndexDict(described.VectorIndexes![0]))}");
            Ensure((await Search("idx-orig")).SequenceEqual(["only"]), $"{what}: idx-orig stopped answering");
        }

        // Positive control: the one-element form is applied.
        var resp = await ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = table,
            VectorIndexUpdates = [new VectorIndexUpdate { Delete = new DeleteVectorIndexAction { IndexName = "idx-orig" } }],
        });
        var st = (resp.TableDescription.VectorIndexes ?? []).Select(v => (v.IndexName, Status: v.IndexStatus?.ToString())).ToList();
        Ensure(st.SequenceEqual([("idx-orig", (string?)"DELETING")]), $"single Delete described {Py.Repr(st)}");
        var gone = false;
        TableDescription? tbl = null;
        clock = FidelityTime.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(240))
        {
            tbl = (await ddb.DescribeTableAsync(table)).Table;
            if (tbl.VectorIndexes is not { Count: > 0 } && tbl.TableStatus == TableStatus.ACTIVE)
            {
                gone = true;
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        Ensure(gone, $"idx-orig was never removed: {Py.Repr(tbl?.VectorIndexes?.Select(IndexDict).ToList())}");
        var searchErr = await Error(() => Search("idx-orig"), "SearchVectors on the deleted index");
        Ensure(
            searchErr.Code == "ValidationException",
            $"search on the deleted index: expected ValidationException, got {Py.Repr(ErrDict(searchErr))}");
        const string expectedMsg = "The table does not have the specified index: idx-orig";
        Ensure(
            searchErr.Message == expectedMsg,
            $"search on the deleted index: expected message {Py.Repr(expectedMsg)}, got {Py.Repr(searchErr.Message)}");
    }

    private static Dictionary<string, object?> IndexDict(VectorIndexDescription v) => new(StringComparer.Ordinal)
    {
        ["IndexName"] = v.IndexName,
        ["IndexStatus"] = v.IndexStatus?.ToString(),
    };
    /// <summary><c>[{'N': repr(float(x))} for x in values]</c>.</summary>
    private static List<AttributeValue> Vec(IEnumerable<double> values) => values.Select(x => N(PyFloat(x))).ToList();

    private static string PyFloat(double x)
    {
        var text = x.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.', StringComparison.Ordinal) || text.Contains('E', StringComparison.Ordinal) ? text : text + ".0";
    }

    private static Dictionary<string, object?> ErrDict(AwsError err) => new(StringComparer.Ordinal) { ["Code"] = err.Code, ["Message"] = err.Message };

    private static async Task<AwsError> Error<T>(Func<Task<T>> call, string what)
    {
        try
        {
            await call();
        }
        catch (AmazonServiceException ex)
        {
            return AwsError.From(ex);
        }
        catch (Exception ex)
        {
            throw new FidelityAssertionException($"{what}: raised a non-service error {ex}", ex);
        }

        throw Fail($"{what}: the call was ACCEPTED; real AWS rejects it");
    }

    private static async Task DeleteTableWhenSettled(IAmazonDynamoDB ddb, string table, int timeoutSeconds = 1500)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(table));
            if (error is null || error.Code == "ResourceNotFoundException")
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed > TimeSpan.FromSeconds(timeoutSeconds))
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task<TableDescription> WaitVectorTableActive(IAmazonDynamoDB ddb, string table, int timeoutSeconds = 240)
    {
        var clock = FidelityTime.StartNew();
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        var tbl = (await ddb.DescribeTableAsync(table)).Table;
        Ensure(
            tbl.VectorIndexes is { Count: > 0 },
            "CreateTable accepted VectorIndexes but DescribeTable reports none - the vector index "
            + "was silently dropped");
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            tbl = (await ddb.DescribeTableAsync(table)).Table;
            if (tbl.TableStatus == TableStatus.ACTIVE && (tbl.VectorIndexes ?? []).All(v => v.IndexStatus?.ToString() == "ACTIVE"))
            {
                return tbl;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail(
            $"table/vector index never became ACTIVE: {Py.Repr(tbl.TableStatus?.Value)} "
            + $"{Py.Repr((tbl.VectorIndexes ?? []).Select(v => (v.IndexName, v.IndexStatus?.ToString())).ToList())}");
    }
}
