// Translated from behavioral/dynamodb/check_item_size_limit_is_aggregate_not_per_attribute_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.item-size-limit-is-aggregate-not-per-attribute-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ItemSizeLimitIsAggregateNotPerAttributeRejected
{
    private const string CheckId = "item-size-limit-is-aggregate-not-per-attribute-rejected";
    private const string ErrValidation = "ValidationException";
    private const int KB = 1024;
    private static readonly string[] FewMembers = ["a0", "a1", "a2", "a3", "a4", "a5", "a6", "a7", "a8", "pk"];

    /// <summary>The AWS error code and message <paramref name="fn"/> raises; an unexpected success fails the check.</summary>
    private static async Task<(string? Code, string? Message)> ErrorCode(Func<Task> fn, string what)
    {
        var error = await FidelityContext.CaptureError(fn);
        return error is null ? throw Fail($"{what}: the call was ACCEPTED; real AWS rejects it") : (error.Code, error.Message);
    }

    /// <summary>
    /// The 400 KB item limit is an aggregate: a ~380 KB item round-trips intact and nine 40 KB
    /// attributes are accepted, while a ~410 KB attribute or eleven 40 KB attributes are refused
    /// ValidationException with AWS's message and store nothing.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.item-size-limit-is-aggregate-not-per-attribute-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-size-");
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

        async Task<GetItemResponse> Get(string pk) =>
            await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S(pk))), ConsistentRead = true });

        // --- under the ceiling: accepted AND lossless ---
        var under = new string('x', 380 * KB);
        await ddb.PutItemAsync(t, Item(("pk", S("under")), ("d", S(under))));
        var got = (await Get("under")).Item ?? [];
        var stored = got.GetValueOrDefault("d")?.S;
        Ensure(stored is not null, $"a ~380KB item is under the ceiling and must be readable, got dict_keys({Py.Repr(got.Keys.ToList())})");
        Ensure(
            stored.Length == under.Length,
            $"a ~380KB value must be stored at its full length {Py.Repr(under.Length)}, got {Py.Repr(stored.Length)} -- the store truncated it");
        Ensure(stored == under, "a ~380KB value must read back byte-identical; the returned value has the right length but different content");

        // --- over the ceiling in one attribute: refused, and nothing lands ---
        var over = new string('x', 410 * KB);
        var (code, message) = await ErrorCode(
            () => ddb.PutItemAsync(t, Item(("pk", S("over")), ("d", S(over)))),
            "PutItem with a single ~410KB attribute");
        Ensure(code == ErrValidation, $"a ~410KB item exceeds the 400KB ceiling and must be refused {ErrValidation}, got {Py.Repr(code)}");
        Ensure(message == "Item size has exceeded the maximum allowed size", $"expected AWS's exact ValidationException message, got {Py.Repr(message)}");
        var absent = await Get("over");
        Ensure(absent.Item is null, $"the refused ~410KB write must leave nothing behind, got {Py.Repr(absent.Item)}");

        // --- over the ceiling in aggregate, with every single attribute far below it ---
        var chunk = new string('y', 40 * KB);
        var many = Item(("pk", S("many")));
        for (var i = 0; i < 11; i++)
        {
            many[$"a{i}"] = S(chunk);
        }

        (code, message) = await ErrorCode(() => ddb.PutItemAsync(t, many), "PutItem with eleven 40KB attributes");
        Ensure(
            code == ErrValidation,
            $"eleven 40KB attributes are ~440KB in aggregate and must be refused {ErrValidation} even though no single attribute is near the ceiling, got {Py.Repr(code)}");
        Ensure(message == "Item size has exceeded the maximum allowed size", $"expected AWS's exact ValidationException message, got {Py.Repr(message)}");
        absent = await Get("many");
        Ensure(absent.Item is null, $"the refused 11x40KB write must leave nothing behind -- not even a partial item -- got {Py.Repr(absent.Item)}");

        // Control: without it the 11-attribute rejection is also consistent with a per-attribute limit.
        var few = Item(("pk", S("few")));
        for (var i = 0; i < 9; i++)
        {
            few[$"a{i}"] = S(chunk);
        }

        await ddb.PutItemAsync(t, few);
        got = (await Get("few")).Item ?? [];
        var sorted = got.Keys.Order(StringComparer.Ordinal).ToList();
        Ensure(sorted.SequenceEqual(FewMembers), $"nine 40KB attributes are ~360KB in aggregate and must all be accepted and readable, got {Py.Repr(sorted)}");
        Ensure(Enumerable.Range(0, 9).All(i => Py.Equal(got[$"a{i}"], S(chunk))), "every one of the nine accepted 40KB attributes must read back intact");
    }
}
