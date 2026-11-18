// Translated from behavioral/dynamodb/check_nesting_depth_error_precedence_across_malformed_requests.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.nesting-depth-error-precedence-across-malformed-requests</summary>
[Category("AwsEvaluationSuite")]
public sealed class NestingDepthErrorPrecedenceAcrossMalformedRequests
{
    private const string CheckId = "nesting-depth-error-precedence-across-malformed-requests";
    private const string ErrValidation = "ValidationException";
    private const string MsgDepth = "1 validation error detected: Nesting Levels have exceeded supported limits: Attributes in the item have nested levels beyond supported limit";

    /// <summary>A chain of <paramref name="levels"/> maps around one scalar leaf, <c>levels + 1</c> levels deep in total.</summary>
    private static AttributeValue Nest(int levels)
    {
        var value = S("leaf");
        for (var i = 0; i < levels; i++)
        {
            value = M(("k", value));
        }

        return value;
    }

    /// <summary>fn()'s ValidationException (code, message); an unexpected success raises.</summary>
    private static async Task<(string? Code, string? Message)> CodeAndMessage<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"real AWS rejects this call, but it SUCCEEDED and returned {Py.Repr(result)}");
    }

    private static async Task<bool> Absent(IAmazonDynamoDB ddb, string table, string key) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(key))), ConsistentRead = true })).Item is null;

    /// <summary>
    /// A document 39 levels past the nesting limit still reports the nesting-limit error when
    /// paired with a bad ReturnValues or an empty expression attribute name, but a
    /// ConditionExpression syntax error or an unused expression value is masked by it instead.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ServiceQuotas.html#limits-attributes
    /// </summary>
    [Test]
    [DisplayName("dynamodb.nesting-depth-error-precedence-across-malformed-requests")]
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
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 30);

        AttributeValue Deep() => Nest(39);
        AttributeValue Shallow() => S("leaf");

        // Positive control: the mechanism itself works for a well-formed call.
        await ddb.PutItemAsync(table, Item(("pk", S("ctrl")), ("doc", Shallow())));
        Ensure(!await Absent(ddb, table, "ctrl"), "the positive control write must be stored");

        // Invalid ReturnValues: the enum-constraint error wins whether or not the item is deep.
        const string msgReturnValues = "1 validation error detected: Failed to satisfy constraint: Member must satisfy enum value set: [ALL_OLD, UPDATED_OLD, ALL_NEW, UPDATE_NEW, NONE]";
        foreach (var (key, doc) in new (string, Func<AttributeValue>)[] { ("a-combo", Deep), ("a-solo", Shallow) })
        {
            var (code, message) = await CodeAndMessage(() => ddb.PutItemAsync(new PutItemRequest
            {
                TableName = table,
                Item = Item(("pk", S(key)), ("doc", doc())),
                ReturnValues = new ReturnValue("BOGUS_VALUE"),
            }));
            Ensure(code == ErrValidation, $"{key}: expected {ErrValidation}, got {Py.Repr(code)}");
            Ensure(message == msgReturnValues, $"{key}: unexpected message {Py.Repr(message)}");
            Ensure(await Absent(ddb, table, key), $"{key}: rejected put must not store the item");
        }

        // Empty attribute name in ExpressionAttributeNames: also unaffected by depth.
        const string msgEmptyName = "1 validation error detected: ExpressionAttributeNames contains invalid value: Empty attribute name for key #x";
        foreach (var (key, doc) in new (string, Func<AttributeValue>)[] { ("b-combo", Deep), ("b-solo", Shallow) })
        {
            var (code, message) = await CodeAndMessage(() => ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = Item(("pk", S(key))),
                UpdateExpression = "SET #x = :v",
                ExpressionAttributeNames = Names(("#x", "")),
                ExpressionAttributeValues = Item((":v", doc())),
            }));
            Ensure(code == ErrValidation, $"{key}: expected {ErrValidation}, got {Py.Repr(code)}");
            Ensure(message == msgEmptyName, $"{key}: unexpected message {Py.Repr(message)}");
            Ensure(await Absent(ddb, table, key), $"{key}: rejected update must not create the item");
        }

        // ConditionExpression syntax error: masked by the nesting error once the item is deep.
        {
            var (code, message) = await CodeAndMessage(() => ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = Item(("pk", S("c-combo"))),
                UpdateExpression = "SET doc = :v",
                ConditionExpression = "attribute_exists(",
                ExpressionAttributeValues = Item((":v", Deep())),
            }));
            Ensure(code == ErrValidation, $"c-combo: expected {ErrValidation}, got {Py.Repr(code)}");
            Ensure(message == MsgDepth, $"c-combo: expected the nesting-depth message, got {Py.Repr(message)}");
            Ensure(await Absent(ddb, table, "c-combo"), "c-combo: rejected update must not create the item");

            (code, message) = await CodeAndMessage(() => ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = Item(("pk", S("c-solo"))),
                UpdateExpression = "SET doc = :v",
                ConditionExpression = "attribute_exists(",
                ExpressionAttributeValues = Item((":v", Shallow())),
            }));
            Ensure(code == ErrValidation, $"c-solo: expected {ErrValidation}, got {Py.Repr(code)}");
            Ensure(
                message == "1 validation error detected: Invalid ConditionExpression: Syntax error; token: \"<EOF>\", near: \"(\"",
                $"c-solo: unexpected message {Py.Repr(message)}");
            Ensure(await Absent(ddb, table, "c-solo"), "c-solo: rejected update must not create the item");

            // Unused ExpressionAttributeValue: also masked by the nesting error once the item is deep.
            (code, message) = await CodeAndMessage(() => ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = Item(("pk", S("d-combo"))),
                UpdateExpression = "SET doc = :v",
                ExpressionAttributeValues = Item((":v", Deep()), (":unused", S("x"))),
            }));
            Ensure(code == ErrValidation, $"d-combo: expected {ErrValidation}, got {Py.Repr(code)}");
            Ensure(message == MsgDepth, $"d-combo: expected the nesting-depth message, got {Py.Repr(message)}");
            Ensure(await Absent(ddb, table, "d-combo"), "d-combo: rejected update must not create the item");

            (code, message) = await CodeAndMessage(() => ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = Item(("pk", S("d-solo"))),
                UpdateExpression = "SET doc = :v",
                ExpressionAttributeValues = Item((":v", Shallow()), (":unused", S("x"))),
            }));
            Ensure(code == ErrValidation, $"d-solo: expected {ErrValidation}, got {Py.Repr(code)}");
            Ensure(
                message == "1 validation error detected: Value provided in ExpressionAttributeValues unused in expressions: keys: {:unused}",
                $"d-solo: unexpected message {Py.Repr(message)}");
            Ensure(await Absent(ddb, table, "d-solo"), "d-solo: rejected update must not create the item");
        }
    }
}
