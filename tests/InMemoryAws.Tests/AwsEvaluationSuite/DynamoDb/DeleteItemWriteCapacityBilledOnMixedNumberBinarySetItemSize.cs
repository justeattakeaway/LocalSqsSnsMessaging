// Translated from behavioral/dynamodb/check_delete_item_write_capacity_billed_on_mixed_number_binary_set_item_size.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Text;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.delete-item-write-capacity-billed-on-mixed-number-binary-set-item-size</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteItemWriteCapacityBilledOnMixedNumberBinarySetItemSize
{
    private const string Check = "delete-item-write-capacity-billed-on-mixed-number-binary-set-item-size";

    // 37 significant digits, no leading/trailing zeroes, so DynamoDB stores it verbatim and its
    // documented size (1 byte per two significant digits, plus one byte) is exact: 1 ("n") + 19 + 1 = 21.
    private const string NValue = "1234567890123456789012345678901234567";

    /// <summary>"pk"(2)+pk(3)=5, "n"(1)+21 digit-bytes=21, "b"(1)+b_len, "bs"(2)+1-byte element=3.</summary>
    private static Dictionary<string, AttributeValue> ItemOf(string pk, int bLen) => Item(
        ("pk", S(pk)),
        ("n", N(NValue)),
        ("b", B(Encoding.ASCII.GetBytes(new string('x', bLen)))),
        ("bs", BS("y"u8.ToArray())));

    /// <summary>
    /// DeleteItem's write capacity is billed on the deleted item's total byte size even when that
    /// size is built from Number and Binary Set members, not only String: an item mixing N, B and BS
    /// that totals 1024 bytes costs 1 write capacity unit, and one at 1025 bytes costs 2.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.delete-item-write-capacity-billed-on-mixed-number-binary-set-item-size")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-delnbs-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        // fixed = pk(5) + n(21) = 26; b(1+994)=995; bs(2+1)=3 -> 26+995+3 = 1024.
        var atBoundary = ItemOf("k01", 994);
        await ddb.PutItemAsync(table, atBoundary);
        var ccAt = (await ddb.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("k01"))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        })).ConsumedCapacity;
        Ensure(
            ccAt.CapacityUnits == 1.0,
            "deleting the 1024-byte mixed N/B/BS item must cost 1.0 write capacity unit, got " +
            $"{Py.Repr(ccAt.CapacityUnits)}");
        Ensure(
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("k01"))), ConsistentRead = true })).Item is null,
            "the deleted 1024-byte item must be gone");

        // one more raw byte in "b" -> 1025 total.
        var overBoundary = ItemOf("k02", 995);
        await ddb.PutItemAsync(table, overBoundary);
        var ccOver = (await ddb.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("k02"))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        })).ConsumedCapacity;
        Ensure(
            ccOver.CapacityUnits == 2.0,
            "deleting the 1025-byte mixed N/B/BS item must cost 2.0 write capacity units, got " +
            $"{Py.Repr(ccOver.CapacityUnits)}");
        Ensure(
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("k02"))), ConsistentRead = true })).Item is null,
            "the deleted 1025-byte item must be gone");
    }

    /// <summary>A cleanup running <c>undo()</c> where a failed delete raises.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };
}
