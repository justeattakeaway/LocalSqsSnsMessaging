// Translated from behavioral/dynamodb/check_partiql_attribute_exists_splits_null_typed_from_missing.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-attribute-exists-splits-null-typed-from-missing</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlAttributeExistsSplitsNullTypedFromMissing
{
    private const string CheckId = "partiql-attribute-exists-splits-null-typed-from-missing";

    /// <summary>pk, sk, whether the item carries attribute "a" and with what value (null = omit the attribute).</summary>
    private static readonly (string Pk, int Sk, Func<AttributeValue>? A)[] Seed =
    [
        ("p1", 1, () => S("hello")),
        ("p1", 2, null),
        ("p1", 3, Null),
        ("p2", 1, () => S("world")),
        ("p2", 2, null),
        ("p3", 1, Null),
    ];

    private static List<(string, int)> Keys(Func<(string Pk, int Sk, Func<AttributeValue>? A), bool> predicate) =>
        [.. Seed.Where(predicate).Select(s => (s.Pk, s.Sk)).OrderBy(k => k.Pk, StringComparer.Ordinal).ThenBy(k => k.Sk)];

    private static readonly List<(string, int)> WithKeyPinned = Keys(s => s.Pk == "p1" && s.A is not null);
    private static readonly List<(string, int)> WithoutKeyPinned = Keys(s => s.Pk == "p1" && s.A is null);
    private static readonly List<(string, int)> WithFull = Keys(s => s.A is not null);
    private static readonly List<(string, int)> WithoutFull = Keys(s => s.A is null);

    private static string TupleRepr(IEnumerable<(string, int)> values)
    {
        var list = values.ToList();
        return "(" + string.Join(", ", list.Select(v => Py.Repr(v))) + (list.Count == 1 ? ",)" : ")");
    }

    /// <summary>An on-demand table keyed by pk (S) HASH + sk (N) RANGE.</summary>
    private static async Task<string> MakeTable(FidelityContext ctx)
    {
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName("sim2real-behav-ddbae-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.N)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 90);
        return name;
    }

    /// <summary>Write every seed item with one PutItem each, so a NULL-typed `a` is stored, not omitted.</summary>
    private static async Task SeedTable(FidelityContext ctx, string name)
    {
        var ddb = ctx.Dynamo;
        foreach (var (pk, sk, a) in Seed)
        {
            var item = Item(("pk", S(pk)), ("sk", N(sk.ToString(CultureInfo.InvariantCulture))));
            if (a is not null)
            {
                item["a"] = a();
            }

            await ddb.PutItemAsync(name, item);
        }
    }

    /// <summary>Every page of a consistent ExecuteStatement, as a sorted set of (pk, sk).</summary>
    private static async Task<List<(string, int)>> Select(IAmazonDynamoDB ddb, string statement)
    {
        var items = new List<Dictionary<string, AttributeValue>>();
        string? token = null;
        while (true)
        {
            var page = await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = statement, ConsistentRead = true, NextToken = token });
            items.AddRange(page.Items);
            token = page.NextToken;
            if (string.IsNullOrEmpty(token))
            {
                return [.. items.Select(i => (i["pk"].S, int.Parse(i["sk"].N, CultureInfo.InvariantCulture))).OrderBy(k => k.Item1, StringComparer.Ordinal).ThenBy(k => k.Item2)];
            }
        }
    }

    /// <summary>
    /// PartiQL attribute_exists/attribute_not_exists treat a NULL-typed attribute as present,
    ///
    /// on both a partition-pinned SELECT and a full-table SELECT.
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ql-functions.exists.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-attribute-exists-splits-null-typed-from-missing")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var name = await MakeTable(ctx);
        await SeedTable(ctx, name);

        var pinnedExists = await Select(ddb, $"SELECT * FROM \"{name}\" WHERE pk = 'p1' AND attribute_exists(a)");
        Ensure(pinnedExists.SequenceEqual(WithKeyPinned), $"key-pinned attribute_exists(a) returned {TupleRepr(pinnedExists)}, expected {TupleRepr(WithKeyPinned)}");

        var pinnedNotExists = await Select(ddb, $"SELECT * FROM \"{name}\" WHERE pk = 'p1' AND attribute_not_exists(a)");
        Ensure(
            pinnedNotExists.SequenceEqual(WithoutKeyPinned),
            $"key-pinned attribute_not_exists(a) returned {TupleRepr(pinnedNotExists)}, expected {TupleRepr(WithoutKeyPinned)}");

        var fullExists = await Select(ddb, $"SELECT * FROM \"{name}\" WHERE attribute_exists(a)");
        Ensure(fullExists.SequenceEqual(WithFull), $"full-table attribute_exists(a) returned {TupleRepr(fullExists)}, expected {TupleRepr(WithFull)}");

        var fullNotExists = await Select(ddb, $"SELECT * FROM \"{name}\" WHERE attribute_not_exists(a)");
        Ensure(fullNotExists.SequenceEqual(WithoutFull), $"full-table attribute_not_exists(a) returned {TupleRepr(fullNotExists)}, expected {TupleRepr(WithoutFull)}");
    }
}
