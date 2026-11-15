// Translated from behavioral/dynamodb/check_deep_dynamodb_2_set_nested_path_on_missing_parent_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.deep-dynamodb-2-set-nested-path-on-missing-parent-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeepDynamodb2SetNestedPathOnMissingParentRejected
{
    private const string Check = "deep-dynamodb-2-set-nested-path-on-missing-parent-rejected";
    private const string ErrValidation = "ValidationException";
    private const string MsgInvalidPath = "The document path provided in the update expression is invalid for update";

    /// <summary>
    /// SET parent.child is rejected when `parent` is absent; DynamoDB does not auto-vivify maps.
    ///
    /// The item stays unchanged, and the same expression succeeds where `parent` is a map.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.deep-dynamodb-2-set-nested-path-on-missing-parent-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = await Table(ctx, ddb);
        const string expr = "SET parent.child = :v";
        Dictionary<string, AttributeValue> Vals() => Item((":v", S("q")));

        // Positive control: same expression, parent exists as an (empty) map.
        await ddb.PutItemAsync(t, Item(("pk", S("has-parent")), ("parent", M())));
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = Item(("pk", S("has-parent"))),
            UpdateExpression = expr,
            ExpressionAttributeValues = Vals(),
        });
        var ok = await Stored(ddb, t, "has-parent");
        Ensure(
            Py.Equal(ok, Item(("pk", S("has-parent")), ("parent", M(("child", S("q")))))),
            $"positive control: SET parent.child on an existing map stored {Py.Repr(ok)}");

        // Broken: parent attribute absent.
        var original = Item(("pk", S("no-parent")), ("x", N("1")));
        await ddb.PutItemAsync(t, original);
        var (code, msg) = await Error(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = Item(("pk", S("no-parent"))),
            UpdateExpression = expr,
            ExpressionAttributeValues = Vals(),
        }));
        Ensure(code == ErrValidation, $"expected {ErrValidation}, got {Py.Repr(code)} ({Py.Repr(msg)})");
        Ensure(msg == MsgInvalidPath, $"expected message {Py.Repr(MsgInvalidPath)}, got {Py.Repr(msg)}");
        var after = await Stored(ddb, t, "no-parent");
        Ensure(Py.Equal(after, original), $"rejected update still changed the item: {Py.Repr(after)} (was {Py.Repr(original)})");

        // Same rejection when the item does not exist at all (upsert cannot vivify the parent either).
        (code, msg) = await Error(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = Item(("pk", S("absent-item"))),
            UpdateExpression = expr,
            ExpressionAttributeValues = Vals(),
        }));
        Ensure(code == ErrValidation, $"absent item: expected {ErrValidation}, got {Py.Repr(code)} ({Py.Repr(msg)})");
        Ensure(msg == MsgInvalidPath, $"absent item: expected {Py.Repr(MsgInvalidPath)}, got {Py.Repr(msg)}");
        Ensure(await Stored(ddb, t, "absent-item") is null, "rejected upsert created the item anyway");
    }

    private static async Task<string> Table(FidelityContext ctx, IAmazonDynamoDB ddb)
    {
        var name = FidelityContext.UniqueName("ddbexpr-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 250);
        return name;
    }

    private static async Task<Dictionary<string, AttributeValue>?> Stored(IAmazonDynamoDB ddb, string table, string pk) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;

    /// <summary>(code, message) of the ClientError fn raises. Raises if fn SUCCEEDS.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (resp, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"call was expected to be rejected but succeeded: {resp}");
        }

        return (error.Code, error.Message);
    }
}
