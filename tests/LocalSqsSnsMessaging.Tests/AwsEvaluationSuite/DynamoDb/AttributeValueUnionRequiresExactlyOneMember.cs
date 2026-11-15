// Translated from behavioral/dynamodb/check_attribute_value_union_requires_exactly_one_member.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.attribute-value-union-requires-exactly-one-member</summary>
[Category("AwsEvaluationSuite")]
public sealed class AttributeValueUnionRequiresExactlyOneMember
{
    private const string Check = "attribute-value-union-requires-exactly-one-member";
    private const string ErrValidation = "ValidationException";

    /// <summary>
    /// An AttributeValue with zero members, with two members, or with an empty nested member fails with
    /// ValidationException and AWS's exact message, and none of those writes lands, while a one-member
    /// write succeeds.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.attribute-value-union-requires-exactly-one-member")]
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
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        // One member is the legal form, so the rejections below are about arity.
        await ddb.PutItemAsync(t, Item(("pk", S("control")), ("a", S("one-member"))));
        Ensure(
            Py.Equal((await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("control"))), ConsistentRead = true })).Item["a"], S("one-member")),
            "positive control failed: a single-member AttributeValue must round-trip");

        var (zero, zeroMessage) = await Code(ctx, t, """{"pk": {"S": "zero"}, "bad": {}}""");
        Ensure(zero == ErrValidation, $"an AttributeValue with no member set must be {ErrValidation}, got {Py.Repr(zero)}");
        Ensure(
            zeroMessage == "Supplied AttributeValue is empty, must contain exactly one of the supported datatypes",
            $"expected AWS's exact ValidationException message, got {Py.Repr(zeroMessage)}");

        var (two, twoMessage) = await Code(ctx, t, """{"pk": {"S": "two"}, "bad": {"S": "x", "N": "1"}}""");
        Ensure(
            two == ErrValidation,
            $"an AttributeValue with two members set must be {ErrValidation} rather than silently " +
            $"resolving to one of them, got {Py.Repr(two)}");
        Ensure(
            twoMessage == "Supplied AttributeValue has more than one datatypes set, must contain exactly one of the supported datatypes",
            $"expected AWS's exact ValidationException message, got {Py.Repr(twoMessage)}");

        foreach (var key in new[] { "zero", "two" })
        {
            var got = await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S(key))), ConsistentRead = true });
            Ensure(got.Item is null, $"rejected write {Py.Repr(key)} must leave no item behind, found {Py.Repr(got.Item)}");
        }

        // The nested case takes the same path: arity is a property of every
        // AttributeValue, not only of a top-level attribute.
        var (nested, nestedMessage) = await Code(ctx, t, """{"pk": {"S": "nested"}, "m": {"M": {"inner": {}}}}""");
        Ensure(nested == ErrValidation, $"an empty AttributeValue nested inside a map must be {ErrValidation}, got {Py.Repr(nested)}");
        Ensure(
            nestedMessage == "Supplied AttributeValue is empty, must contain exactly one of the supported datatypes",
            $"expected AWS's exact ValidationException message, got {Py.Repr(nestedMessage)}");
        Ensure(
            (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("nested"))), ConsistentRead = true })).Item is null,
            "rejected nested write must leave no item behind");
    }

    /// <summary>
    /// The botocore error code and message a PutItem of <paramref name="itemJson"/> raises (sent raw:
    /// the .NET SDK's AttributeValue can't be sent empty or with two members).
    ///
    /// An unexpected SUCCESS raises rather than returning None: a responder that ACCEPTS a request real
    /// AWS refuses is the strongest signal this check can produce.
    /// </summary>
    private static async Task<(string? Code, string? Message)> Code(FidelityContext ctx, string table, string itemJson)
    {
        var resp = await ctx.DynamoDbRaw("PutItem", $$"""{"TableName": "{{table}}", "Item": {{itemJson}}}""");
        if ((int)resp.StatusCode < 300)
        {
            throw Fail($"real AWS rejects this call, but it SUCCEEDED and returned {resp.Body}");
        }

        return (resp.ErrorCode, resp.ErrorMessage);
    }
}
