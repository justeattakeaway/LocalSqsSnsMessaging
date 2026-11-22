// Translated from behavioral/dynamodb/check_query_filter_contains_empty_string_set_member_matches_the_holder.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-filter-contains-empty-string-set-member-matches-the-holder</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryFilterContainsEmptyStringSetMemberMatchesTheHolder
{
    private const string Check = "query-filter-contains-empty-string-set-member-matches-the-holder";
    private const string Pk = "pk";
    private const string Sk = "sk";

    /// <summary>Items in the queried partition; only `i2`'s roles set holds the empty-string member.</summary>
    private const string P1 = "p1";

    private static readonly (string Sk, string[] Roles)[] P1Items =
    [
        ("i1", ["admin", "packer"]),
        ("i2", ["", "packer"]),
        ("i3", ["reader"]),
    ];

    /// <summary>
    /// A different partition whose item also holds the empty member, proving the match stays
    /// inside the queried partition rather than leaking in from elsewhere in the table.
    /// </summary>
    private const string P2 = "p2";

    private static readonly (string Sk, string[] Roles) P2Item = ("i4", ["", "x"]);

    /// <summary>
    /// Query FilterExpression contains(roles, '') matches the one item in the partition whose
    /// SS holds the empty-string member, leaving its non-holding partition siblings out.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-filter-contains-empty-string-set-member-matches-the-holder")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement(Pk, KeyType.HASH), new KeySchemaElement(Sk, KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition(Pk, ScalarAttributeType.S), new AttributeDefinition(Sk, ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        Task Put(string pk, (string Sk, string[] Roles) item) =>
            ddb.PutItemAsync(table, Item(("pk", S(pk)), ("sk", S(item.Sk)), ("roles", SS(item.Roles))));

        foreach (var item in P1Items)
        {
            await Put(P1, item);
        }

        await Put(P2, P2Item);

        var resp = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            ConsistentRead = true,
            KeyConditionExpression = "#pk = :pk",
            FilterExpression = "contains(#r, :empty)",
            ExpressionAttributeNames = Names(("#pk", "pk"), ("#r", "roles")),
            ExpressionAttributeValues = Item((":pk", S(P1)), (":empty", S(""))),
        });
        var items = resp.Items;
        Ensure(resp.Count == 1, $"expected exactly one match in partition {Py.Repr(P1)}, got Count={resp.Count} Items={Py.Repr(items)}");
        Ensure(items[0]["sk"].S == "i2", $"expected the holder i2 to match, got {Py.Repr(items[0]["sk"])}");
        Ensure(
            items[0]["roles"].SS.Order(StringComparer.Ordinal).SequenceEqual(["", "packer"]),
            $"the matched item's roles must still be the set it was written with, got {Py.Repr(items[0]["roles"])}");
        Ensure(
            resp.ScannedCount == P1Items.Length,
            $"ScannedCount was {resp.ScannedCount}, expected the partition's {P1Items.Length} items " +
            "regardless of the filter");

        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(P1)), ("sk", S("i1"))), ConsistentRead = true })).Item;
        Ensure(
            got["roles"].SS.Order(StringComparer.Ordinal).SequenceEqual(["admin", "packer"]),
            $"i1's roles set must not hold the empty member and must be unaffected by the query, got {Py.Repr(got["roles"])}");
    }
}
