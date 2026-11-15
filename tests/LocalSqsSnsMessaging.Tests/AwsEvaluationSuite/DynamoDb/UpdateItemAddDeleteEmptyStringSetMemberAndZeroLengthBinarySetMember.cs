// Translated from behavioral/dynamodb/check_update_item_add_delete_empty_string_set_member_and_zero_length_binary_set_member.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-add-delete-empty-string-set-member-and-zero-length-binary-set-member</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemAddDeleteEmptyStringSetMemberAndZeroLengthBinarySetMember
{
    private const string CheckId = "update-item-add-delete-empty-string-set-member-and-zero-length-binary-set-member";

    /// <summary>
    /// UpdateItem ADD/DELETE on a String Set follow ordinary set semantics for the empty-string
    /// member, and ADD on a Binary Set accepts a zero-length member.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-add-delete-empty-string-set-member-and-zero-length-binary-set-member")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-ddb-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real-behav", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        var key = Item(("pk", S("sets")));
        await ddb.PutItemAsync(t, Item(("pk", S("sets"))));

        Task<Dictionary<string, AttributeValue>> Update(string expression, AttributeValue operand, bool allNew = true) =>
            UpdateAsync(ddb, t, key, expression, operand, allNew);

        // ADD [''] onto an absent attribute creates an SS of just the empty string.
        var item = await Update("ADD tags :e", SS(""));
        Ensure(
            item["tags"].SS.SequenceEqual([""]),
            $"ADD {{'SS': ['']}} onto an absent attribute produced {Py.Repr(item.GetValueOrDefault("tags"))}, expected {{'SS': ['']}}");

        // ADD [''] again onto an SS that already holds only the empty string leaves one member.
        item = await Update("ADD tags :e", SS(""));
        Ensure(
            item["tags"].SS.SequenceEqual([""]),
            $"ADD {{'SS': ['']}} onto SS=[''] produced {Py.Repr(item.GetValueOrDefault("tags"))}, expected it to stay {{'SS': ['']}}");

        // Bring in a second member so the set is {'', 'a'} (member order not asserted).
        item = await Update("ADD tags :e", SS("a"));
        Ensure(
            item["tags"].SS.ToHashSet(StringComparer.Ordinal).SetEquals(["", "a"]),
            $"ADD {{'SS': ['a']}} onto SS=[''] produced {Py.Repr(item.GetValueOrDefault("tags"))}, expected the set {{'', 'a'}}");

        // DELETE [''] from {'', 'a'} leaves just ['a'].
        item = await Update("DELETE tags :rm", SS(""));
        Ensure(
            item["tags"].SS.SequenceEqual(["a"]),
            $"DELETE {{'SS': ['']}} from SS={{'', 'a'}} produced {Py.Repr(item.GetValueOrDefault("tags"))}, expected {{'SS': ['a']}}");

        // Put the empty string back, so the set is {'', 'a'} again.
        item = await Update("ADD tags :e", SS(""));
        Ensure(
            item["tags"].SS.ToHashSet(StringComparer.Ordinal).SetEquals(["", "a"]),
            $"ADD {{'SS': ['']}} onto SS=['a'] produced {Py.Repr(item.GetValueOrDefault("tags"))}, expected the set {{'', 'a'}}");

        // DELETE ['a'] from {'', 'a'} leaves the SS holding only the empty string.
        item = await Update("DELETE tags :rm", SS("a"));
        Ensure(
            item["tags"].SS.SequenceEqual([""]),
            $"DELETE {{'SS': ['a']}} from SS={{'', 'a'}} produced {Py.Repr(item.GetValueOrDefault("tags"))}, expected {{'SS': ['']}}");

        // DELETE [''] from [''] removes the attribute entirely -- DynamoDB has no empty set.
        await Update("DELETE tags :rm", SS(""), allNew: false);
        item = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = key, ConsistentRead = true })).Item;
        Ensure(
            !item.ContainsKey("tags"),
            $"DELETE {{'SS': ['']}} from SS=[''] must remove the attribute, got tags={Py.Repr(item.GetValueOrDefault("tags"))}");

        // ADD [b''] onto an absent Binary Set attribute creates one holding the zero-length member.
        item = await Update("ADD bins :e", BS(Array.Empty<byte>()));
        Ensure(
            item["bins"].BS.Count == 1 && item["bins"].BS[0].Length == 0,
            $"ADD {{'BS': [b'']}} onto an absent attribute produced {Py.Repr(item.GetValueOrDefault("bins"))}, expected {{'BS': [b'']}}");

        // ADD [b'x'] onto that Binary Set, then ADD [b''] again: the zero-length member joins it.
        await Update("ADD bins :e", BS("x"u8.ToArray()), allNew: false);
        item = await Update("ADD bins :e", BS(Array.Empty<byte>()));
        var members = item["bins"].BS.Select(b => Convert.ToHexString(b.ToArray())).ToHashSet(StringComparer.Ordinal);
        Ensure(
            members.SetEquals(["", Convert.ToHexString("x"u8)]),
            $"ADD {{'BS': [b'']}} onto BS=[b'x'] produced {Py.Repr(item.GetValueOrDefault("bins"))}, expected the set {{b'', b'x'}}");
    }

    private static async Task<Dictionary<string, AttributeValue>> UpdateAsync(
        IAmazonDynamoDB ddb, string table, Dictionary<string, AttributeValue> key, string expression, AttributeValue operand, bool allNew)
    {
        var placeholder = expression.Contains(":rm", StringComparison.Ordinal) ? ":rm" : ":e";
        var request = new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = expression,
            ExpressionAttributeValues = Item((placeholder, operand)),
        };
        if (allNew)
        {
            request.ReturnValues = ReturnValue.ALL_NEW;
        }

        return (await ddb.UpdateItemAsync(request)).Attributes;
    }
}
