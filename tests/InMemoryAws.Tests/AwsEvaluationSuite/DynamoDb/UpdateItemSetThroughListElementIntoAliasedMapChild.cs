// Translated from behavioral/dynamodb/check_update_item_set_through_list_element_into_aliased_map_child.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-set-through-list-element-into-aliased-map-child</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemSetThroughListElementIntoAliasedMapChild
{
    private const string Check = "update-item-set-through-list-element-into-aliased-map-child";

    private static AttributeValue Elem(string status, string note) => M(("status", S(status)), ("note", S(note)));

    /// <summary>
    /// SET through a list element into a map child applies with a list- or child-alias, and
    /// ALL_NEW/ALL_OLD/UPDATED_NEW each scope to the full item or to the touched leaf alone.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_UpdateItem.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-set-through-list-element-into-aliased-map-child")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-ddb-lec-");
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

        var key = Item(("pk", S("doc")));
        await ddb.PutItemAsync(t, Item(
            ("pk", S("doc")),
            ("l", L(Elem("open", "n0"), Elem("pending", "n1"), Elem("closed", "n2"))),
            ("other", S("keep"))));

        // 1. SET l[0].#k = :v, aliasing only the reserved-word child ("status"); unaliased the
        // literal keyword would be a parse error. ReturnValues defaults to NONE: prove the write
        // through a read, not the response.
        var resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = key,
            UpdateExpression = "SET l[0].#k = :v",
            ExpressionAttributeNames = Names(("#k", "status")),
            ExpressionAttributeValues = Item((":v", S("approved"))),
        });
        Ensure(resp.Attributes is null, $"default ReturnValues must omit Attributes, got {Py.Repr(resp.Attributes)}");
        var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = key, ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(item["l"], L(Elem("approved", "n0"), Elem("pending", "n1"), Elem("closed", "n2"))),
            $"after SET l[0].#k the list was {Py.Repr(item["l"])}; expected only index 0's status changed");

        // 2. SET through a list element into an ordinary (non-reserved) map child via alias, on a
        // different index, proving the mechanism is general and not specific to the reserved word.
        resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = key,
            UpdateExpression = "SET #l[1].#n = :v",
            ExpressionAttributeNames = Names(("#l", "l"), ("#n", "note")),
            ExpressionAttributeValues = Item((":v", S("n1-updated"))),
            ReturnValues = ReturnValue.UPDATED_OLD,
        });
        Ensure(
            Py.Equal(resp.Attributes, Item(("l", L(M(("note", S("n1"))))))),
            $"UPDATED_OLD for #l[1].#n returned {Py.Repr(resp.Attributes)}; expected a single-element list " +
            "holding only the pre-update leaf");

        // 3. Aliased list AND aliased reserved-word child together, with UPDATED_NEW: the rebuild
        // composes the list-index truncation (single-element list) with the map rebuild (only the
        // touched field), not just one or the other.
        resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = key,
            UpdateExpression = "SET #l[2].#k = :v",
            ExpressionAttributeNames = Names(("#l", "l"), ("#k", "status")),
            ExpressionAttributeValues = Item((":v", S("reopened"))),
            ReturnValues = ReturnValue.UPDATED_NEW,
        });
        Ensure(
            Py.Equal(resp.Attributes, Item(("l", L(M(("status", S("reopened"))))))),
            $"UPDATED_NEW for #l[2].#k returned {Py.Repr(resp.Attributes)}; expected a single-element list " +
            "holding a map with only the touched field");

        // 4. ALL_NEW after a list-element-child SET returns the complete resulting item, not just the
        // touched path.
        resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = key,
            UpdateExpression = "SET l[0].#k = :v",
            ExpressionAttributeNames = Names(("#k", "status")),
            ExpressionAttributeValues = Item((":v", S("closed-final"))),
            ReturnValues = ReturnValue.ALL_NEW,
        });
        var wantAllNew = Item(
            ("pk", S("doc")),
            ("l", L(Elem("closed-final", "n0"), M(("status", S("pending")), ("note", S("n1-updated"))), Elem("reopened", "n2"))),
            ("other", S("keep")));
        Ensure(
            Py.Equal(resp.Attributes, wantAllNew),
            $"ALL_NEW after SET l[0].#k returned {Py.Repr(resp.Attributes)}; expected {Py.Repr(wantAllNew)}");

        // 5. ALL_OLD before a list-element-child SET returns the complete prior item.
        var wantAllOld = wantAllNew;
        resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = key,
            UpdateExpression = "SET l[1].#n = :v",
            ExpressionAttributeNames = Names(("#n", "note")),
            ExpressionAttributeValues = Item((":v", S("n1-final"))),
            ReturnValues = ReturnValue.ALL_OLD,
        });
        Ensure(
            Py.Equal(resp.Attributes, wantAllOld),
            $"ALL_OLD before SET l[1].#n returned {Py.Repr(resp.Attributes)}; expected the prior full item {Py.Repr(wantAllOld)}");

        var final = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = key, ConsistentRead = true })).Item;
        var wantFinal = Item(
            ("pk", S("doc")),
            ("l", L(Elem("closed-final", "n0"), M(("status", S("pending")), ("note", S("n1-final"))), Elem("reopened", "n2"))),
            ("other", S("keep")));
        Ensure(Py.Equal(final, wantFinal), $"final item was {Py.Repr(final)}; expected {Py.Repr(wantFinal)}");
    }
}
