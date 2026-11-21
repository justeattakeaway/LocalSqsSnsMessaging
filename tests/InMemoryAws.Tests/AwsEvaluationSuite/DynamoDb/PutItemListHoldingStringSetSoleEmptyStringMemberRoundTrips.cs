// Translated from behavioral/dynamodb/check_put_item_list_holding_string_set_sole_empty_string_member_round_trips.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.put-item-list-holding-string-set-sole-empty-string-member-round-trips</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutItemListHoldingStringSetSoleEmptyStringMemberRoundTrips
{
    private const string Check = "put-item-list-holding-string-set-sole-empty-string-member-round-trips";

    /// <summary>
    /// PutItem accepts an L element holding a string set whose only member is the empty
    /// string, and a later GetItem reads the set back with that one member intact.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.put-item-list-holding-string-set-sole-empty-string-member-round-trips")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-ddb-lss-");
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

        await ddb.PutItemAsync(t, Item(("pk", S("x")), ("attr", L(SS("")))));

        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("x"))), ConsistentRead = true })).Item;
        Ensure(got is not null, "an L holding an SS whose only member is the empty string must be stored");
        Ensure(
            Py.Equal(got["attr"], L(SS(""))),
            $"GetItem must read back the L element as [{{'SS': ['']}}], got {Py.Repr(got["attr"])}");
    }
}
