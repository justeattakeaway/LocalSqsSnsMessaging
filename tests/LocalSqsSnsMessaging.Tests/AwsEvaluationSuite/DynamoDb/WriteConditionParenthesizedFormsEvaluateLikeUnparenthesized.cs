// Translated from behavioral/dynamodb/check_write_condition_parenthesized_forms_evaluate_like_unparenthesized.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.write-condition-parenthesized-forms-evaluate-like-unparenthesized</summary>
[Category("AwsEvaluationSuite")]
public sealed class WriteConditionParenthesizedFormsEvaluateLikeUnparenthesized
{
    private const string Check = "write-condition-parenthesized-forms-evaluate-like-unparenthesized";
    private static readonly (string Code, string Message) Ccf = ("ConditionalCheckFailedException", "The conditional request failed");

    /// <summary>
    /// A parenthesised ConditionExpression on PutItem, DeleteItem and UpdateItem evaluates
    /// like the unparenthesised form, accepting a true condition and rejecting a false one
    /// with ConditionalCheckFailedException while leaving the item unchanged.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.write-condition-parenthesized-forms-evaluate-like-unparenthesized")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-parens-");
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

        // --- PutItem: per-condition form "(a) AND (b)" ---
        var keyPut = Item(("pk", S("item-put")));
        var basePut = With(keyPut, ("ver", N("1")), ("status", S("A")));
        await ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = basePut });

        var wrongPut = With(basePut, ("touched", S("should-not-land")));
        await ExpectError(
            () => ddb.PutItemAsync(new PutItemRequest
            {
                TableName = table,
                Item = wrongPut,
                ConditionExpression = "(ver = :v) AND (#st = :s)",
                ExpressionAttributeNames = Names(("#st", "status")),
                ExpressionAttributeValues = Item((":v", N("99")), (":s", S("A"))),
            }),
            Ccf.Code,
            Ccf.Message);
        var unchanged = await Get(ddb, table, keyPut);
        Ensure(Py.Equal(unchanged, basePut), $"PutItem under a false parenthesised condition changed the item to {Py.Repr(unchanged)}");

        var rightPut = With(basePut, ("touched", S("yes")));
        await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = rightPut,
            ConditionExpression = "(ver = :v) AND (#st = :s)",
            ExpressionAttributeNames = Names(("#st", "status")),
            ExpressionAttributeValues = Item((":v", N("1")), (":s", S("A"))),
        });
        var landed = await Get(ddb, table, keyPut);
        Ensure(Py.Equal(landed, rightPut), $"PutItem under a true parenthesised condition left {Py.Repr(landed)}, expected {Py.Repr(rightPut)}");

        // --- DeleteItem: full-wrap form "(a AND b)" ---
        var keyDel = Item(("pk", S("item-del")));
        var baseDel = With(keyDel, ("ver", N("1")), ("status", S("A")));
        await ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = baseDel });

        await ExpectError(
            () => ddb.DeleteItemAsync(new DeleteItemRequest
            {
                TableName = table,
                Key = keyDel,
                ConditionExpression = "(ver = :v AND #st = :s)",
                ExpressionAttributeNames = Names(("#st", "status")),
                ExpressionAttributeValues = Item((":v", N("99")), (":s", S("A"))),
            }),
            Ccf.Code,
            Ccf.Message);
        var stillThere = await Get(ddb, table, keyDel);
        Ensure(Py.Equal(stillThere, baseDel), $"DeleteItem under a false parenthesised condition changed the item to {Py.Repr(stillThere)}");

        await ddb.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = table,
            Key = keyDel,
            ConditionExpression = "(ver = :v AND #st = :s)",
            ExpressionAttributeNames = Names(("#st", "status")),
            ExpressionAttributeValues = Item((":v", N("1")), (":s", S("A"))),
        });
        var gone = await Get(ddb, table, keyDel);
        Ensure(gone is null, $"DeleteItem under a true parenthesised condition left {Py.Repr(gone)}");

        // --- UpdateItem: nested non-redundant form "a AND (b)" ---
        var keyUpd = Item(("pk", S("item-upd")));
        var baseUpd = With(keyUpd, ("ver", N("1")), ("status", S("A")));
        await ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = baseUpd });

        await ExpectError(
            () => ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = keyUpd,
                UpdateExpression = "SET #st = :ns",
                ExpressionAttributeNames = Names(("#st", "status")),
                ConditionExpression = "ver = :v AND (#st = :s)",
                ExpressionAttributeValues = Item((":v", N("99")), (":s", S("A")), (":ns", S("B"))),
            }),
            Ccf.Code,
            Ccf.Message);
        var notUpdated = await Get(ddb, table, keyUpd);
        Ensure(Py.Equal(notUpdated, baseUpd), $"UpdateItem under a false parenthesised condition changed the item to {Py.Repr(notUpdated)}");

        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = keyUpd,
            UpdateExpression = "SET #st = :ns",
            ExpressionAttributeNames = Names(("#st", "status")),
            ConditionExpression = "ver = :v AND (#st = :s)",
            ExpressionAttributeValues = Item((":v", N("1")), (":s", S("A")), (":ns", S("B"))),
        });
        var updated = await Get(ddb, table, keyUpd);
        Ensure(
            Py.Equal(updated, With(keyUpd, ("ver", N("1")), ("status", S("B")))),
            $"UpdateItem under a true parenthesised condition left {Py.Repr(updated)}");
    }

    private static Dictionary<string, AttributeValue> With(Dictionary<string, AttributeValue> item, params (string Name, AttributeValue Value)[] entries)
    {
        var copy = new Dictionary<string, AttributeValue>(item, StringComparer.Ordinal);
        foreach (var (name, value) in entries)
        {
            copy[name] = value;
        }

        return copy;
    }

    private static async Task<Dictionary<string, AttributeValue>?> Get(IAmazonDynamoDB ddb, string table, Dictionary<string, AttributeValue> key) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;

    /// <summary>Assert fn() raises exactly (code, message).</summary>
    private static async Task ExpectError(Func<Task> fn, string code, string message)
    {
        var error = await FidelityContext.CaptureError(fn) ?? throw Fail("expected the call to be refused, but it succeeded");
        var got = (error.Code, error.Message);
        Ensure(got == (code, message), $"expected {Py.Repr((code, message))}, got {Py.Repr(got)}");
    }
}
