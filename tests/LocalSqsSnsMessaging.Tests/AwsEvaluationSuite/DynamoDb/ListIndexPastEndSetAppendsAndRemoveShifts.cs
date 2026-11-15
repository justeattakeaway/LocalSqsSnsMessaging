// Translated from behavioral/dynamodb/check_list_index_past_end_set_appends_and_remove_shifts.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.list-index-past-end-set-appends-and-remove-shifts</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListIndexPastEndSetAppendsAndRemoveShifts
{
    private const string CheckId = "list-index-past-end-set-appends-and-remove-shifts";
    private const double TableActiveBudgetSeconds = 120.0;
    private static readonly string[] AfterSet = ["x", "y", "z", "w"];
    private static readonly string[] AfterRemove = ["x", "z", "w"];

    private static async Task DeleteTableWhenPossible(IAmazonDynamoDB ddb, string tableName)
    {
        try
        {
            await ddb.DeleteTableAsync(tableName);
        }
        catch (ResourceNotFoundException)
        {
        }
    }

    private static async Task<string> CreateTable(FidelityContext ctx, IAmazonDynamoDB ddb)
    {
        var name = FidelityContext.UniqueName("sim2real-behav-ddb-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => DeleteTableWhenPossible(ddb, name));
        var clock = FidelityTime.StartNew();
        string? status = null;
        while (clock.Elapsed.TotalSeconds < TableActiveBudgetSeconds)
        {
            status = (await ddb.DescribeTableAsync(name)).Table.TableStatus.Value;
            if (status == "ACTIVE")
            {
                return name;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2.0));
        }

        throw Fail($"table {Py.Repr(name)} not ACTIVE within {Py.Repr(TableActiveBudgetSeconds)}s: {Py.Repr(status)}");
    }

    private static List<string> Strings(AttributeValue attr) => [.. attr.L.Select(e => e.S)];

    /// <summary>
    /// SET at a list index past the end appends (UPDATED_NEW returns nothing, as l[10] does not
    /// exist), REMOVE past the end is a no-op, and REMOVE inside the list shifts the tail left with
    /// UPDATED_NEW reporting the element now at that index.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.list-index-past-end-set-appends-and-remove-shifts")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx, ddb);
        Dictionary<string, AttributeValue> Key() => Item(("pk", S("list")));
        await ddb.PutItemAsync(table, Item(("pk", S("list")), ("l", L(S("x"), S("y"), S("z")))));

        var resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Key(),
            UpdateExpression = "SET l[10] = :v",
            ExpressionAttributeValues = Item((":v", S("w"))),
            ReturnValues = ReturnValue.UPDATED_NEW,
        });
        Ensure(
            resp.Attributes is null,
            $"SET l[10] with ReturnValues=UPDATED_NEW returned Attributes {Py.Repr(resp.Attributes)}; real AWS returns none, because the element is appended at l[3] and the path l[10] it names does not exist after the write");
        var afterSet = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item["l"];
        Ensure(
            Strings(afterSet).SequenceEqual(AfterSet),
            $"after SET l[10] on a 3-element list the list is {Py.Repr(afterSet)}; expected the value APPENDED as the 4th element -- no padding, no sparse index");

        resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Key(),
            UpdateExpression = "REMOVE l[10]",
            ReturnValues = ReturnValue.ALL_NEW,
        });
        Ensure(
            Strings(resp.Attributes["l"]).SequenceEqual(AfterSet),
            $"REMOVE l[10] on a 4-element list returned ALL_NEW l={Py.Repr(resp.Attributes.GetValueOrDefault("l"))}; removing past the end is a no-op, not an error and not a truncation");

        resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Key(),
            UpdateExpression = "REMOVE l[1]",
            ReturnValues = ReturnValue.UPDATED_NEW,
        });
        Ensure(
            Py.Equal(resp.Attributes, Item(("l", L(S("z"))))),
            $"REMOVE l[1] with UPDATED_NEW returned {Py.Repr(resp.Attributes)}; expected l=[z], the element that shifted into index 1");
        var final = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item["l"];
        Ensure(
            Strings(final).SequenceEqual(AfterRemove),
            $"after REMOVE l[1] the list is {Py.Repr(final)}; expected the tail shifted left to [x, z, w] with no hole");
    }
}
