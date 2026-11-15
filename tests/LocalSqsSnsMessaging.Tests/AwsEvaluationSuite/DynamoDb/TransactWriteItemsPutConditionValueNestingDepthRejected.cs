// Translated from behavioral/dynamodb/check_transact_write_items_put_condition_value_nesting_depth_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-items-put-condition-value-nesting-depth-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteItemsPutConditionValueNestingDepthRejected
{
    private const string Check = "transact-write-items-put-condition-value-nesting-depth-rejected";
    private const string ErrValidation = "ValidationException";
    private const string NestingMessage =
        "Nesting Levels have exceeded supported limits: Attributes in the item have nested levels beyond supported limit";

    /// <summary>A chain of `levels` nested maps around one scalar leaf, `levels` + 1 deep in total.</summary>
    private static AttributeValue Deep(int levels)
    {
        var value = S("leaf");
        for (var i = 0; i < levels; i++)
        {
            value = M(("n", value));
        }

        return value;
    }

    /// <summary>(Error.Code, Error.Message) of the error fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be rejected, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }

    /// <summary>
    /// A 33-level value in a TransactWriteItems Put action's own ConditionExpression is a
    /// top-level ValidationException for the whole call; a 32-level one is accepted, and the
    /// refused call's sibling Update action never takes effect either.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-items-put-condition-value-nesting-depth-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-ddbtxnput-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 30);

        async Task<Dictionary<string, AttributeValue>?> Get(string pk) =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;

        // Positive control: a 32-level condition value on a Put action inside TransactWriteItems is
        // within the limit and the item lands.
        var atLimit = Deep(31);
        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem
                {
                    Put = new Put
                    {
                        TableName = t,
                        Item = Item(("pk", S("at-limit")), ("v", S("ok"))),
                        ConditionExpression = "attribute_not_exists(pk) OR :d = :d",
                        ExpressionAttributeValues = Item((":d", atLimit)),
                    },
                },
            ],
        });
        var ctl = await Get("at-limit");
        Ensure(
            Py.Equal(ctl, Item(("pk", S("at-limit")), ("v", S("ok")))),
            $"a 32-level Put condition value inside TransactWriteItems must be accepted, stored {Py.Repr(ctl)}");

        // Seed the sibling item the negative case's Update action targets.
        await ddb.PutItemAsync(t, Item(("pk", S("sibling")), ("ctr", N("1"))));

        var overLimit = Deep(32);
        var (code, message) = await Error(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem
                {
                    Put = new Put
                    {
                        TableName = t,
                        Item = Item(("pk", S("deep-put-target")), ("v", S("new"))),
                        ConditionExpression = "attribute_not_exists(pk) OR :d = :d",
                        ExpressionAttributeValues = Item((":d", overLimit)),
                    },
                },
                new TransactWriteItem
                {
                    Update = new Update
                    {
                        TableName = t,
                        Key = Item(("pk", S("sibling"))),
                        UpdateExpression = "SET ctr = :two",
                        ExpressionAttributeValues = Item((":two", N("2"))),
                    },
                },
            ],
        }));
        Ensure(
            code == ErrValidation,
            $"a 33-level Put condition value inside TransactWriteItems must be {ErrValidation} for the whole call, got {Py.Repr(code)}: {Py.Repr(message)}");
        Ensure(message == NestingMessage, $"expected the exact ValidationException message, got {Py.Repr(message)}");
        var deepPut = await Get("deep-put-target");
        Ensure(deepPut is null, $"the refused Put must not be written, got {Py.Repr(deepPut)}");
        var sibling = await Get("sibling");
        Ensure(
            Py.Equal(sibling, Item(("pk", S("sibling")), ("ctr", N("1")))),
            $"the transaction's own valid Update action must not take effect either, got {Py.Repr(sibling)}");
    }
}
