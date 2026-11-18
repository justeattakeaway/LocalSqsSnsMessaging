// Translated from behavioral/dynamodb/check_put_get_binary_blob_round_trip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.put-get-binary-blob-round-trip</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutGetBinaryBlobRoundTrip
{
    private const string CheckId = "put-get-binary-blob-round-trip";

    /// <summary>
    /// A base64-transported Binary (B) attribute round-trips through PutItem/GetItem with
    /// identical byte content, including all 256 byte values.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.put-get-binary-blob-round-trip")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx);
        var pk = FidelityContext.UniqueName("item-");
        var payload = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        await ddb.PutItemAsync(table, Item(("pk", S(pk)), ("blob", B(payload))));
        var resp = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true });
        var item = resp.Item;
        Ensure(item is not null, "item not found after PutItem");
        var returned = item["blob"].B;
        // botocore decodes Binary shapes back to raw bytes for the JSON protocol.
        Ensure(Bytes(returned).SequenceEqual(payload), "binary payload changed across round-trip");
    }

    private static async Task<string> CreateTable(FidelityContext ctx)
    {
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 250);
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        return name;
    }
}
