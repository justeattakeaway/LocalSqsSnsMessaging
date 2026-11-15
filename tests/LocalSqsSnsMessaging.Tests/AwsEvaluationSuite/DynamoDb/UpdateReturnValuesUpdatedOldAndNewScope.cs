// Translated from behavioral/dynamodb/check_update_return_values_updated_old_and_new_scope.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-return-values-updated-old-and-new-scope</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateReturnValuesUpdatedOldAndNewScope
{
    private const string Check = "update-return-values-updated-old-and-new-scope";

    /// <summary>
    /// UpdateItem ReturnValues differ in scope: UPDATED_NEW/UPDATED_OLD return only touched attributes,
    /// ALL_NEW/ALL_OLD the whole item with its key, NONE omits Attributes, and ALL_OLD on a new key
    /// omits Attributes rather than returning an empty map.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-return-values-updated-old-and-new-scope")]
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

        var key = Item(("pk", S("rv")));
        await ddb.PutItemAsync(t, Item(("pk", S("rv")), ("a", N("1")), ("untouched", S("keep"))));

        Task<UpdateItemResponse> Update(Dictionary<string, AttributeValue> k, string expr, Dictionary<string, AttributeValue> values, ReturnValue rv) =>
            ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = t,
                Key = k,
                UpdateExpression = expr,
                ExpressionAttributeValues = values,
                ReturnValues = rv,
            });

        // UPDATED_NEW: exactly the attributes SET by this expression, and nothing else.
        var resp = await Update(key, "SET a = :a, b = :b", Item((":a", N("2")), (":b", S("new"))), ReturnValue.UPDATED_NEW);
        var attrs = resp.Attributes;
        Ensure(
            Py.Equal(attrs, Item(("a", N("2")), ("b", S("new")))),
            "UPDATED_NEW must return exactly the updated attributes (no pk, no untouched), " +
            $"got {Py.Repr(attrs)}");
        Ensure(!attrs.ContainsKey("pk"), $"UPDATED_NEW must not echo the key, got {Py.Repr(Sorted(attrs))}");
        Ensure(!attrs.ContainsKey("untouched"), $"UPDATED_NEW must not echo untouched attributes, got {Py.Repr(Sorted(attrs))}");

        // UPDATED_OLD: the PRE-update values of exactly the touched attributes.
        resp = await Update(key, "SET a = :a", Item((":a", N("3"))), ReturnValue.UPDATED_OLD);
        attrs = resp.Attributes;
        Ensure(
            Py.Equal(attrs, Item(("a", N("2")))),
            $"UPDATED_OLD must return only the pre-update value of the one touched attribute, got {Py.Repr(attrs)}");

        // ALL_OLD: the ENTIRE prior item, key included.
        resp = await Update(key, "SET a = :a", Item((":a", N("4"))), ReturnValue.ALL_OLD);
        attrs = resp.Attributes;
        Ensure(
            Py.Equal(attrs, Item(("pk", S("rv")), ("a", N("3")), ("b", S("new")), ("untouched", S("keep")))),
            $"ALL_OLD must return the complete prior item including the key, got {Py.Repr(attrs)}");

        // ALL_NEW: the ENTIRE resulting item, key included.
        resp = await Update(key, "SET a = :a", Item((":a", N("5"))), ReturnValue.ALL_NEW);
        attrs = resp.Attributes;
        Ensure(
            Py.Equal(attrs, Item(("pk", S("rv")), ("a", N("5")), ("b", S("new")), ("untouched", S("keep")))),
            $"ALL_NEW must return the complete resulting item including the key, got {Py.Repr(attrs)}");

        // NONE: no Attributes member at all (not an empty map).
        resp = await Update(key, "SET a = :a", Item((":a", N("6"))), ReturnValue.NONE);
        Ensure(
            resp.Attributes is null,
            $"ReturnValues=NONE must omit Attributes entirely, got {Py.Repr(resp.Attributes)}");

        // ALL_OLD on an UpsertING update: the item is created, but there is no prior item, so
        // Attributes must be omitted rather than returned empty.
        var fresh = Item(("pk", S("rv-fresh")));
        resp = await Update(fresh, "SET c = :c", Item((":c", S("created"))), ReturnValue.ALL_OLD);
        Ensure(
            resp.Attributes is null,
            $"ALL_OLD on a key with no prior item must omit Attributes, got {Py.Repr(resp.Attributes)}");
        var created = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = fresh, ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(created, Item(("pk", S("rv-fresh")), ("c", S("created")))),
            $"the upserting UpdateItem must still have created the item, got {Py.Repr(created)}");

        // UPDATED_OLD on that same brand-new key's next update returns only the touched attribute.
        resp = await Update(fresh, "SET c = :c", Item((":c", S("replaced"))), ReturnValue.UPDATED_OLD);
        Ensure(
            Py.Equal(resp.Attributes, Item(("c", S("created")))),
            $"UPDATED_OLD must return only the touched attribute, got {Py.Repr(resp.Attributes)}");

        // Final state check: the sequence of updates landed exactly as expected.
        var final = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = key, ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(final, Item(("pk", S("rv")), ("a", N("6")), ("b", S("new")), ("untouched", S("keep")))),
            $"unexpected final item state, got {Py.Repr(final)}");
    }

    private static List<string> Sorted(Dictionary<string, AttributeValue> item) => item.Keys.Order(StringComparer.Ordinal).ToList();
}
