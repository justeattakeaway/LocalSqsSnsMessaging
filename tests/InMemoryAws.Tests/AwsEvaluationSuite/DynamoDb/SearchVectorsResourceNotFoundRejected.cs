// Translated from behavioral/dynamodb/check_search_vectors_resource_not_found_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.search-vectors-resource-not-found-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class SearchVectorsResourceNotFoundRejected
{
    /// <summary>
    /// SearchVectors with a nonexistent index on an existing table returns ValidationException
    /// naming the index, and the table's vector index list never gains that name.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.search-vectors-resource-not-found-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableName = FidelityContext.UniqueName("sim2real-novecidx-");
        const string bogusIndex = "nonexistent-vector-index";
        List<AttributeValue> SearchVector() =>
            [.. Enumerable.Range(0, 8).Select(i => N(Py.Repr(i / 10.0)))];

        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(tableName));
        await ddb.WaitForTableExists(tableName, delaySeconds: 2, maxAttempts: 250);

        Task Search() => ddb.SearchVectorsAsync(new SearchVectorsRequest
        {
            TableName = tableName,
            IndexName = bogusIndex,
            SearchVector = SearchVector(),
            TopK = 5,
        });

        var (code, message) = await ErrorInfo(Search);
        Ensure(code == "ValidationException", $"expected ValidationException, got {code}: {message}");
        var expected = $"The table does not have the specified index: {bogusIndex}";
        Ensure(message == expected, $"expected the message naming the bogus index, got: {Py.Repr(message)}");

        var described = (await ddb.DescribeTableAsync(tableName)).Table;
        Ensure(described.TableStatus == TableStatus.ACTIVE, $"table went missing/inactive: {described}");
        var existingIndexNames = (described.VectorIndexes ?? []).Select(vi => vi.IndexName).ToList();
        Ensure(
            !existingIndexNames.Contains(bogusIndex),
            $"{bogusIndex} should never have been created on {tableName}, got indexes {Py.Repr(existingIndexNames)}");
    }

    private static async Task<(string? Code, string Message)> ErrorInfo(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is null)
        {
            throw Fail("expected search_vectors to raise, but it succeeded");
        }

        return (error.Code, error.Message ?? "");
    }
}
