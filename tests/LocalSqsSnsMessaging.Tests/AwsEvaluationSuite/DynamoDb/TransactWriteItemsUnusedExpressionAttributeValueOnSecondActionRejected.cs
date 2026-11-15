// Translated from behavioral/dynamodb/check_transact_write_items_unused_expression_attribute_value_on_second_action_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-items-unused-expression-attribute-value-on-second-action-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteItemsUnusedExpressionAttributeValueOnSecondActionRejected
{
    private const string Check = "transact-write-items-unused-expression-attribute-value-on-second-action-rejected";
    private const string ErrValidation = "ValidationException";
    private const string ExpectedMessage = "Value provided in ExpressionAttributeValues unused in expressions: keys: {:extra}";
    private static readonly string[] Kinds = ["Put", "Update", "Delete", "ConditionCheck"];

    /// <summary>(code, message) of the ClientError fn() raises; RAISES if the call unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, err) = await FidelityContext.Capture(fn);
        if (err is not null)
        {
            return (err.Code, err.Message);
        }

        throw Fail($"call unexpectedly SUCCEEDED where ValidationException was required: {result}");
    }

    /// <summary>A second TransactItems entry of <paramref name="kind"/> carrying one unused ExpressionAttributeValues entry.</summary>
    private static TransactWriteItem SecondActionWithStrayValue(string kind, string table)
    {
        if (kind == "Update")
        {
            return new TransactWriteItem
            {
                Update = new Update
                {
                    TableName = table,
                    Key = Item(("pk", S("target"))),
                    UpdateExpression = "SET v = :v",
                    ExpressionAttributeValues = Item((":v", N("1")), (":extra", N("9"))),
                },
            };
        }

        if (kind == "Put")
        {
            return new TransactWriteItem
            {
                Put = new Put
                {
                    TableName = table,
                    Item = Item(("pk", S("target"))),
                    ConditionExpression = "attribute_not_exists(pk) OR :x = :x",
                    ExpressionAttributeValues = Item((":x", S("z")), (":extra", N("9"))),
                },
            };
        }

        if (kind == "Delete")
        {
            return new TransactWriteItem
            {
                Delete = new Delete
                {
                    TableName = table,
                    Key = Item(("pk", S("target"))),
                    ConditionExpression = "attribute_exists(pk)",
                    ExpressionAttributeValues = Item((":extra", N("9"))),
                },
            };
        }

        return new TransactWriteItem
        {
            ConditionCheck = new ConditionCheck
            {
                TableName = table,
                Key = Item(("pk", S("target"))),
                ConditionExpression = "attribute_exists(pk)",
                ExpressionAttributeValues = Item((":extra", N("9"))),
            },
        };
    }

    /// <summary>
    /// An ExpressionAttributeValues entry no expression references is rejected whole even when it
    /// sits only on the second of two TransactWriteItems actions; the first, valid action does not
    /// commit either.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_TransactWriteItems.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-items-unused-expression-attribute-value-on-second-action-rejected")]
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
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 30);
        await ddb.PutItemAsync(table, Item(("pk", S("target"))));

        // Positive control: the same four kinds, minus the stray entry, commit as the second action.
        foreach (var kind in Kinds)
        {
            var firstKey = $"control-first-{kind}";
            TransactWriteItem second;
            if (kind == "Put")
            {
                second = new TransactWriteItem
                {
                    Put = new Put { TableName = table, Item = Item(("pk", S("target")), ("touched", S(kind))) },
                };
            }
            else if (kind == "Update")
            {
                second = new TransactWriteItem
                {
                    Update = new Update
                    {
                        TableName = table,
                        Key = Item(("pk", S("target"))),
                        UpdateExpression = "SET v = :v",
                        ExpressionAttributeValues = Item((":v", N("1"))),
                    },
                };
            }
            else if (kind == "Delete")
            {
                second = new TransactWriteItem
                {
                    Delete = new Delete { TableName = table, Key = Item(("pk", S($"to-delete-{kind}"))) },
                };
                await ddb.PutItemAsync(table, Item(("pk", S($"to-delete-{kind}"))));
            }
            else
            {
                second = new TransactWriteItem
                {
                    ConditionCheck = new ConditionCheck
                    {
                        TableName = table,
                        Key = Item(("pk", S("target"))),
                        ConditionExpression = "attribute_exists(pk)",
                    },
                };
            }

            await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems =
                [
                    new TransactWriteItem { Put = new Put { TableName = table, Item = Item(("pk", S(firstKey))) } },
                    second,
                ],
            });
            var got = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(firstKey))), ConsistentRead = true });
            Ensure(got.Item is not null, $"positive control for {kind}: first action {Py.Repr(firstKey)} must commit");
        }

        foreach (var kind in Kinds)
        {
            var firstKey = $"rejected-first-{kind}";
            var first = new TransactWriteItem { Put = new Put { TableName = table, Item = Item(("pk", S(firstKey))) } };
            var second = SecondActionWithStrayValue(kind, table);
            var (code, message) = await Error(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems = [first, second],
            }));
            Ensure(
                code == ErrValidation,
                $"a stray ExpressionAttributeValues entry on the second ({kind}) action answered {Py.Repr(code)} ({Py.Repr(message)}), expected {ErrValidation}");
            Ensure(
                message == ExpectedMessage,
                $"second action {kind}: expected message {Py.Repr(ExpectedMessage)}, got {Py.Repr(message)}");
            var got = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(firstKey))), ConsistentRead = true });
            Ensure(
                got.Item is null,
                $"a transaction rejected for a stray ExpressionAttributeValues entry on its second ({kind}) action must not write its first action either, got {Py.Repr(got.Item)}");
        }
    }
}
