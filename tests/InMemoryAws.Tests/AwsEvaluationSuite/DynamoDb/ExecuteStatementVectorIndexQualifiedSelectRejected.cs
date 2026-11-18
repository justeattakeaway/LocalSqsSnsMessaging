// Translated from behavioral/dynamodb/check_execute_statement_vector_index_qualified_select_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.execute-statement-vector-index-qualified-select-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ExecuteStatementVectorIndexQualifiedSelectRejected
{
    private const string CheckId = "execute-statement-vector-index-qualified-select-rejected";
    private static readonly (string Code, string Message) NoVectorIndexQuery = ("ValidationException", "Scan operation not supported on this index type");

    /// <summary>
    /// ExecuteStatement rejects a SELECT qualified with a table's vector index; a SELECT on the
    /// base table returns the vector attribute as an ordinary list of numbers.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.execute-statement-vector-index-qualified-select-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-pqlvec-");
        const string indexName = "vec-idx";
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
                    IndexName = indexName,
                    VectorAttribute = new VectorAttributeDefinition { AttributeName = "embedding" },
                    Dimensions = 3,
                    DistanceFunction = "COSINE",
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));

        var clock = FidelityTime.StartNew();
        string? status = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(300))
        {
            var described = (await ddb.DescribeTableAsync(table)).Table;
            var vectorIndexes = described.VectorIndexes ?? [];
            status = vectorIndexes.Count > 0 ? vectorIndexes[0].IndexStatus?.Value : null;
            if (described.TableStatus == TableStatus.ACTIVE && status == "ACTIVE")
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        Ensure(status == "ACTIVE", $"vector index never became ACTIVE: {Py.Repr(status)}");

        await ddb.PutItemAsync(table, Item(("pk", S("p1")), ("embedding", L(N("1"), N("0"), N("0")))));

        // Positive control: a base-table SELECT returns the vector attribute untouched, as an
        // ordinary list of numbers -- PartiQL has no special handling for it at all.
        var @base = await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"SELECT * FROM \"{table}\" WHERE pk = 'p1'",
            ConsistentRead = true,
        });
        Ensure(
            Py.Equal(@base.Items, new[] { Item(("pk", S("p1")), ("embedding", L(N("1"), N("0"), N("0")))) }),
            $"base-table SELECT did not return the vector attribute as an ordinary list: {Py.Repr(@base.Items)}");

        // Break exactly one thing: qualify the FROM clause with the vector index itself.
        await ExpectError(
            () => ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"SELECT * FROM \"{table}\".\"{indexName}\"" }),
            NoVectorIndexQuery.Code,
            NoVectorIndexQuery.Message);
    }

    private static async Task ExpectError<T>(Func<Task<T>> fn, string code, string message)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be rejected, but it succeeded: {Py.Repr(result)}");
        }

        Ensure(error.Code == code, $"expected {Py.Repr(code)}, got {Py.Repr(error.Code)} ({Py.Repr(error.Message)})");
        Ensure(error.Message == message, $"unexpected message {Py.Repr(error.Message)}");
    }
}
