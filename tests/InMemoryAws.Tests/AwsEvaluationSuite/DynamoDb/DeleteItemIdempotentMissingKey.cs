// Translated from behavioral/dynamodb/check_delete_item_idempotent_missing_key.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.delete-item-idempotent-missing-key</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteItemIdempotentMissingKey
{
    private const string Check = "delete-item-idempotent-missing-key";

    /// <summary>
    /// Poll DescribeTable until ACTIVE. CreateTable is eventually consistent, so a
    /// transient ResourceNotFoundException counts as "not yet", never a failure.
    /// </summary>
    private static async Task<string> WaitForTableActive(IAmazonDynamoDB ddb, string tableName, double timeoutSeconds = 90.0, double intervalSeconds = 1.0)
    {
        var clock = FidelityTime.StartNew();
        string? status = null;
        while (clock.Elapsed.TotalSeconds < timeoutSeconds)
        {
            var (resp, error) = await FidelityContext.Capture(() => ddb.DescribeTableAsync(new DescribeTableRequest { TableName = tableName }));
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
                status = resp!.Table.TableStatus.Value;
            }

            if (status == "ACTIVE")
            {
                return status;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"table {tableName} never went ACTIVE in {timeoutSeconds}s (last={status ?? "None"})");
    }

    /// <summary>Create a pk (S) + sk (S) composite-key table, register teardown, wait ACTIVE.</summary>
    private static async Task<string> CreateSimpleTable(IAmazonDynamoDB ddb, FidelityContext ctx)
    {
        var tableName = FidelityContext.UniqueName("sim2real-behav-ddb-tbl-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(tableName));
        await WaitForTableActive(ddb, tableName);
        return tableName;
    }

    /// <summary>
    /// DeleteItem with ReturnValues=ALL_OLD on a missing key succeeds with no Attributes and no item, also when
    /// repeated; on a real item it returns the old payload and removes the item, and a repeat delete is a no-op.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.delete-item-idempotent-missing-key")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableName = await CreateSimpleTable(ddb, ctx);
        var missingKey = Item(("pk", S("missing")), ("sk", S("x")));

        Task<DeleteItemResponse> DeleteAllOld(Dictionary<string, AttributeValue> key) =>
            ddb.DeleteItemAsync(new DeleteItemRequest { TableName = tableName, Key = key, ReturnValues = ReturnValue.ALL_OLD });
        Task<GetItemResponse> Get(Dictionary<string, AttributeValue> key) =>
            ddb.GetItemAsync(new GetItemRequest { TableName = tableName, Key = key, ConsistentRead = true });

        var resp = await DeleteAllOld(missingKey);
        Ensure(resp.Attributes is null, $"expected no Attributes deleting a missing key, got {Py.Repr(resp.Attributes)}");
        var read1 = await Get(missingKey);
        Ensure(read1.Item is null, $"expected no item at missing key, got {Py.Repr(read1.Item)}");

        // calling it again is still a no-op success, independently confirmed absent
        var resp2 = await DeleteAllOld(missingKey);
        Ensure(resp2.Attributes is null, $"expected no Attributes on repeat delete, got {Py.Repr(resp2.Attributes)}");
        var read2 = await Get(missingKey);
        Ensure(read2.Item is null, $"expected item to remain absent after repeat delete, got {Py.Repr(read2.Item)}");

        // positive control: prove ReturnValues="ALL_OLD" reports real attributes when something
        // actually existed, so the two "not in resp" assertions above are not vacuous
        var realKey = Item(("pk", S("present")), ("sk", S("y")));
        await ddb.PutItemAsync(tableName, Item(("pk", S("present")), ("sk", S("y")), ("payload", S("hello"))));
        var putRead = await Get(realKey);
        Ensure(putRead.Item is not null, $"expected item present after PutItem, got {Py.Repr(putRead.Item)}");

        var delResp = await DeleteAllOld(realKey);
        var deletedPayload = delResp.Attributes is not null && delResp.Attributes.TryGetValue("payload", out var payload) ? payload.S : null;
        Ensure(deletedPayload == "hello", $"expected deleted Attributes to echo the item's payload 'hello', got {Py.Repr(delResp.Attributes)}");
        var read3 = await Get(realKey);
        Ensure(read3.Item is null, $"expected item gone after delete, got {Py.Repr(read3.Item)}");

        // idempotent repeat on the now-missing real key
        var resp3 = await DeleteAllOld(realKey);
        Ensure(resp3.Attributes is null, $"expected no Attributes on repeat delete, got {Py.Repr(resp3.Attributes)}");
        var read4 = await Get(realKey);
        Ensure(read4.Item is null, $"expected item to remain absent after repeat delete, got {Py.Repr(read4.Item)}");
    }
}
