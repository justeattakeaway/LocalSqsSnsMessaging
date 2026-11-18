// Translated from behavioral/dynamodb/check_create_table_vector_index_validation_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.create-table-vector-index-validation-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateTableVectorIndexValidationRejected
{
    private const string CheckId = "create-table-vector-index-validation-rejected";

    /// <summary>
    /// CreateTable refuses a vector index that violates a documented per-table rule, and the
    /// table it would have created never exists: Dimensions above 4096, more than 5 vector indexes
    /// on one table, and two indexes on the same attribute at different Dimensions.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.create-table-vector-index-validation-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;

        // Positive control: a single, valid vector index is accepted and described back.
        var control = FidelityContext.UniqueName("sim2real-behav-ddbvecval-control-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = control,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            VectorIndexes = [Vi("idx", "emb", 16)],
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(control));
        var clock = FidelityTime.StartNew();
        var budget = TimeSpan.FromSeconds(BudgetSeconds(ctx, 240.0));
        var tbl = (await ddb.DescribeTableAsync(control)).Table;
        while (!(tbl.TableStatus == TableStatus.ACTIVE && tbl.VectorIndexes is { Count: > 0 }
                 && tbl.VectorIndexes[0].IndexStatus?.ToString() == "ACTIVE"))
        {
            Ensure(clock.Elapsed < budget, $"control table never became ACTIVE: {TableRepr(tbl)}");
            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
            tbl = (await ddb.DescribeTableAsync(control)).Table;
        }

        Ensure(
            tbl.VectorIndexes[0].Dimensions == 16,
            $"control table vector index described back with Dimensions "
            + $"{tbl.VectorIndexes[0].Dimensions}, expected 16");

        // Dimensions above the documented maximum of 4096.
        var overDims = FidelityContext.UniqueName("sim2real-behav-ddbvecval-overdim-");
        var (code, message) = await Error(() => ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = overDims,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            VectorIndexes = [Vi("idx", "emb", 4097)],
        }));
        Ensure(code == "ValidationException", $"over-max Dimensions gave {Py.Repr(code)}, not ValidationException");
        var expected = "One or more parameter values were invalid: Number of dimensions must be between 1 and 4096 inclusive.";
        Ensure(message == expected, $"over-max Dimensions message was {Py.Repr(message)}");
        await AssertNeverCreated(ddb, overDims);

        // More vector indexes than the documented per-table default of 5.
        var tooMany = FidelityContext.UniqueName("sim2real-behav-ddbvecval-toomany-");
        var sixIndexes = Enumerable.Range(0, 6).Select(i => Vi($"idx{i}", $"emb{i}", 8)).ToList();
        (code, message) = await Error(() => ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tooMany,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            VectorIndexes = sixIndexes,
        }));
        Ensure(code == "ValidationException", $"six vector indexes gave {Py.Repr(code)}, not ValidationException");
        expected = "One or more parameter values were invalid: VectorIndex count exceeds the per-table limit of 5";
        Ensure(message == expected, $"too-many-indexes message was {Py.Repr(message)}");
        await AssertNeverCreated(ddb, tooMany);

        // The same vector attribute indexed twice at different Dimensions.
        var conflict = FidelityContext.UniqueName("sim2real-behav-ddbvecval-conflict-");
        (code, message) = await Error(() => ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = conflict,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            VectorIndexes = [Vi("idx-a", "emb", 8), Vi("idx-b", "emb", 16, fn: "EUCLIDEAN")],
        }));
        Ensure(code == "ValidationException", $"conflicting Dimensions gave {Py.Repr(code)}, not ValidationException");
        expected = "One or more parameter values were invalid: Conflicting attribute definition for "
            + "'emb'. All VectorIndexes on the same vector attribute must use the same dimensions.";
        Ensure(message == expected, $"conflicting-dimensions message was {Py.Repr(message)}");
        await AssertNeverCreated(ddb, conflict);
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

    private static VectorIndex Vi(string name, string attr, int dims, string fn = "COSINE") => new()
    {
        IndexName = name,
        VectorAttribute = new VectorAttributeDefinition { AttributeName = attr },
        Dimensions = dims,
        DistanceFunction = fn,
        Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
    };

    private static double BudgetSeconds(FidelityContext ctx, double capSeconds) =>
        Math.Max(1.0, Math.Min(capSeconds, ctx.TimeLeftSeconds - 5.0));

    private static async Task AssertNeverCreated(IAmazonDynamoDB ddb, string table)
    {
        var (code, message) = await Error(() => ddb.DescribeTableAsync(table));
        Ensure(
            code == "ResourceNotFoundException",
            $"{table}: CreateTable was refused but DescribeTable returned {Py.Repr(code)}, not "
            + "ResourceNotFoundException -- the table exists");
        var expected = $"Requested resource not found: Table: {table} not found";
        Ensure(message == expected, $"{table}: unexpected DescribeTable message {Py.Repr(message)}");
    }

    /// <summary>A short stand-in for <c>repr(tbl)</c>: the fields this check looks at.</summary>
    private static string TableRepr(TableDescription tbl) => Py.Repr(new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["TableName"] = tbl.TableName,
        ["TableStatus"] = tbl.TableStatus?.Value,
        ["VectorIndexes"] = (tbl.VectorIndexes ?? []).Select(v => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["IndexName"] = v.IndexName,
            ["IndexStatus"] = v.IndexStatus?.ToString(),
        }).ToList(),
    });
}
