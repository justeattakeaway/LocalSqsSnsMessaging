// Translated from behavioral/dynamodb/check_transact_write_items_client_token_idempotent_mismatch_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-items-client-token-idempotent-mismatch-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteItemsClientTokenIdempotentMismatchRejected
{
    private const string Check = "transact-write-items-client-token-idempotent-mismatch-rejected";

    /// <summary>
    /// Reusing a TransactWriteItems ClientRequestToken with different item content raises
    /// IdempotentParameterMismatchException with the exact message and leaves the first write's value v1.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-items-client-token-idempotent-mismatch-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableName = FidelityContext.UniqueName("sim2real-behav-ddb-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => SafeDeleteTable(ddb, tableName));
        await WaitTableActive(ddb, tableName);

        var token = Guid.NewGuid().ToString();

        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem { Put = new Put { TableName = tableName, Item = Item(("pk", S("idem-item")), ("val", S("v1"))) } },
            ],
            ClientRequestToken = token,
        });
        var got = await ddb.GetItemAsync(new GetItemRequest { TableName = tableName, Key = Item(("pk", S("idem-item"))) });
        var val = got.Item?.GetValueOrDefault("val")?.S;
        Ensure(val == "v1", $"positive control: expected initial write to land as 'v1', got {Py.Repr(val)}");

        // Same token, different item content -> must be a mismatch, not applied.
        var (code, message) = await Error(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem { Put = new Put { TableName = tableName, Item = Item(("pk", S("idem-item")), ("val", S("v2-DIFFERENT"))) } },
            ],
            ClientRequestToken = token,
        }));
        Ensure(code == "IdempotentParameterMismatchException", $"expected IdempotentParameterMismatchException, got {Py.Repr(code)}");
        const string expected = "Specified idempotent token was used with different request parameters within the idempotency window";
        Ensure(message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(message)}");

        var still = await ddb.GetItemAsync(new GetItemRequest { TableName = tableName, Key = Item(("pk", S("idem-item"))) });
        var stillVal = still.Item?.GetValueOrDefault("val")?.S;
        Ensure(stillVal == "v1", $"rejected mismatch must leave the original item untouched, got val={Py.Repr(stillVal)}");
    }

    /// <summary>
    /// Call fn(); return its (code, message). An unexpected SUCCESS raises, because an emulator
    /// ACCEPTING a mismatched replay real AWS refuses is the strongest signal available.
    /// </summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected IdempotentParameterMismatchException, but the call SUCCEEDED and returned {result}");
        }

        return (error.Code, error.Message);
    }

    private static async Task<TableDescription> WaitTableActive(IAmazonDynamoDB ddb, string tableName, double timeoutS = 120.0, double intervalS = 2.0)
    {
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var desc = (await ddb.DescribeTableAsync(tableName)).Table;
            if (desc.TableStatus == TableStatus.ACTIVE)
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"table {tableName} never became ACTIVE");
    }

    private static async Task SafeDeleteTable(IAmazonDynamoDB ddb, string tableName)
    {
        try
        {
            await ddb.DeleteTableAsync(tableName);
        }
        catch (ResourceNotFoundException)
        {
        }
    }
}
