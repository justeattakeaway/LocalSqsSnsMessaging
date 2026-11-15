// Translated from behavioral/dynamodb/check_update_item_expression_substitution_total_size_over_2mb_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-expression-substitution-total-size-over-2mb-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemExpressionSubstitutionTotalSizeOver2mbRejected
{
    private const string CheckId = "update-item-expression-substitution-total-size-over-2mb-rejected";
    private const string ErrValidation = "ValidationException";
    private const int Kb = 1024;
    private const int TwoMb = 2 * 1024 * 1024;
    private const int UnderHalf = 950 * Kb;
    private const int OverFiller = 490 * Kb;
    private const int OverCount = 6;

    /// <summary>
    /// An UpdateItem whose ExpressionAttributeValues total just under 2 MB, split across its
    /// ConditionExpression and UpdateExpression, is evaluated and applies; the same shape with the
    /// substitution total over 2 MB is refused with ValidationException and the item is unchanged.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-expression-substitution-total-size-over-2mb-rejected")]
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

        await ddb.PutItemAsync(t, Item(("pk", S("row")), ("marker", S("initial"))));

        // Split the substitution budget across ConditionExpression's :vc and UpdateExpression's
        // :vu (a DELETE from a set attribute that does not exist, so it writes nothing) so neither
        // expression alone needs to carry the whole total; both the OR and the DELETE are no-ops on
        // the outcome and exist only to make every placeholder referenced exactly once.
        var underValues = Item(
            (":m", S("written")),
            (":vc", S(new string('c', UnderHalf))),
            (":vu", SS(new string('u', UnderHalf))));
        var underTotal = underValues[":vc"].S.Length + underValues[":vu"].SS[0].Length + underValues[":m"].S.Length;
        Ensure(underTotal < TwoMb, $"the under-cap request totals {underTotal} bytes, not under 2 MB");
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = Item(("pk", S("row"))),
            UpdateExpression = "SET marker = :m DELETE tags :vu",
            ConditionExpression = "attribute_exists(pk) OR pk = :vc",
            ExpressionAttributeValues = underValues,
        });
        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("row"))), ConsistentRead = true })).Item ?? [];
        Ensure(
            got.GetValueOrDefault("marker")?.S == "written",
            $"an UpdateItem whose substitution values total {underTotal} bytes (< 2 MB) must be " +
            $"evaluated and applied, got marker={Py.Repr(got.GetValueOrDefault("marker"))}");
        Ensure(!got.ContainsKey("tags"), $"DELETE from a never-created set attribute must not create it, got {Py.Repr(got)}");

        var overValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal);
        for (var i = 0; i < OverCount; i++)
        {
            overValues[$":o{i}"] = S(new string((char)('a' + i), OverFiller));
        }

        overValues[":m2"] = S("written2");
        var pairs = string.Join(" OR ", Enumerable.Range(0, OverCount / 2).Select(j => $":o{2 * j} = :o{(2 * j) + 1}"));
        var overTotal = Enumerable.Range(0, OverCount).Sum(i => overValues[$":o{i}"].S.Length) + overValues[":m2"].S.Length;
        Ensure(overTotal > TwoMb, $"the over-cap request totals {overTotal} bytes, not over 2 MB");
        var (code, message) = await Code(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = Item(("pk", S("row"))),
            UpdateExpression = "SET marker = :m2",
            ConditionExpression = $"attribute_exists(pk) OR {pairs}",
            ExpressionAttributeValues = overValues,
        }));
        Ensure(
            code == ErrValidation,
            $"an UpdateItem whose substitution values total {overTotal} bytes (> 2 MB) must be " +
            $"{ErrValidation}, got {Py.Repr(code)}: {Py.Repr(message)}");
        Ensure(
            message == "1 validation error detected: ExpressionAttributeValues exceeds max size",
            $"expected AWS's exact ValidationException message, got {Py.Repr(message)}");
        var unchanged = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("row"))), ConsistentRead = true })).Item ?? [];
        Ensure(
            unchanged.GetValueOrDefault("marker")?.S == "written",
            $"the refused over-2MB UpdateItem must leave the item unchanged, got marker={Py.Repr(unchanged.GetValueOrDefault("marker"))}");
    }

    /// <summary>The error code and message <paramref name="fn"/> raises. An unexpected success raises.</summary>
    private static async Task<(string? Code, string? Message)> Code<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error is null
            ? throw Fail($"real AWS rejects this call, but it SUCCEEDED and returned {Py.Repr(result)}")
            : (error.Code, error.Message);
    }
}
