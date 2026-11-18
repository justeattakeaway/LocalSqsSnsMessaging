// Translated from behavioral/dynamodb/check_consumed_capacity_binary_attribute_counts_raw_bytes_not_base64.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.consumed-capacity-binary-attribute-counts-raw-bytes-not-base64</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConsumedCapacityBinaryAttributeCountsRawBytesNotBase64
{
    private const string Check = "consumed-capacity-binary-attribute-counts-raw-bytes-not-base64";

    /// <summary>
    /// PutItem's write capacity for a Binary attribute is billed on its raw byte length: an item
    /// totalling exactly 1024 raw bytes costs 1 write capacity unit and 1025 costs 2, even though the
    /// attribute's base64 text is already longer than 1024 characters in both cases.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.consumed-capacity-binary-attribute-counts-raw-bytes-not-base64")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        // "pk" (2) + a 3-char key value + "b" (1) + raw_len = raw_len + 6; raw_len=1018 -> 1024 total.
        var atBoundary = ItemOf("k01", 1018);
        var base64AtBoundary = Convert.ToBase64String(Bytes(atBoundary["b"].B)).Length;
        var ccAt = (await ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = atBoundary, ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL })).ConsumedCapacity;
        Ensure(
            ccAt.CapacityUnits == 1.0,
            $"a 1024-raw-byte item (base64 {base64AtBoundary} chars) cost {Py.Repr(ccAt.CapacityUnits)} " +
            "write capacity units, expected 1.0");

        var overBoundary = ItemOf("k02", 1019);
        var ccOver = (await ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = overBoundary, ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL })).ConsumedCapacity;
        Ensure(
            ccOver.CapacityUnits == 2.0,
            $"a 1025-raw-byte item cost {Py.Repr(ccOver.CapacityUnits)} write capacity units, expected 2.0");
    }

    /// <summary>A 3-byte key plus one Binary attribute of `raw_len` raw bytes.</summary>
    private static Dictionary<string, AttributeValue> ItemOf(string pk, int rawLen) =>
        Item(("pk", S(pk)), ("b", B(Enumerable.Repeat((byte)'x', rawLen).ToArray())));
}
