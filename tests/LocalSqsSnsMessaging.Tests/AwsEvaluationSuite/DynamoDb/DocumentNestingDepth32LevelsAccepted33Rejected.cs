// Translated from behavioral/dynamodb/check_document_nesting_depth_32_levels_accepted_33_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.document-nesting-depth-32-levels-accepted-33-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DocumentNestingDepth32LevelsAccepted33Rejected
{
    private const string CheckId = "document-nesting-depth-32-levels-accepted-33-rejected";
    private const string ErrValidation = "ValidationException";
    private const string NestingMessage = "1 validation error detected: Nesting Levels have exceeded supported limits: Attributes in the item have nested levels beyond supported limit";

    private static AttributeValue Leaf() => S("leaf");

    /// <summary>
    /// Document nesting is limited to 32 levels inclusive, for maps and lists alike.
    ///
    /// A 32-level item is stored and reads back intact to its leaf; a 33-level one is a
    /// ValidationException and nothing is written.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.document-nesting-depth-32-levels-accepted-33-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-nest-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        // --- 32 levels (31 map wrappers + the scalar leaf): accepted and lossless ---
        await ddb.PutItemAsync(t, Item(("pk", S("m32")), ("d", WrapMaps(31, Leaf()))));
        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("m32"))), ConsistentRead = true })).Item ?? [];
        var value = got.GetValueOrDefault("d");
        Ensure(value is not null, $"a 32-level document is within the limit and must be readable, got attributes {Py.Repr(got.Keys.Order(StringComparer.Ordinal).ToList())}");
        var walked = 0;
        while (value?.M is { } inner)
        {
            var innerKeys = inner.Keys.Order(StringComparer.Ordinal).ToList();
            Ensure(innerKeys.SequenceEqual(["n"]), $"at wrapper {walked} the stored map must carry exactly the authored key, got {Py.Repr(innerKeys)}");
            value = inner["n"];
            walked++;
        }

        Ensure(walked == 31, $"a 32-level document must come back 32 levels deep: expected to unwrap 31 maps, unwrapped {Py.Repr(walked)} before reaching {Py.Repr(value)}");
        Ensure(Py.Equal(value, Leaf()), $"the scalar at the bottom of a 32-level document must round-trip unchanged, got {Py.Repr(value)}");

        // --- 33 levels via maps: refused, nothing lands ---
        var (code, message) = await ErrorCode(
            () => ddb.PutItemAsync(t, Item(("pk", S("m33")), ("d", WrapMaps(32, Leaf())))),
            "PutItem with a 33-level chain of maps");
        Ensure(code == ErrValidation, $"a 33-level document exceeds the 32-level limit and must be refused {ErrValidation}, got {Py.Repr(code)}");
        Ensure(message == NestingMessage, $"expected the exact ValidationException message AWS returns, got {Py.Repr(message)}");
        var absent = await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("m33"))), ConsistentRead = true });
        Ensure(absent.Item is null, $"the refused 33-level write must leave nothing behind -- not a truncated document -- got {Py.Repr(absent.Item)}");

        // --- the same boundary reached through lists, not maps ---
        await ddb.PutItemAsync(t, Item(("pk", S("l32")), ("d", WrapLists(31, Leaf()))));
        got = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("l32"))), ConsistentRead = true })).Item ?? [];
        value = got.GetValueOrDefault("d");
        Ensure(value is not null, $"a 32-level chain of lists is within the limit and must be readable, got {Py.Repr(got.Keys.Order(StringComparer.Ordinal).ToList())}");
        walked = 0;
        while (value?.L is { } list)
        {
            Ensure(list.Count == 1, $"at wrapper {walked} the stored list must carry exactly the authored element, got {Py.Repr(list.Count)} elements");
            value = list[0];
            walked++;
        }

        Ensure(walked == 31, $"a 32-level chain of lists must come back 32 levels deep: unwrapped {Py.Repr(walked)} lists before reaching {Py.Repr(value)}");
        Ensure(Py.Equal(value, Leaf()), $"the scalar at the bottom of a 32-level list chain must round-trip unchanged, got {Py.Repr(value)}");

        (code, message) = await ErrorCode(
            () => ddb.PutItemAsync(t, Item(("pk", S("l33")), ("d", WrapLists(32, Leaf())))),
            "PutItem with a 33-level chain of lists");
        Ensure(code == ErrValidation, $"a 33-level chain of lists exceeds the same 32-level limit and must be refused {ErrValidation}, got {Py.Repr(code)}");
        Ensure(message == NestingMessage, $"expected the exact ValidationException message AWS returns, got {Py.Repr(message)}");
        absent = await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("l33"))), ConsistentRead = true });
        Ensure(absent.Item is null, $"the refused 33-level list write must leave nothing behind, got {Py.Repr(absent.Item)}");

        // --- the same boundary reached through an ExpressionAttributeValue on a PutItem condition ---
        (code, message) = await ErrorCode(
            () => ddb.PutItemAsync(new PutItemRequest
            {
                TableName = t,
                Item = Item(("pk", S("cond33")), ("d", Leaf())),
                ConditionExpression = "attribute_not_exists(pk) OR :v = :v",
                ExpressionAttributeValues = Item((":v", WrapMaps(32, Leaf()))),
            }),
            "PutItem whose ConditionExpression carries a 33-level EAV");
        Ensure(code == ErrValidation, $"a 33-level value in a PutItem ConditionExpression exceeds the limit and must be refused {ErrValidation}, got {Py.Repr(code)}");
        Ensure(message == NestingMessage, $"expected the exact ValidationException message AWS returns, got {Py.Repr(message)}");
        absent = await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("cond33"))), ConsistentRead = true });
        Ensure(absent.Item is null, $"the refused condition-side 33-level write must leave nothing behind, got {Py.Repr(absent.Item)}");

        // --- the same boundary reached through an ExpressionAttributeValue on an UpdateItem SET ---
        (code, message) = await ErrorCode(
            () => ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = t,
                Key = Item(("pk", S("m32"))),
                UpdateExpression = "SET e = :v",
                ExpressionAttributeValues = Item((":v", WrapMaps(32, Leaf()))),
            }),
            "UpdateItem SET with a 33-level EAV");
        Ensure(code == ErrValidation, $"a 33-level EAV in an UpdateItem SET exceeds the limit and must be refused {ErrValidation}, got {Py.Repr(code)}");
        Ensure(message == NestingMessage, $"expected the exact ValidationException message AWS returns, got {Py.Repr(message)}");
        var untouched = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("m32"))), ConsistentRead = true })).Item ?? [];
        Ensure(!untouched.ContainsKey("e"), $"the refused UpdateItem SET must leave the existing item unchanged, got attribute 'e' in {Py.Repr(untouched.Keys.Order(StringComparer.Ordinal).ToList())}");
        Ensure(
            Py.Equal(untouched.GetValueOrDefault("d"), WrapMaps(31, Leaf())),
            $"the existing item's other attribute must be unaffected by the refused update, got {Py.Repr(untouched.GetValueOrDefault("d"))}");
    }

    /// <summary>The AWS error code and message `fn` raises; an unexpected success raises AssertionError.</summary>
    private static async Task<(string? Code, string? Message)> ErrorCode(Func<Task> fn, string what)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is null)
        {
            throw Fail($"{what}: the call was ACCEPTED; real AWS rejects it");
        }

        return (error.Code, error.Message);
    }

    /// <summary>`levels` nested M wrappers around `leaf`, so the document is `levels` + 1 deep.</summary>
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
}
