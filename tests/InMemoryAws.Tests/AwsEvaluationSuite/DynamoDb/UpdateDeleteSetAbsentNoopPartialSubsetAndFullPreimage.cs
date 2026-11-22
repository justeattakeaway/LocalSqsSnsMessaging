// Translated from behavioral/dynamodb/check_update_delete_set_absent_noop_partial_subset_and_full_preimage.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-delete-set-absent-noop-partial-subset-and-full-preimage</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateDeleteSetAbsentNoopPartialSubsetAndFullPreimage
{
    private const string Check = "update-delete-set-absent-noop-partial-subset-and-full-preimage";

    private static bool SetIs(IEnumerable<string>? members, params string[] expected) =>
        members is not null && new HashSet<string>(members, StringComparer.Ordinal).SetEquals(expected);

    /// <summary>
    /// UpdateItem DELETE on an attribute the item lacks is a silent no-op; DELETE of a proper
    /// subset of a Number Set leaves the exact remainder, and UPDATED_OLD reports the full
    /// pre-delete set rather than only the removed members.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_UpdateItem.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-delete-set-absent-noop-partial-subset-and-full-preimage")]
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
        await ddb.PutItemAsync(table, Item(("pk", S("item")), ("nums", NS("1", "2", "3", "4")), ("keep", S("k"))));

        // DELETE against an attribute the item never had: no error, nothing to report back.
        var resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "DELETE ghost :v",
            ExpressionAttributeValues = Item((":v", SS("z"))),
            ReturnValues = ReturnValue.UPDATED_OLD,
        });
        Ensure(
            resp.Attributes is null,
            $"DELETE of a never-set attribute with UPDATED_OLD must report nothing, got {Py.Repr(resp.Attributes)}");
        var unaffected = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(
            SetIs(unaffected["nums"].NS, "1", "2", "3", "4"),
            $"a no-op DELETE must leave sibling attributes untouched, got {Py.Repr(unaffected["nums"])}");
        Ensure(Py.Equal(unaffected["keep"], S("k")), $"a no-op DELETE must leave sibling attributes untouched, got {Py.Repr(unaffected)}");

        // DELETE of a proper subset of the Number Set: UPDATED_OLD is the full pre-delete set, not
        // just the two removed members.
        var old = (await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "DELETE nums :v",
            ExpressionAttributeValues = Item((":v", NS("1", "2"))),
            ReturnValues = ReturnValue.UPDATED_OLD,
        })).Attributes;
        Ensure(
            SetIs(old["nums"].NS, "1", "2", "3", "4"),
            $"UPDATED_OLD after a subset DELETE must be the full pre-delete set, got {Py.Repr(old["nums"])}");

        var remainder = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(
            SetIs(remainder["nums"].NS, "3", "4"),
            $"DELETE of a proper subset must leave the exact remainder, got {Py.Repr(remainder["nums"])}");
    }
}
