// Translated from behavioral/dynamodb/check_get_item_projection_expression_alias_substitution_exceeds_4kb_accepted.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.get-item-projection-expression-alias-substitution-exceeds-4kb-accepted</summary>
[Category("AwsEvaluationSuite")]
public sealed class GetItemProjectionExpressionAliasSubstitutionExceeds4kbAccepted
{
    private const int Count = 20;
    private const int NameLen = 250;

    /// <summary>
    /// GetItem accepts a ProjectionExpression whose raw string stays under the 4096-byte
    /// expression-size limit, even though its ExpressionAttributeNames placeholders substitute
    /// to attribute names that together total well over 4096 bytes.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ServiceQuotas.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.get-item-projection-expression-alias-substitution-exceeds-4kb-accepted")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-expr-alias-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await ddb.WaitForTableExists(table, delaySeconds: 1, maxAttempts: 60);

        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var item = Item(("pk", S("p1")));
        for (var i = 0; i < Count; i++)
        {
            var prefix = string.Create(CultureInfo.InvariantCulture, $"n{i}_");
            var longName = prefix + new string('a', NameLen - prefix.Length);
            Ensure(longName.Length == NameLen, $"built name of {longName.Length} bytes, wanted {NameLen}");
            names[string.Create(CultureInfo.InvariantCulture, $"#a{i}")] = longName;
            item[longName] = S(string.Create(CultureInfo.InvariantCulture, $"v{i}"));
        }

        item["excluded"] = S("not-projected");
        await ddb.PutItemAsync(table, item);

        var expr = string.Join(",", Enumerable.Range(0, Count).Select(i => string.Create(CultureInfo.InvariantCulture, $"#a{i}")));
        var substitutedTotal = names.Values.Sum(v => v.Length);
        Ensure(expr.Length < 4096, $"raw ProjectionExpression is {expr.Length} bytes, expected well under 4096");
        Ensure(substitutedTotal > 4096, $"substituted attribute names total {substitutedTotal} bytes, expected well over 4096");

        var resp = await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("p1"))),
            ProjectionExpression = expr,
            ExpressionAttributeNames = names,
            ConsistentRead = true,
        });
        var got = resp.Item;
        var keys = got.Keys.Order(StringComparer.Ordinal).ToList();
        Ensure(!got.ContainsKey("excluded"), $"expected 'excluded' excluded by the projection, got keys={Py.Repr(keys)}");
        Ensure(!got.ContainsKey("pk"), $"expected key attribute 'pk' excluded when not named in ProjectionExpression, got keys={Py.Repr(keys)}");
        Ensure(got.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(names.Values), $"expected exactly the {Count} aliased attributes projected, got keys={Py.Repr(keys)}");
        for (var i = 0; i < Count; i++)
        {
            var attr = names[string.Create(CultureInfo.InvariantCulture, $"#a{i}")];
            Ensure(
                Py.Equal(got[attr], S(string.Create(CultureInfo.InvariantCulture, $"v{i}"))),
                $"expected {attr}={{'S': 'v{i}'}}, got {Py.Repr(got.GetValueOrDefault(attr))}");
        }
    }

    /// <summary>A cleanup running <c>undo()</c> where a failed delete raises.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };
}
