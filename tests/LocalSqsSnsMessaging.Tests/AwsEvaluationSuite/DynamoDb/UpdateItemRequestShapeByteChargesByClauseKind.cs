// Translated from behavioral/dynamodb/check_update_item_request_shape_byte_charges_by_clause_kind.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-request-shape-byte-charges-by-clause-kind</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemRequestShapeByteChargesByClauseKind
{
    private const string CheckId = "update-item-request-shape-byte-charges-by-clause-kind";
    private const string ErrValidation = "ValidationException";
    private const string MsgOverLimit = "Item size to update has exceeded the maximum allowed size";
    private const string Key = "pk";
    private const int UpperBound = 410 * 1024;
    private static readonly (string Code, string Message) OverLimit = (ErrValidation, MsgOverLimit);

    // Each family: the attributes a fresh item needs before the update, and a builder from
    // a baseline value length n to (UpdateExpression, ExpressionAttributeValues). Every
    // family's non-baseline operand is chosen so an accepted update leaves its own stored
    // bytes unchanged (0 + 0, if_not_exists over an attribute that already exists, an
    // ADD/DELETE operand that is already a no-op): any boundary shift relative to a plain
    // SET comes only from the clause's own request shape, never from the finished item
    // growing or shrinking.
    private static readonly (string Label, Func<Dictionary<string, AttributeValue>> BaseItem, Func<int, (string Expr, Dictionary<string, AttributeValue> Values)> ExprFn)[] Families =
    [
        ("path_operand", () => [], n => ("SET v = :v", Item((":v", S(new string('x', n)))))),
        ("document_path", () => Item(("m", M(("x", S("a"))))),
            n => ("SET v = :v, m.x = :mval", Item((":v", S(new string('x', n))), (":mval", S("a"))))),
        ("arithmetic", () => Item(("n", N("0"))),
            n => ("SET v = :v, n = n + :one", Item((":v", S(new string('x', n))), (":one", N("0"))))),
        ("function", () => Item(("f", S("a"))),
            n => ("SET v = :v, f = if_not_exists(f, :fval)", Item((":v", S(new string('x', n))), (":fval", S("a"))))),
        ("add", () => Item(("c", N("0"))),
            n => ("SET v = :v ADD c :zero", Item((":v", S(new string('x', n))), (":zero", N("0"))))),
        ("remove_delete", () => Item(("s", SS("a"))),
            n => ("SET v = :v DELETE s :absent", Item((":v", S(new string('x', n))), (":absent", SS("q"))))),
    ];

    // Bytes of accepted-size shortfall relative to the plain-SET baseline, identical in
    // us-west-2 and us-east-1: the charge differs by clause kind.
    private static readonly (string Family, int Delta)[] ExpectedDeltaFromBaseline =
    [
        ("path_operand", 0),
        ("document_path", 26),
        ("arithmetic", 71),
        ("function", 76),
        ("add", 21),
        ("remove_delete", 2),
    ];

    /// <summary>
    /// UpdateItem's size limit charges each SET/ADD/DELETE clause's own request shape,
    /// not only the bytes the finished item occupies, and the per-clause-kind shortfall
    /// from a flat 400 KB boundary is identical in us-west-2 and us-east-1.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ServiceQuotas.html#limits-items
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-request-shape-byte-charges-by-clause-kind")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddbWest = ctx.Dynamo;
        // region-pinned: UpdateItem's per-clause size accounting is compared against us-east-1.
        var ddbEast = ctx.DynamoClient(region: "us-east-1");

        var boundariesByRegion = new Dictionary<string, Dictionary<string, (int Lo, int Hi)>>(StringComparer.Ordinal);
        foreach (var (regionLabel, ddb) in new[] { ("us-west-2", ddbWest), ("us-east-1", ddbEast) })
        {
            var table = FidelityContext.UniqueName("sim2real-behav-reqshape-");
            await CreateTable(ctx, ddb, table);
            using var pkSource = Enumerable.Range(1, 999_999).Select(i => i.ToString("D8", CultureInfo.InvariantCulture)).GetEnumerator();
            boundariesByRegion[regionLabel] = await Measure(ddb, table, pkSource);
        }

        foreach (var (fam, _, _) in Families)
        {
            var west = boundariesByRegion["us-west-2"][fam];
            var east = boundariesByRegion["us-east-1"][fam];
            Ensure(west == east, $"{fam}: us-west-2 boundary {Py.Repr(west)} differs from us-east-1 boundary {Py.Repr(east)}");
        }

        var baseLo = boundariesByRegion["us-west-2"]["path_operand"].Lo;
        foreach (var (fam, expectedDelta) in ExpectedDeltaFromBaseline)
        {
            var lo = boundariesByRegion["us-west-2"][fam].Lo;
            var delta = baseLo - lo;
            Ensure(
                delta == expectedDelta,
                $"{fam}: measured {delta} byte(s) of request-shape charge relative to a plain SET, " +
                $"expected {expectedDelta}");
        }

        // Positive control: the accepted boundary for the plain SET actually lands, and the
        // next byte up actually leaves no trace -- proven by a read, not only by the
        // absence of a ClientError.
        var controlTable = FidelityContext.UniqueName("sim2real-behav-reqshape-control-");
        await CreateTable(ctx, ddbWest, controlTable);
        var (controlLo, controlHi) = boundariesByRegion["us-west-2"]["path_operand"];

        await ddbWest.PutItemAsync(controlTable, Item((Key, S("accepted"))));
        await ddbWest.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = controlTable,
            Key = Item((Key, S("accepted"))),
            UpdateExpression = "SET v = :v",
            ExpressionAttributeValues = Item((":v", S(new string('x', controlLo)))),
        });
        var stored = (await ddbWest.GetItemAsync(new GetItemRequest
        {
            TableName = controlTable,
            Key = Item((Key, S("accepted"))),
            ConsistentRead = true,
        })).Item ?? [];
        Ensure(
            (stored.GetValueOrDefault("v")?.S ?? "").Length == controlLo,
            $"the accepted {controlLo}-byte SET did not read back at that length, got {Py.Repr(stored)}");

        await ddbWest.PutItemAsync(controlTable, Item((Key, S("refused"))));
        var (code, message) = await CodeAndMessage(() => ddbWest.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = controlTable,
            Key = Item((Key, S("refused"))),
            UpdateExpression = "SET v = :v",
            ExpressionAttributeValues = Item((":v", S(new string('x', controlHi)))),
        }));
        Ensure(
            (code, message) == OverLimit,
            $"refusal at {controlHi} bytes was {Py.Repr((code, message))}, expected {Py.Repr(OverLimit)}");
        var afterRefused = (await ddbWest.GetItemAsync(new GetItemRequest
        {
            TableName = controlTable,
            Key = Item((Key, S("refused"))),
            ConsistentRead = true,
        })).Item ?? [];
        Ensure(!afterRefused.ContainsKey("v"), $"a refused SET must leave no trace, item is {Py.Repr(afterRefused)}");
    }

    /// <summary>The (code, message) <paramref name="fn"/> raises; an unexpected success raises instead.</summary>
    private static async Task<(string? Code, string? Message)> CodeAndMessage<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error is null
            ? throw Fail($"expected a refusal, but the call succeeded: {Py.Repr(result)}")
            : (error.Code, error.Message);
    }

    /// <summary>Reset <paramref name="pk"/>'s item to <paramref name="baseItem"/> then attempt one UpdateItem; true if accepted.</summary>
    private static async Task<bool> Accepted(
        IAmazonDynamoDB ddb, string table, string pk, Dictionary<string, AttributeValue> baseItem, string expr, Dictionary<string, AttributeValue> values)
    {
        var item = Item((Key, S(pk)));
        foreach (var (name, value) in baseItem)
        {
            item[name] = value;
        }

        await ddb.PutItemAsync(table, item);
        var error = await FidelityContext.CaptureError(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item((Key, S(pk))),
            UpdateExpression = expr,
            ExpressionAttributeValues = values,
        }));
        if (error is null)
        {
            return true;
        }

        if ((error.Code, error.Message) == OverLimit)
        {
            return false;
        }

        throw error.Exception;
    }

    private static string Next(IEnumerator<string> pkSource)
    {
        pkSource.MoveNext();
        return pkSource.Current;
    }

    /// <summary>(largest accepted n, smallest refused n) for the clause exprFn(n) builds.</summary>
    private static async Task<(int Lo, int Hi)> Bisect(
        IAmazonDynamoDB ddb, string table, Func<Dictionary<string, AttributeValue>> baseItem,
        Func<int, (string Expr, Dictionary<string, AttributeValue> Values)> exprFn, IEnumerator<string> pkSource)
    {
        int lo = 1, hi = UpperBound;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            var (expr, values) = exprFn(mid);
            if (await Accepted(ddb, table, Next(pkSource), baseItem(), expr, values))
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        return (lo, hi);
    }

    /// <summary>Bisected (lo, hi) boundary per family, each re-verified once for stability.</summary>
    private static async Task<Dictionary<string, (int Lo, int Hi)>> Measure(IAmazonDynamoDB ddb, string table, IEnumerator<string> pkSource)
    {
        var boundaries = new Dictionary<string, (int Lo, int Hi)>(StringComparer.Ordinal);
        foreach (var (label, baseItem, exprFn) in Families)
        {
            var (lo, hi) = await Bisect(ddb, table, baseItem, exprFn, pkSource);
            Ensure(hi == lo + 1, $"{label}: bisection did not converge to adjacent bytes, got lo={lo} hi={hi}");
            boundaries[label] = (lo, hi);
        }

        foreach (var (label, baseItem, exprFn) in Families)
        {
            var (lo, hi) = boundaries[label];
            var (loExpr, loValues) = exprFn(lo);
            Ensure(
                await Accepted(ddb, table, Next(pkSource), baseItem(), loExpr, loValues),
                $"{label}: re-measurement refused the previously-accepted size {lo}");
            var (hiExpr, hiValues) = exprFn(hi);
            Ensure(
                !await Accepted(ddb, table, Next(pkSource), baseItem(), hiExpr, hiValues),
                $"{label}: re-measurement accepted the previously-refused size {hi}");
        }

        return boundaries;
    }

    private static async Task CreateTable(FidelityContext ctx, IAmazonDynamoDB ddb, string name)
    {
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement(Key, KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition(Key, ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 30);
    }
}
