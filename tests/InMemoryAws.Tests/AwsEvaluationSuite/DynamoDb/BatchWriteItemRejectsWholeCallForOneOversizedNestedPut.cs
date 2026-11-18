// Translated from behavioral/dynamodb/check_batch_write_item_rejects_whole_call_for_one_oversized_nested_put.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-write-item-rejects-whole-call-for-one-oversized-nested-put</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchWriteItemRejectsWholeCallForOneOversizedNestedPut
{
    private const string Check = "batch-write-item-rejects-whole-call-for-one-oversized-nested-put";
    private const string ErrValidation = "ValidationException";
    private const string NestingMessage =
        "Nesting Levels have exceeded supported limits: Attributes in the item have nested levels " +
        "beyond supported limit";

    /// <summary>
    /// BatchWriteItem refuses the whole call ValidationException when one PutRequest nests 33
    /// levels deep, and the request's valid sibling PutRequest is not written either.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batch-write-item-rejects-whole-call-for-one-oversized-nested-put")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-ddbbatch-");
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

        var deep = Deep(32); // 33 levels, one past the limit.
        var (code, message) = await Error(() => ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new()
            {
                [t] =
                [
                    new WriteRequest { PutRequest = new PutRequest { Item = Item(("pk", S("ok-sibling")), ("v", S("fine"))) } },
                    new WriteRequest { PutRequest = new PutRequest { Item = Item(("pk", S("too-deep")), ("v", deep)) } },
                ],
            },
        }));
        Ensure(
            code == ErrValidation,
            $"a BatchWriteItem call with one 33-level item must be {ErrValidation} for the whole " +
            $"call, got {Py.Repr(code)}: {Py.Repr(message)}");
        Ensure(message == NestingMessage, $"expected the exact ValidationException message, got {Py.Repr(message)}");

        var sibling = await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("ok-sibling"))), ConsistentRead = true });
        Ensure(
            sibling.Item is null,
            "the request is rejected as a whole, so the valid sibling PutRequest must not be " +
            $"written, got {Py.Repr(sibling.Item)}");
        var deepItem = await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("too-deep"))), ConsistentRead = true });
        Ensure(deepItem.Item is null, $"the over-nested PutRequest must not be written, got {Py.Repr(deepItem.Item)}");
    }

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

    /// <summary>(Error.Code, Error.Message) of the ClientError fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be rejected, but it succeeded: {Py.Repr(result)}");
    }
}
