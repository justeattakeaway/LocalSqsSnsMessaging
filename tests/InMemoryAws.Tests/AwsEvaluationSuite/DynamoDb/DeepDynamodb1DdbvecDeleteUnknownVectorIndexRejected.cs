// Translated from behavioral/dynamodb/check_deep_dynamodb_1_ddbvec_delete_unknown_vector_index_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.deep-dynamodb-1-ddbvec-delete-unknown-vector-index-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeepDynamodb1DdbvecDeleteUnknownVectorIndexRejected
{
    private const string CheckId = "deep-dynamodb-1-ddbvec-delete-unknown-vector-index-rejected";
    private static readonly double[] Query = [1.0, 0.0, 0.0, 0.0];

    /// <summary>
    /// UpdateTable deleting a vector index the table lacks raises ResourceNotFoundException with the exact message
    /// and leaves both indexes ACTIVE; deleting a real index removes only it, and a search on it then raises
    /// ValidationException with the exact message.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.deep-dynamodb-1-ddbvec-delete-unknown-vector-index-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbvec-");

        static VectorIndex Vi(string name, string fn) => new()
        {
            IndexName = name,
            VectorAttribute = new VectorAttributeDefinition { AttributeName = "emb" },
            Dimensions = 4,
            DistanceFunction = fn,
            Projection = new Projection { ProjectionType = ProjectionType.ALL },
        };

        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            VectorIndexes = [Vi("idx-keep", "EUCLIDEAN"), Vi("idx-drop", "DOT_PRODUCT")],
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

        foreach (var idx in new[] { "idx-keep", "idx-drop" })
        {
            var got = new List<string>();
            var clock = FidelityTime.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(90))
            {
                got = await Search(idx);
                if (got.Count > 0)
                {
                    break;
                }

                await FidelityTime.Delay(TimeSpan.FromSeconds(1));
            }

            Ensure(got.SequenceEqual(["only"]), $"{idx} returned {Py.Repr(got)} before any delete");
        }

        var err = await Error(
            () => ddb.UpdateTableAsync(new UpdateTableRequest
            {
                TableName = table,
                VectorIndexUpdates = [new VectorIndexUpdate { Delete = new DeleteVectorIndexAction { IndexName = "idx-dropped" } }],
            }),
            "UpdateTable deleting a nonexistent vector index");
        Ensure(err.Code == "ResourceNotFoundException", $"expected ResourceNotFoundException, got {Py.Repr(ErrDict(err))}");
        var expectedMsg = $"Requested resource not found: Index idx-dropped for table {table}";
        Ensure(err.Message == expectedMsg, $"expected message {Py.Repr(expectedMsg)}, got {Py.Repr(err.Message)}");
        var tbl = (await ddb.DescribeTableAsync(table)).Table;
        var st = (tbl.VectorIndexes ?? [])
            .Select(v => (v.IndexName, Status: v.IndexStatus?.ToString()))
            .OrderBy(v => v.IndexName, StringComparer.Ordinal).ThenBy(v => v.Status, StringComparer.Ordinal)
            .ToList();
        Ensure(
            st.SequenceEqual([("idx-drop", (string?)"ACTIVE"), ("idx-keep", "ACTIVE")]),
            $"after the refusal: {Py.Repr(st)}");

        // Positive control: deleting a real index removes exactly that one.
        await ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = table,
            VectorIndexUpdates = [new VectorIndexUpdate { Delete = new DeleteVectorIndexAction { IndexName = "idx-drop" } }],
        });
        List<string>? names = null;
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(240))
        {
            tbl = (await ddb.DescribeTableAsync(table)).Table;
            names = (tbl.VectorIndexes ?? []).Select(v => v.IndexName).Order(StringComparer.Ordinal).ToList();
            if (names.SequenceEqual(["idx-keep"]) && tbl.TableStatus == TableStatus.ACTIVE)
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        Ensure(names is not null && names.SequenceEqual(["idx-keep"]), $"after deleting idx-drop the table has {Py.Repr(names)}");
        Ensure((await Search("idx-keep")).SequenceEqual(["only"]), "the sibling index stopped answering after the delete");
        err = await Error(() => Search("idx-drop"), "SearchVectors on the deleted index");
        Ensure(
            err.Code == "ValidationException",
            $"search on the deleted index: expected ValidationException, got {Py.Repr(ErrDict(err))}");
        expectedMsg = "The table does not have the specified index: idx-drop";
        Ensure(
            err.Message == expectedMsg,
            $"search on the deleted index: expected message {Py.Repr(expectedMsg)}, got {Py.Repr(err.Message)}");
    }

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
            "CreateTable accepted VectorIndexes but DescribeTable reports none - the vector indexes "
            + "were silently dropped");
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
            $"table/vector indexes never became ACTIVE: {Py.Repr(tbl.TableStatus?.Value)} "
            + $"{Py.Repr((tbl.VectorIndexes ?? []).Select(v => (v.IndexName, v.IndexStatus?.ToString())).ToList())}");
    }
}
