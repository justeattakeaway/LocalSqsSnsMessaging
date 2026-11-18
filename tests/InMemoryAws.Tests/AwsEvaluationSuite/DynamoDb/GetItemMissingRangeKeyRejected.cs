// Translated from behavioral/dynamodb/check_get_item_missing_range_key_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.get-item-missing-range-key-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class GetItemMissingRangeKeyRejected
{
    private const string CheckId = "get-item-missing-range-key-rejected";

    /// <summary>GetItem on a hash+range table supplying only the hash key must be rejected, not resolved to some item.</summary>
    [Test]
    [DisplayName("dynamodb.get-item-missing-range-key-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableName = FidelityContext.UniqueName("sim2real-behav-ddb-partialkey-");
        var (keySchema, attrDefs) = CompositeKeySchema("pk", "sk");
        await CreateTableAndWait(ddb, ctx, tableName, keySchema, attrDefs, tags: [new Tag { Key = "sim2real", Value = CheckId }]);

        await ddb.PutItemAsync(tableName, Item(("pk", S("p1")), ("sk", S("s1"))));

        var (code, message) = await ErrorCodeAndMessage(() => ddb.GetItemAsync(new GetItemRequest
        {
            TableName = tableName,
            Key = Item(("pk", S("p1"))),
            ConsistentRead = true,
        }));
        Ensure(code == "ValidationException", $"expected ValidationException, got {code}");
        const string expectedMessage = "The provided key element does not match the schema";
        Ensure(message == expectedMessage, $"expected message {Py.Repr(expectedMessage)}, got {Py.Repr(message)}");

        // A fully-specified key must still work, proving the table itself is healthy.
        var ok = await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = tableName,
            Key = Item(("pk", S("p1")), ("sk", S("s1"))),
            ConsistentRead = true,
        });
        Ensure(ok.Item is not null, $"expected Item in response for a fully-specified key, got {Py.Repr(ok.Item)}");
    }

    /// <summary>
    /// Call fn(); return the botocore Error.Code and Error.Message if a ClientError is raised. An
    /// unexpected SUCCESS raises, because an emulator ACCEPTING input real AWS refuses is the strongest
    /// signal available.
    /// </summary>
    private static async Task<(string? Code, string? Message)> ErrorCodeAndMessage<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"real AWS rejects this call, but it SUCCEEDED and returned {Py.Repr(result)}");
        }

        return (error.Code, error.Message);
    }

    /// <summary>Poll DescribeTable until TableStatus is ACTIVE (a transient 404 counts as not-yet).</summary>
    private static async Task<string?> WaitTableActive(IAmazonDynamoDB ddb, string tableName, double timeoutSeconds = 60.0, double intervalSeconds = 1.0)
    {
        var clock = FidelityTime.StartNew();
        string? status = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            var (resp, error) = await FidelityContext.Capture(() => ddb.DescribeTableAsync(tableName));
            if (error is not null)
            {
                if (error.Code != "ResourceNotFoundException")
                {
                    throw error.Exception;
                }

                status = "PENDING";
            }
            else
            {
                status = resp!.Table.TableStatus?.Value;
            }

            if (status == "ACTIVE")
            {
                return status;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"table {tableName} never went ACTIVE in {timeoutSeconds}s (last={status})");
    }

    /// <summary>Create a table, register teardown, and wait until ACTIVE.</summary>
    private static async Task<CreateTableResponse> CreateTableAndWait(
        IAmazonDynamoDB ddb,
        FidelityContext ctx,
        string tableName,
        List<KeySchemaElement> keySchema,
        List<AttributeDefinition> attributeDefinitions,
        string billingMode = "PAY_PER_REQUEST",
        double timeoutSeconds = 60.0,
        List<Tag>? tags = null)
    {
        var resp = await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            KeySchema = keySchema,
            AttributeDefinitions = attributeDefinitions,
            BillingMode = new BillingMode(billingMode),
            Tags = tags,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(tableName));
        await WaitTableActive(ddb, tableName, timeoutSeconds: timeoutSeconds);
        return resp;
    }

    /// <summary>Return (KeySchema, AttributeDefinitions) for a hash+range table.</summary>
    private static (List<KeySchemaElement> KeySchema, List<AttributeDefinition> AttributeDefinitions) CompositeKeySchema(
        string hashName = "pk", string rangeName = "sk", string hashType = "S", string rangeType = "S") =>
        (
            [new KeySchemaElement(hashName, KeyType.HASH), new KeySchemaElement(rangeName, KeyType.RANGE)],
            [new AttributeDefinition(hashName, new ScalarAttributeType(hashType)), new AttributeDefinition(rangeName, new ScalarAttributeType(rangeType))]
        );
}
