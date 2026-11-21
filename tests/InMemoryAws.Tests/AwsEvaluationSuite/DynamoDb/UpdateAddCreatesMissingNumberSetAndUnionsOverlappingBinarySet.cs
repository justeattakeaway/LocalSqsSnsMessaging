// Translated from behavioral/dynamodb/check_update_add_creates_missing_number_set_and_unions_overlapping_binary_set.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-add-creates-missing-number-set-and-unions-overlapping-binary-set</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateAddCreatesMissingNumberSetAndUnionsOverlappingBinarySet
{
    private const string Check = "update-add-creates-missing-number-set-and-unions-overlapping-binary-set";

    private static bool SetIs(IEnumerable<string>? members, params string[] expected) =>
        members is not null && new HashSet<string>(members, StringComparer.Ordinal).SetEquals(expected);

    private static bool BinarySetIs(IEnumerable<MemoryStream>? members, params string[] expected) =>
        members is not null && new HashSet<string>(members.Select(m => Convert.ToBase64String(m.ToArray())), StringComparer.Ordinal)
            .SetEquals(expected.Select(e => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(e))));

    /// <summary>
    /// UpdateItem ADD onto a Number Set the item lacks creates it with exactly the added
    /// members, and ADD onto an existing Binary Set unions in a member already present without
    /// duplicating it.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_UpdateItem.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-add-creates-missing-number-set-and-unions-overlapping-binary-set")]
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

        var key = Item(("pk", S("item")));
        await ddb.PutItemAsync(table, Item(("pk", S("item")), ("bins", BS("aa"u8.ToArray(), "bb"u8.ToArray()))));

        // ADD onto a Number Set that does not exist yet: the attribute is created from nothing.
        var newAttrs = (await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "ADD counts :v",
            ExpressionAttributeValues = Item((":v", NS("5", "7"))),
            ReturnValues = ReturnValue.UPDATED_NEW,
        })).Attributes;
        Ensure(
            SetIs(newAttrs["counts"].NS, "5", "7"),
            $"ADD onto a missing Number Set must create it with exactly the added members, got {Py.Repr(newAttrs["counts"])}");
        var created = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(
            SetIs(created["counts"].NS, "5", "7"),
            $"the created Number Set must read back exactly the added members, got {Py.Repr(created["counts"])}");
        Ensure(
            BinarySetIs(created["bins"].BS, "aa", "bb"),
            $"ADD on one attribute must leave the sibling Binary Set untouched, got {Py.Repr(created["bins"])}");

        // ADD a Binary Set with one member already present: the union drops the duplicate.
        var allNew = (await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "ADD bins :v",
            ExpressionAttributeValues = Item((":v", BS("aa"u8.ToArray(), "cc"u8.ToArray()))),
            ReturnValues = ReturnValue.ALL_NEW,
        })).Attributes;
        Ensure(
            BinarySetIs(allNew["bins"].BS, "aa", "bb", "cc"),
            $"ADD with an overlapping Binary Set member must union without duplicating it, got {Py.Repr(allNew["bins"])}");
        Ensure(
            SetIs(allNew["counts"].NS, "5", "7"),
            $"ALL_NEW must still carry the sibling attribute unchanged, got {Py.Repr(allNew["counts"])}");
        Ensure(Py.Equal(allNew["pk"], key["pk"]), $"ALL_NEW must still carry the key, got {Py.Repr(allNew.GetValueOrDefault("pk"))}");
    }
}
