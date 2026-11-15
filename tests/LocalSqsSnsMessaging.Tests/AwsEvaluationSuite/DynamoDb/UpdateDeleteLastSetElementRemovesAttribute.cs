// Translated from behavioral/dynamodb/check_update_delete_last_set_element_removes_attribute.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-delete-last-set-element-removes-attribute</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateDeleteLastSetElementRemovesAttribute
{
    private const string Check = "update-delete-last-set-element-removes-attribute";
    private const string ErrValidation = "ValidationException";

    /// <summary>Call fn(); return its ClientError code and message, raising on an unexpected success.</summary>
    private static async Task<(string? Code, string? Message)> ErrorCode<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, err) = await FidelityContext.Capture(fn);
        if (err is not null)
        {
            return (err.Code, err.Message);
        }

        throw Fail($"real AWS rejects this call, but it SUCCEEDED and returned {result}");
    }

    /// <summary>Python's <c>f'{item.get(name)}'</c>: the attribute's dict, or None.</summary>
    private static string Attr(Dictionary<string, AttributeValue> item, string name) =>
        item.TryGetValue(name, out var value) ? Py.Repr(value) : "None";

    private static bool SameMembers(List<string>? members, params string[] expected) =>
        members is not null && members.ToHashSet(StringComparer.Ordinal).SetEquals(expected);

    /// <summary>
    /// DynamoDB has no empty set: DELETE of a set's last member removes the attribute, ADD re-creates
    /// it, DELETE of a non-member is a no-op, and writing an empty SS or NS is ValidationException.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-delete-last-set-element-removes-attribute")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        var key = Item(("pk", S("sets")));
        await ddb.PutItemAsync(t, Item(("pk", S("sets")), ("tags", SS("only")), ("nums", NS("7")), ("keep", S("k"))));

        async Task<Dictionary<string, AttributeValue>> Get() =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = key, ConsistentRead = true })).Item;

        // DELETE the last String Set member -> the ATTRIBUTE is gone, the item survives.
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = key,
            UpdateExpression = "DELETE tags :rm",
            ExpressionAttributeValues = Item((":rm", SS("only"))),
        });
        var item = await Get();
        Ensure(
            !item.ContainsKey("tags"),
            $"removing a set's final member must delete the attribute, not leave an empty set; got tags={Attr(item, "tags")}");
        Ensure(Py.Equal(item["keep"], S("k")), $"the rest of the item must be untouched, got {Py.Repr(item)}");
        Ensure(Py.Equal(item["pk"], S("sets")), $"the key must survive, got {Py.Repr(item)}");

        // attribute_not_exists must now hold for that attribute -- the observable consequence.
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = key,
            UpdateExpression = "SET marker = :m",
            ConditionExpression = "attribute_not_exists(tags)",
            ExpressionAttributeValues = Item((":m", S("gone"))),
        });

        // Same rule for a Number Set.
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = key,
            UpdateExpression = "DELETE nums :rm",
            ExpressionAttributeValues = Item((":rm", NS("7"))),
        });
        item = await Get();
        Ensure(
            !item.ContainsKey("nums"),
            $"removing a Number Set's final member must delete the attribute; got nums={Attr(item, "nums")}");

        // ADD re-creates the attribute from nothing.
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = key,
            UpdateExpression = "ADD tags :add",
            ExpressionAttributeValues = Item((":add", SS("x", "y"))),
        });
        item = await Get();
        Ensure(
            SameMembers(item["tags"].SS, "x", "y"),
            $"ADD on a missing set attribute must create it with the added members, got {Attr(item, "tags")}");

        // DELETE of a non-member is a silent no-op (no error, set unchanged).
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = key,
            UpdateExpression = "DELETE tags :rm",
            ExpressionAttributeValues = Item((":rm", SS("not-a-member"))),
        });
        item = await Get();
        Ensure(
            SameMembers(item["tags"].SS, "x", "y"),
            $"DELETE of a non-member must leave the set unchanged, got {Attr(item, "tags")}");

        // DELETE of every remaining member removes the attribute again.
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = key,
            UpdateExpression = "DELETE tags :rm",
            ExpressionAttributeValues = Item((":rm", SS("x", "y"))),
        });
        item = await Get();
        Ensure(
            !item.ContainsKey("tags"),
            $"deleting every member must remove the attribute, got tags={Attr(item, "tags")}");

        // The empty set is not a representable value: writing one directly is rejected, and the
        // rejected write leaves no item behind.
        var (code, message) = await ErrorCode(() => ddb.PutItemAsync(t, Item(("pk", S("empty-ss")), ("tags", SS()))));
        Ensure(code == ErrValidation, $"PutItem with an empty String Set must be rejected with {ErrValidation}; got {code}");
        Ensure(
            message == "1 validation error detected: One or more parameter values were invalid: An string set  may not be empty",
            $"expected AWS's exact ValidationException message, got {Py.Repr(message)}");
        var leaked = await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("empty-ss"))), ConsistentRead = true });
        Ensure(
            leaked.Item is null,
            $"the rejected empty-set PutItem must not create an item, found {Py.Repr(leaked.Item)}");
        var (nsCode, nsMessage) = await ErrorCode(() => ddb.PutItemAsync(t, Item(("pk", S("empty-ns")), ("nums", NS()))));
        Ensure(nsCode == ErrValidation, $"PutItem with an empty Number Set must be rejected with {ErrValidation}; got {nsCode}");
        Ensure(
            nsMessage == "1 validation error detected: One or more parameter values were invalid: An number set  may not be empty",
            $"expected AWS's exact ValidationException message, got {Py.Repr(nsMessage)}");

        // Control: the same write with a non-empty set succeeds.
        await ddb.PutItemAsync(t, Item(("pk", S("empty-ss")), ("tags", SS("ok"))));
        var control = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("empty-ss"))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(control["tags"], SS("ok")), $"control non-empty-set write must succeed, got {Py.Repr(control)}");
    }
}
