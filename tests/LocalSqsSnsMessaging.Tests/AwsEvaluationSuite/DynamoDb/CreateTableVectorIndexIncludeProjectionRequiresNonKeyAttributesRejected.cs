// Translated from behavioral/dynamodb/check_create_table_vector_index_include_projection_requires_non_key_attributes_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.create-table-vector-index-include-projection-requires-non-key-attributes-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateTableVectorIndexIncludeProjectionRequiresNonKeyAttributesRejected
{
    private const string CheckId = "create-table-vector-index-include-projection-requires-non-key-attributes-rejected";

    /// <summary>
    /// CreateTable refuses a vector index with Projection INCLUDE and no NonKeyAttributes, and the
    /// table it would have created never exists.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.create-table-vector-index-include-projection-requires-non-key-attributes-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;

        // Positive control: INCLUDE with a non-empty NonKeyAttributes is accepted and described back.
        var control = FidelityContext.UniqueName("sim2real-behav-ddbvecincl-control-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = control,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            VectorIndexes = [Vi("idx", "emb", 16, new Projection { ProjectionType = ProjectionType.INCLUDE, NonKeyAttributes = ["extra"] })],
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(control));
        var tbl = await WaitVectorTableActive(ctx, ddb, control);
        var described = ProjectionDict(tbl.VectorIndexes[0].Projection);
        Ensure(
            Py.Equal(described, ProjectionDict(new Projection { ProjectionType = ProjectionType.INCLUDE, NonKeyAttributes = ["extra"] })),
            $"control table described back {Py.Repr(described)}");

        // INCLUDE with no NonKeyAttributes at all.
        var missing = FidelityContext.UniqueName("sim2real-behav-ddbvecincl-missing-");
        var (code, message) = await Error(() => ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = missing,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            VectorIndexes = [Vi("idx", "emb", 16, new Projection { ProjectionType = ProjectionType.INCLUDE })],
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        }));
        Ensure(code == "ValidationException", $"INCLUDE with no NonKeyAttributes: expected ValidationException, got {Py.Repr(code)}");
        const string expectedMessage = "One or more parameter values were invalid: ProjectionType is INCLUDE, but "
            + "NonKeyAttributes is not specified";
        Ensure(message == expectedMessage, $"INCLUDE with no NonKeyAttributes: unexpected message {Py.Repr(message)}");
        var (code2, message2) = await Error(() => ddb.DescribeTableAsync(missing));
        Ensure(
            code2 == "ResourceNotFoundException",
            $"{missing}: CreateTable was refused but DescribeTable returned {Py.Repr(code2)}, not "
            + "ResourceNotFoundException -- the table exists");
        var expectedNotFound = $"Requested resource not found: Table: {missing} not found";
        Ensure(message2 == expectedNotFound, $"{missing}: unexpected DescribeTable message {Py.Repr(message2)}");
    }

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

    private static VectorIndex Vi(string name, string attr, int dims, Projection projection) => new()
    {
        IndexName = name,
        VectorAttribute = new VectorAttributeDefinition { AttributeName = attr },
        Dimensions = dims,
        DistanceFunction = "COSINE",
        Projection = projection,
    };

    private static Dictionary<string, object?> ProjectionDict(Projection? projection)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (projection?.ProjectionType is not null)
        {
            dict["ProjectionType"] = projection.ProjectionType.Value;
        }

        if (projection?.NonKeyAttributes is not null)
        {
            dict["NonKeyAttributes"] = projection.NonKeyAttributes;
        }

        return dict;
    }

    private static double BudgetSeconds(FidelityContext ctx, double capSeconds) =>
        Math.Max(1.0, Math.Min(capSeconds, ctx.TimeLeftSeconds - 5.0));

    private static async Task<TableDescription> WaitVectorTableActive(FidelityContext ctx, IAmazonDynamoDB ddb, string table, double capSeconds = 240.0)
    {
        var clock = FidelityTime.StartNew();
        var budget = TimeSpan.FromSeconds(BudgetSeconds(ctx, capSeconds));
        _ = (await ddb.DescribeTableAsync(table)).Table;
        while (true)
        {
            var tbl = (await ddb.DescribeTableAsync(table)).Table;
            var indexes = tbl.VectorIndexes ?? [];
            if (tbl.TableStatus == TableStatus.ACTIVE && indexes.Count > 0
                && indexes.All(v => v.IndexStatus?.ToString() == "ACTIVE"))
            {
                return tbl;
            }

            if (clock.Elapsed >= budget)
            {
                var states = indexes.Select(v => (v.IndexName, v.IndexStatus?.ToString())).ToList();
                throw Fail(
                    $"table/vector index never became ACTIVE in time: status "
                    + $"{Py.Repr(tbl.TableStatus?.Value)}, indexes "
                    + $"{Py.Repr(states)}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    }
}
