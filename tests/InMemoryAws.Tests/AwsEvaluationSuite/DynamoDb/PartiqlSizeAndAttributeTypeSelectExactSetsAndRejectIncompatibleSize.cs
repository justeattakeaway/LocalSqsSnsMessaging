// Translated from behavioral/dynamodb/check_partiql_size_and_attribute_type_select_exact_sets_and_reject_incompatible_size.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-size-and-attribute-type-select-exact-sets-and-reject-incompatible-size</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlSizeAndAttributeTypeSelectExactSetsAndRejectIncompatibleSize
{
    private const string CheckId = "partiql-size-and-attribute-type-select-exact-sets-and-reject-incompatible-size";

    /// <summary>pk -> the AttributeValue stored under "val", one per attribute_type code under test.</summary>
    private static readonly (string Pk, string Code, AttributeValue Value)[] TypeItems =
    [
        ("type-s", "S", S("hello")),
        ("type-n", "N", N("42")),
        ("type-l", "L", L(S("a"), S("b"), S("c"))),
        ("type-m", "M", M(("x", S("1")), ("y", S("2")))),
        ("type-ss", "SS", SS("a", "b", "c", "d")),
        ("type-null", "NULL", Null()),
        ("type-bool", "BOOL", Bool(true)),
    ];

    /// <summary>(attribute name, threshold, pk of the item below the threshold, pk of the item above it).</summary>
    private static readonly (string Attr, int Threshold, string SmallPk, string BigPk)[] SizeCases =
    [
        ("sval", 3, "size-s-small", "size-s-big"),
        ("bval", 3, "size-b-small", "size-b-big"),
        ("lval", 2, "size-l-small", "size-l-big"),
        ("mval", 2, "size-m-small", "size-m-big"),
        ("ssval", 2, "size-ss-small", "size-ss-big"),
    ];

    /// <summary>pk -> {attribute name: AttributeValue}, one PutItem per size case's small/big pair.</summary>
    private static readonly (string Pk, string Attr, AttributeValue Value)[] SizeItems =
    [
        ("size-s-small", "sval", S("ab")),
        ("size-s-big", "sval", S("abcdef")),
        ("size-b-small", "bval", B("AB")),
        ("size-b-big", "bval", B("ABCDEFGH")),
        ("size-l-small", "lval", L(N("1"))),
        ("size-l-big", "lval", L(N("1"), N("2"), N("3"))),
        ("size-m-small", "mval", M(("a", N("1")))),
        ("size-m-big", "mval", M(("a", N("1")), ("b", N("2")), ("c", N("3")))),
        ("size-ss-small", "ssval", SS("a")),
        ("size-ss-big", "ssval", SS("a", "b", "c")),
    ];

    /// <summary>An on-demand table keyed by pk (S) HASH only.</summary>
    private static async Task<string> MakeTable(FidelityContext ctx)
    {
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName("sim2real-behav-ddbsz-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 90);
        return name;
    }

    private static async Task Seed(FidelityContext ctx, string name)
    {
        var ddb = ctx.Dynamo;
        foreach (var (pk, _, val) in TypeItems)
        {
            await ddb.PutItemAsync(name, Item(("pk", S(pk)), ("val", val)));
        }

        foreach (var (pk, attr, val) in SizeItems)
        {
            await ddb.PutItemAsync(name, Item(("pk", S(pk)), (attr, val)));
        }
    }

    private static async Task<SortedSet<string>> SelectPks(IAmazonDynamoDB ddb, string name, string where)
    {
        var items = new List<Dictionary<string, AttributeValue>>();
        string? token = null;
        while (true)
        {
            var request = new ExecuteStatementRequest { Statement = $"SELECT pk FROM \"{name}\" WHERE {where}", ConsistentRead = true };
            if (!string.IsNullOrEmpty(token))
            {
                request.NextToken = token;
            }

            var page = await ddb.ExecuteStatementAsync(request);
            items.AddRange(page.Items);
            token = page.NextToken;
            if (string.IsNullOrEmpty(token))
            {
                return new SortedSet<string>(items.Select(i => i["pk"].S), StringComparer.Ordinal);
            }
        }
    }

    /// <summary>
    /// PartiQL attribute_type(val, code) selects exactly the item carrying that type; size(attr)
    /// selects exactly the items whose S/B/L/M/set attribute clears the threshold; an attribute_type
    /// code outside the documented set is refused with DynamoDB's exact validation error.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-size-and-attribute-type-select-exact-sets-and-reject-incompatible-size")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var name = await MakeTable(ctx);
        await Seed(ctx, name);

        // attribute_type: exactly the item whose stored type matches the requested code.
        foreach (var (pk, code, _) in TypeItems)
        {
            var got = await SelectPks(ddb, name, $"attribute_type(val, '{code}')");
            Ensure(got.SetEquals([pk]), $"attribute_type(val, '{code}') returned {Py.Repr(got.ToList())}, expected [{Py.Repr(pk)}]");
        }

        // size(): exactly the "-big" item clears the threshold; the "-small" one and every item that
        // lacks the attribute entirely do not.
        foreach (var (attr, threshold, smallPk, bigPk) in SizeCases)
        {
            var got = await SelectPks(ddb, name, $"size({attr}) > {threshold}");
            Ensure(got.SetEquals([bigPk]), $"size({attr}) > {threshold} returned {Py.Repr(got.ToList())}, expected [{Py.Repr(bigPk)}]");
            var gotSmall = await SelectPks(ddb, name, $"size({attr}) <= {threshold}");
            Ensure(
                gotSmall.Contains(smallPk) && !gotSmall.Contains(bigPk),
                $"size({attr}) <= {threshold} returned {Py.Repr(gotSmall.ToList())}; expected to contain {Py.Repr(smallPk)} and exclude {Py.Repr(bigPk)}");
        }

        // Positive control: attribute_type rejects a code outside the documented set.
        var (result, error) = await FidelityContext.Capture(() => ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"SELECT pk FROM \"{name}\" WHERE attribute_type(val, 'NOTATYPE')",
            ConsistentRead = true,
        }));
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
        }

        Ensure(error.Code == "ValidationException", $"attribute_type(val, 'NOTATYPE') raised {Py.Repr(error.Code)}");
        const string expectedMsg = "Invalid attribute type name found; type: NOTATYPE, valid types: {N,BS,L,B,NULL,M,S,SS,NS,BOOL}";
        Ensure(error.Message == expectedMsg, $"unexpected message: {Py.Repr(error.Message)}");
    }
}
