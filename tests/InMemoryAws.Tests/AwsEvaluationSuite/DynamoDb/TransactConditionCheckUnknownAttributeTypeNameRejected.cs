// Translated from behavioral/dynamodb/check_transact_condition_check_unknown_attribute_type_name_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-condition-check-unknown-attribute-type-name-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactConditionCheckUnknownAttributeTypeNameRejected
{
    private const string Check = "transact-condition-check-unknown-attribute-type-name-rejected";
    private const string Canceled = "TransactionCanceledException";
    private const string CanceledPrefix = "Transaction cancelled, please refer cancellation reasons for specific reasons ";
    private const string Validation = "ValidationException";

    private static string UnknownType(string name) =>
        $"Invalid ConditionExpression: Invalid attribute type name found; type: {name}, valid types: {{S,SS,N,NS,B,BS,BOOL,NULL,L,M}}";

    /// <summary>
    /// attribute_type naming a type outside the case-sensitive set refuses the whole TransactWriteItems
    /// call, while a valid name that does not match cancels it as a condition failure.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.OperatorsAndFunctions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-condition-check-unknown-attribute-type-name-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(t), "ResourceNotFoundException"));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 150);

        var stored = Item(("pk", S("doc")), ("s", S("text")));
        await ddb.PutItemAsync(t, stored);

        TransactWriteItemsRequest Items(string typeName) => new()
        {
            TransactItems =
            [
                new TransactWriteItem { Put = new Put { TableName = t, Item = Item(("pk", S("sibling")), ("v", N("1"))) } },
                new TransactWriteItem
                {
                    ConditionCheck = new ConditionCheck
                    {
                        TableName = t,
                        Key = Item(("pk", S("doc"))),
                        ConditionExpression = "attribute_type(s, :t)",
                        ExpressionAttributeValues = Item((":t", S(typeName))),
                    },
                },
            ],
        };

        foreach (var name in new[] { "X", "s" })
        {
            var refused = await Refusal(() => ddb.TransactWriteItemsAsync(Items(name)));
            Ensure(refused.Code == Validation, $"type {Py.Repr(name)}: expected {Validation}, got {refused}");
            Ensure(
                refused.Message == UnknownType(name),
                $"type {Py.Repr(name)}: expected {Py.Repr(UnknownType(name))}, got {Py.Repr(refused.Message)}");
            Ensure(
                refused.CancellationReasons() is null,
                $"type {Py.Repr(name)}: a request-level refusal carried cancellation reasons {Py.Repr(Reasons(refused))}");
        }

        // Legal neighbour: a valid type name that does not match the stored type is a condition failure.
        var err = await Refusal(() => ddb.TransactWriteItemsAsync(Items("N")));
        Ensure(err.Code == Canceled, $"type N: expected {Canceled}, got {err}");
        Ensure(err.Message == CanceledPrefix + "[None, ConditionalCheckFailed]", $"type N: message was {Py.Repr(err.Message)}");
        Ensure(
            Py.Equal(Reasons(err), new[] { R("None"), R("ConditionalCheckFailed", "The conditional request failed") }),
            $"type N: cancellation reasons were {Py.Repr(Reasons(err))}");

        var sibling = await Get(ddb, t, "sibling");
        Ensure(sibling is null, $"a refused transaction wrote its sibling Put: {Py.Repr(sibling)}");

        // Positive control: the matching type name lets the transaction commit.
        await ddb.TransactWriteItemsAsync(Items("S"));
        sibling = await Get(ddb, t, "sibling");
        Ensure(Py.Equal(sibling, Item(("pk", S("sibling")), ("v", N("1")))), $"the committed sibling read {Py.Repr(sibling)}");
        var after = await Get(ddb, t, "doc");
        Ensure(Py.Equal(after, stored), $"a ConditionCheck changed the item it checked: {Py.Repr(after)}");
    }

    private static async Task<Dictionary<string, AttributeValue>?> Get(IAmazonDynamoDB ddb, string table, string pk) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;

    /// <summary>The cancellation reasons as boto3 hands them back: each with only the members present.</summary>
    private static List<Dictionary<string, object?>>? Reasons(AwsError err) => err.CancellationReasons()?.Select(Reason).ToList();

    /// <summary>A cancellation reason as boto3 hands it back.</summary>
    private static Dictionary<string, object?> Reason(CancellationReason reason)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (reason.Item is not null)
        {
            result["Item"] = reason.Item;
        }

        if (reason.Code is not null)
        {
            result["Code"] = reason.Code;
        }

        if (reason.Message is not null)
        {
            result["Message"] = reason.Message;
        }

        return result;
    }

    /// <summary>A reason as the check writes it: <c>{'Code': code, 'Message': message}</c>, leaving out what is null.</summary>
    private static Dictionary<string, object?> R(string code, string? message = null, Dictionary<string, AttributeValue>? item = null)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (item is not null)
        {
            result["Item"] = item;
        }

        result["Code"] = code;
        if (message is not null)
        {
            result["Message"] = message;
        }

        return result;
    }

    /// <summary>The error <paramref name="fn"/> raises. An unexpected success raises too, naming what came back.</summary>
    private static async Task<AwsError> Refusal(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        return error ?? throw Fail("expected the call to be refused, but it succeeded");
    }

    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };
}
