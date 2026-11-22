// Translated from behavioral/dynamodb/check_putitem_condition_contains_partial_map_in_list_is_false.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.putitem-condition-contains-partial-map-in-list-is-false</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutitemConditionContainsPartialMapInListIsFalse
{
    private const string Check = "putitem-condition-contains-partial-map-in-list-is-false";

    private static AttributeValue Role() => M(("name", S("Jane Doe")), ("title", S("admin")));

    private static Dictionary<string, AttributeValue> Seed() => Item(
        ("id", S("roster")),
        ("roles", L(Role(), S("sentinel"))),
        ("version", N("1")));

    /// <summary>
    /// contains(list, map) requires an exact element match: a map operand holding only a subset
    /// of a list element's keys evaluates false and fails the PutItem condition; the same operand
    /// completed to an exact match evaluates true and the write goes through.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.putitem-condition-contains-partial-map-in-list-is-false")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var name = await Table(ctx, ddb);
        Dictionary<string, AttributeValue> Key() => Item(("id", S("roster")));

        await ddb.PutItemAsync(name, Seed());
        var seeded = (await ddb.GetItemAsync(new GetItemRequest { TableName = name, Key = Key(), ConsistentRead = true })).Item;
        Ensure(Py.Equal(seeded, Seed()), $"seed GetItem returned {Py.Repr(seeded)}, expected {Py.Repr(Seed())}");

        var partial = M(("name", S("Jane Doe")));
        var replacement = Item(("id", S("roster")), ("version", N("2")), ("updated", Bool(true)));
        var (code, msg) = await Error(() => ddb.PutItemAsync(new PutItemRequest
        {
            TableName = name,
            Item = replacement,
            ConditionExpression = "contains(#r, :v)",
            ExpressionAttributeNames = Names(("#r", "roles")),
            ExpressionAttributeValues = Item((":v", partial)),
        }));
        Ensure(
            code == "ConditionalCheckFailedException",
            $"PutItem under contains(roles, {Py.Repr(partial)}) raised {Py.Repr(code)}, expected " +
            "ConditionalCheckFailedException");
        const string expectedMsg = "The conditional request failed";
        Ensure(msg == expectedMsg, $"expected message {Py.Repr(expectedMsg)}, got {Py.Repr(msg)}");
        var unchanged = (await ddb.GetItemAsync(new GetItemRequest { TableName = name, Key = Key(), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(unchanged, Seed()),
            $"PutItem under a failing partial-map contains() left {Py.Repr(unchanged)}, expected the " +
            $"unchanged seed {Py.Repr(Seed())}");

        // Positive control: the same operand completed into an exact match of the list element.
        var full = Role();
        await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = name,
            Item = replacement,
            ConditionExpression = "contains(#r, :v)",
            ExpressionAttributeNames = Names(("#r", "roles")),
            ExpressionAttributeValues = Item((":v", full)),
        });
        var written = (await ddb.GetItemAsync(new GetItemRequest { TableName = name, Key = Key(), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(written, replacement),
            $"PutItem under a holding exact-map contains() left {Py.Repr(written)}, expected {Py.Repr(replacement)}");
    }

    /// <summary>The AWS error (code, message) <c>fn</c> raises; a call that succeeds raises instead.</summary>
    private static async Task<(string? Code, string Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error is not null
            ? (error.Code, error.Message ?? "")
            : throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
    }

    /// <summary>Delete a table; one already gone counts as deleted, any other failure raises.</summary>
    private static async Task Drop(IAmazonDynamoDB ddb, string name)
    {
        var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(name));
        if (error is not null && error.Code != "ResourceNotFoundException")
        {
            throw error.Exception;
        }
    }

    /// <summary>Create an on-demand table with a string <c>id</c> hash key and wait for ACTIVE.</summary>
    private static async Task<string> Table(FidelityContext ctx, IAmazonDynamoDB ddb)
    {
        var name = FidelityContext.UniqueName("sim2real-ddb-contains-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            AttributeDefinitions = [new AttributeDefinition("id", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("id", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => Drop(ddb, name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 60);
        return name;
    }
}
