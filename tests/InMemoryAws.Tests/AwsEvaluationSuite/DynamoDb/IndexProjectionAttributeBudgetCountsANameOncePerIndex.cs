// Translated from behavioral/dynamodb/check_index_projection_attribute_budget_counts_a_name_once_per_index.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.index-projection-attribute-budget-counts-a-name-once-per-index</summary>
[Category("AwsEvaluationSuite")]
public sealed class IndexProjectionAttributeBudgetCountsANameOncePerIndex
{
    /// <summary>The Error dict from a call that MUST be refused.</summary>
    private static async Task<AwsError> Error(Func<Task> call, string what)
    {
        var error = await FidelityContext.CaptureError(call);
        return error ?? throw Fail($"{what}: the call was ACCEPTED; real AWS rejects it");
    }

    /// <summary>
    /// The 100-attribute projection budget is shared across ALL secondary indexes, and the same
    /// attribute name projected into two indexes counts TWICE. A target counting distinct names
    /// accepts a table real AWS refuses.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.index-projection-attribute-budget-counts-a-name-once-per-index")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        // 20 is the per-index NonKeyAttributes ceiling, so each index stays legal on its own; six of
        // them projecting the SAME 20 names spend 120 of the 100-attribute table-wide budget.
        var names = Enumerable.Range(0, 20).Select(i => $"a{i}").ToList();
        const int nIndexes = 6;
        var err = await Error(
            () => ddb.CreateTableAsync(new CreateTableRequest
            {
                TableName = table,
                KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
                AttributeDefinitions =
                [
                    new AttributeDefinition("pk", ScalarAttributeType.S),
                    .. Enumerable.Range(0, nIndexes).Select(i => new AttributeDefinition($"g{i}", ScalarAttributeType.S)),
                ],
                GlobalSecondaryIndexes =
                [
                    .. Enumerable.Range(0, nIndexes).Select(i => new GlobalSecondaryIndex
                    {
                        IndexName = $"idx{i}",
                        KeySchema = [new KeySchemaElement($"g{i}", KeyType.HASH)],
                        Projection = new Projection { ProjectionType = ProjectionType.INCLUDE, NonKeyAttributes = [.. names] },
                    }),
                ],
                BillingMode = BillingMode.PAY_PER_REQUEST,
            }),
            "six indexes each projecting the same 20 attributes (120 against a 100 budget)");
        Ensure(err.Code == "ValidationException", $"expected ValidationException, got {err.Code}");
        var expected = "One or more parameter values were invalid: Number of projected attributes in all " +
            $"indexes exceeds limit of 100, number of projected attributes:{names.Count * nIndexes}";
        Ensure(err.Message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(err.Message)}");
        Ensure(!(await ddb.ListTablesAsync()).TableNames.Contains(table), "the refused table must not exist");
    }
}
