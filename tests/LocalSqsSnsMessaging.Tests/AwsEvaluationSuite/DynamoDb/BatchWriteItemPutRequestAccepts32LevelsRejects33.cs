// Translated from behavioral/dynamodb/check_batch_write_item_put_request_accepts_32_levels_rejects_33.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-write-item-put-request-accepts-32-levels-rejects-33</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchWriteItemPutRequestAccepts32LevelsRejects33
{
    private const string Check = "batch-write-item-put-request-accepts-32-levels-rejects-33";
    private const string ErrValidation = "ValidationException";
    private const string NestingMessage =
        "Nesting Levels have exceeded supported limits: Attributes in the item have nested levels " +
        "beyond supported limit";

    private static AttributeValue Leaf() => S("leaf");

    /// <summary>
    /// BatchWriteItem's PutRequest accepts a 32-level nested item, map- or list-shaped, with no
    /// UnprocessedItems, and it reads back intact; a 33-level list-nested item is refused for the
    /// whole call and nothing lands.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batch-write-item-put-request-accepts-32-levels-rejects-33")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-bwidepth-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        // --- 32-level map-nested PutRequest: accepted whole, no UnprocessedItems, lossless read-back ---
        var resp = await ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new() { [t] = [new WriteRequest { PutRequest = new PutRequest { Item = Item(("pk", S("m32")), ("d", WrapMaps(31, Leaf()))) } }] },
        });
        Ensure(
            resp.UnprocessedItems is { Count: 0 },
            "a 32-level map-nested PutRequest is within the limit, got UnprocessedItems " +
            $"{Py.Repr(resp.UnprocessedItems?.Keys)}");
        var value = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("m32"))), ConsistentRead = true })).Item?.GetValueOrDefault("d");
        var walked = 0;
        while (value?.M is { } inner)
        {
            var keys = inner.Keys.Order(StringComparer.Ordinal).ToList();
            Ensure(
                keys.SequenceEqual(["n"]),
                $"at wrapper {walked} the stored map must carry exactly the authored key, got {Py.Repr(keys)}");
            value = inner["n"];
            walked++;
        }

        Ensure(
            walked == 31 && Py.Equal(value, Leaf()),
            "a 32-level map document written by BatchWriteItem must read back 32 levels deep to the " +
            $"unchanged leaf, unwrapped {Py.Repr(walked)} levels before reaching {Py.Repr(value)}");

        // --- 32-level list-nested PutRequest: the positive control for the list-shaped refusal below ---
        resp = await ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new() { [t] = [new WriteRequest { PutRequest = new PutRequest { Item = Item(("pk", S("l32")), ("d", WrapLists(31, Leaf()))) } }] },
        });
        Ensure(
            resp.UnprocessedItems is { Count: 0 },
            "a 32-level list-nested PutRequest is within the limit, got UnprocessedItems " +
            $"{Py.Repr(resp.UnprocessedItems?.Keys)}");
        value = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("l32"))), ConsistentRead = true })).Item?.GetValueOrDefault("d");
        walked = 0;
        while (value?.L is { } list)
        {
            Ensure(
                list.Count == 1,
                $"at wrapper {walked} the stored list must carry exactly the authored element, got " +
                $"{Py.Repr(list.Count)} elements");
            value = list[0];
            walked++;
        }

        Ensure(
            walked == 31 && Py.Equal(value, Leaf()),
            "a 32-level list document written by BatchWriteItem must read back 32 levels deep to the " +
            $"unchanged leaf, unwrapped {Py.Repr(walked)} levels before reaching {Py.Repr(value)}");

        // --- 33-level list-nested PutRequest alongside a valid sibling: whole call refused, nothing lands ---
        var (code, message) = await Error(() => ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new()
            {
                [t] =
                [
                    new WriteRequest { PutRequest = new PutRequest { Item = Item(("pk", S("ok-sibling")), ("v", S("fine"))) } },
                    new WriteRequest { PutRequest = new PutRequest { Item = Item(("pk", S("l33")), ("d", WrapLists(32, Leaf()))) } },
                ],
            },
        }));
        Ensure(
            code == ErrValidation,
            $"a BatchWriteItem call with one 33-level list-nested item must be {ErrValidation} for " +
            $"the whole call, got {Py.Repr(code)}: {Py.Repr(message)}");
        Ensure(message == NestingMessage, $"expected the exact ValidationException message, got {Py.Repr(message)}");

        var sibling = await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("ok-sibling"))), ConsistentRead = true });
        Ensure(
            sibling.Item is null,
            "the request is rejected as a whole, so the valid sibling PutRequest must not be " +
            $"written, got {Py.Repr(sibling.Item)}");
        var deepItem = await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("l33"))), ConsistentRead = true });
        Ensure(deepItem.Item is null, $"the over-nested list PutRequest must not be written, got {Py.Repr(deepItem.Item)}");
    }

    /// <summary>`levels` nested single-key M wrappers around `leaf`, so the value is `levels` + 1 deep.</summary>
    private static AttributeValue WrapMaps(int levels, AttributeValue leaf)
    {
        var value = leaf;
        for (var i = 0; i < levels; i++)
        {
            value = M(("n", value));
        }

        return value;
    }

    /// <summary>`levels` nested single-element L wrappers around `leaf`.</summary>
    private static AttributeValue WrapLists(int levels, AttributeValue leaf)
    {
        var value = leaf;
        for (var i = 0; i < levels; i++)
        {
            value = L(value);
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
