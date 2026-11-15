// Translated from behavioral/dynamodb/check_nesting_depth_limit_enforced_at_32_levels.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.nesting-depth-limit-enforced-at-32-levels</summary>
[Category("AwsEvaluationSuite")]
public sealed class NestingDepthLimitEnforcedAt32Levels
{
    private const string CheckId = "nesting-depth-limit-enforced-at-32-levels";
    private const string ErrValidation = "ValidationException";
    private const string DepthMessage = "1 validation error detected: Nesting Levels have exceeded supported limits: Attributes in the item have nested levels beyond supported limit";

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

    /// <summary>Return fn()'s error code and message; an unexpected success raises AssertionError.</summary>
    private static async Task<(string? Code, string? Message)> Code<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"real AWS rejects this call, but it SUCCEEDED and returned {Py.Repr(result)}");
    }

    /// <summary>
    /// A 32-level attribute value is accepted and reads back at full depth; a 33-level map or list
    /// chain is rejected with ValidationException and the rejected map item is not stored.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.nesting-depth-limit-enforced-at-32-levels")]
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
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        // 31 wrapping maps + the leaf = 32 levels, the deepest accepted document.
        await ddb.PutItemAsync(t, Item(("pk", S("deepest")), ("v", Nest(31))));
        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("deepest"))), ConsistentRead = true })).Item ?? [];
        var cursor = got.GetValueOrDefault("v");
        for (var level = 0; level < 31; level++)
        {
            Ensure(cursor?.M is not null, $"stored document must preserve all 31 map levels; level {level} was {Py.Repr(cursor)}");
            cursor = cursor.M.GetValueOrDefault("k");
        }

        Ensure(Py.Equal(cursor, S("leaf")), $"the leaf at depth 32 must round-trip, got {Py.Repr(cursor)}");

        // One level deeper is 33 and must be refused.
        var (code, message) = await Code(() => ddb.PutItemAsync(t, Item(("pk", S("too-deep")), ("v", Nest(32)))));
        Ensure(code == ErrValidation, $"a 33-level document must be {ErrValidation}, got {Py.Repr(code)}");
        Ensure(message == DepthMessage, $"expected AWS's exact ValidationException message, got {Py.Repr(message)}");
        Ensure(
            (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("too-deep"))), ConsistentRead = true })).Item is null,
            "a document rejected for nesting depth must not be stored");

        // Lists count toward depth the same way maps do.
        var deepList = S("leaf");
        for (var i = 0; i < 32; i++)
        {
            deepList = L(deepList);
        }

        (code, message) = await Code(() => ddb.PutItemAsync(t, Item(("pk", S("deep-list")), ("v", deepList))));
        Ensure(code == ErrValidation, $"a 33-level list chain must be {ErrValidation}, got {Py.Repr(code)}");
        Ensure(message == DepthMessage, $"expected AWS's exact ValidationException message, got {Py.Repr(message)}");
    }
}
