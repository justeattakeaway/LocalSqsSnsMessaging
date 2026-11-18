// Translated from behavioral/dynamodb/check_batch_get_item_duplicate_keys_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-get-item-duplicate-keys-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchGetItemDuplicateKeysRejected
{
    /// <summary>
    /// BatchGetItem rejects a request that specifies the same primary key more than once
    /// within a single table's Keys list with ValidationException, rather than silently
    /// deduplicating it.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batch-get-item-duplicate-keys-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx, ddb);
        await ddb.PutItemAsync(table, Item(("pk", S("dup-1")), ("val", S("v1"))));

        var (code, message) = await Error(() => ddb.BatchGetItemAsync(new BatchGetItemRequest
        {
            RequestItems = new()
            {
                [table] = new KeysAndAttributes { Keys = [Item(("pk", S("dup-1"))), Item(("pk", S("dup-1")))] },
            },
        }));
        Ensure(code == "ValidationException", $"expected ValidationException, got {Py.Repr(code)}");
        Ensure(message == "Provided list of item keys contains duplicates", $"expected the duplicate-keys refusal message, got {Py.Repr(message)}");

        // Control: the same call with the duplicate removed succeeds normally.
        var resp = await ddb.BatchGetItemAsync(new BatchGetItemRequest
        {
            RequestItems = new() { [table] = new KeysAndAttributes { Keys = [Item(("pk", S("dup-1")))] } },
        });
        var items = resp.Responses[table];
        Ensure(items.Count == 1, $"expected exactly 1 item for the deduplicated key, got {Py.Repr(items)}");
        Ensure(items[0]["pk"].S == "dup-1", $"expected pk 'dup-1' back, got {Py.Repr(items[0])}");
    }

    private static async Task<string> CreateTable(FidelityContext ctx, IAmazonDynamoDB ddb)
    {
        var name = FidelityContext.UniqueName("sim2real-behav-bgidup-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags =
            [
                new Tag { Key = "sim2real:managed", Value = "true" },
                new Tag { Key = "sim2real:prefix", Value = "sim2real-behav" },
            ],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await WaitTableActive(ddb, name);
        return name;
    }

    private static async Task WaitTableActive(IAmazonDynamoDB ddb, string name, double timeoutS = 60.0, double intervalS = 2.0)
    {
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            var status = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = name })).Table.TableStatus;
            if (status == TableStatus.ACTIVE)
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"table {name} never became ACTIVE");
    }

    /// <summary>
    /// Call fn(); return its botocore error (Code, Message). An unexpected SUCCESS raises, because
    /// an emulator ACCEPTING a request real AWS refuses is the strongest signal available.
    /// </summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"real AWS rejects this call, but it SUCCEEDED and returned {Py.Repr(result)}");
    }
}
