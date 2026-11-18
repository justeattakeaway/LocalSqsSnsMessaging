// Translated from behavioral/dynamodb/check_create_table_with_six_local_secondary_indexes_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.create-table-with-six-local-secondary-indexes-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateTableWithSixLocalSecondaryIndexesRejected
{
    private const string CheckId = "create-table-with-six-local-secondary-indexes-rejected";
    private const string ErrValidation = "ValidationException";

    /// <summary>
    /// CreateTable with 6 LocalSecondaryIndexes is rejected with its exact message; the same
    /// table with 5 is accepted -- the per-table LSI limit sits between those two counts.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.create-table-with-six-local-secondary-indexes-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var okName = FidelityContext.UniqueName("sim2real-behav-lsi5-");
        var badName = FidelityContext.UniqueName("sim2real-behav-lsi6-");
        ctx.OnCleanup(() => DeleteTableIfExists(ddb, okName));
        ctx.OnCleanup(() => DeleteTableIfExists(ddb, badName));

        // Positive control: 5 LSIs on a fresh base table (HASH+RANGE primary key, one range per
        // index) must be ACCEPTED -- the row's own evidence that 5 is inside the limit.
        var (okSk, okAd, okLsi) = LsiSet(5, "ok");
        var okAdFull = okAd.Append(new AttributeDefinition("sk", ScalarAttributeType.S)).ToList();
        var okKsFull = okSk.Append(new KeySchemaElement("sk", KeyType.RANGE)).ToList();
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = okName,
            KeySchema = okKsFull,
            AttributeDefinitions = okAdFull,
            LocalSecondaryIndexes = okLsi,
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        await ddb.WaitForTableExists(okName, delaySeconds: 2, maxAttempts: 250);
        var okDesc = (await ddb.DescribeTableAsync(okName)).Table;
        var okLsiNames = (okDesc.LocalSecondaryIndexes ?? []).Select(i => i.IndexName).ToHashSet(StringComparer.Ordinal);
        Ensure(
            okLsiNames.SetEquals(okLsi.Select(i => i.IndexName)),
            $"positive control did not create all 5 LSIs: {Py.Repr(okLsiNames.Order(StringComparer.Ordinal).ToList())}");

        var (badSk, badAd, badLsi) = LsiSet(6, "bad");
        var badAdFull = badAd.Append(new AttributeDefinition("sk", ScalarAttributeType.S)).ToList();
        var badKsFull = badSk.Append(new KeySchemaElement("sk", KeyType.RANGE)).ToList();
        var (code, message) = await Error(() => ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = badName,
            KeySchema = badKsFull,
            AttributeDefinitions = badAdFull,
            LocalSecondaryIndexes = badLsi,
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        }));
        Ensure(code == ErrValidation, $"expected {ErrValidation}, got {Py.Repr(code)}");
        const string expectedMsg = "One or more parameter values were invalid: Number of LocalSecondaryIndexes exceeds "
            + "per-table limit of 5";
        Ensure(message == expectedMsg, $"expected message {Py.Repr(expectedMsg)}, got {Py.Repr(message)}");

        // The rejected CreateTable must not have left a stray table behind.
        var (existsCode, existsMsg) = await Error(() => ddb.DescribeTableAsync(badName));
        Ensure(
            existsCode == "ResourceNotFoundException",
            $"the refused CreateTable left {Py.Repr(badName)} behind: DescribeTable gave {Py.Repr(existsCode)}");
        var expectedNf = $"Requested resource not found: Table: {badName} not found";
        Ensure(existsMsg == expectedNf, $"expected message {Py.Repr(expectedNf)}, got {Py.Repr(existsMsg)}");
    }

    /// <summary>Call fn(); return (code, message). An unexpected SUCCESS raises rather than returning a falsy sentinel.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected a ClientError, but the call SUCCEEDED and returned {result}");
        }

        return (error.Code, error.Message);
    }

    private static async Task DeleteTableIfExists(IAmazonDynamoDB ddb, string tableName)
    {
        var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(tableName));
        if (error is not null && error.Code != "ResourceNotFoundException")
        {
            throw error.Exception;
        }
    }

    private static (List<KeySchemaElement> KeySchema, List<AttributeDefinition> AttributeDefinitions, List<LocalSecondaryIndex> Indexes) LsiSet(int n, string prefix)
    {
        var keySchema = new List<KeySchemaElement> { new("pk", KeyType.HASH) };
        var attributeDefinitions = new List<AttributeDefinition> { new("pk", ScalarAttributeType.S) };
        var indexes = new List<LocalSecondaryIndex>();
        for (var i = 0; i < n; i++)
        {
            var sortAttr = $"{prefix}{i}";
            attributeDefinitions.Add(new AttributeDefinition(sortAttr, ScalarAttributeType.S));
            indexes.Add(new LocalSecondaryIndex
            {
                IndexName = $"{prefix}idx{i}",
                KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement(sortAttr, KeyType.RANGE)],
                Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
            });
        }

        return (keySchema, attributeDefinitions, indexes);
    }
}
