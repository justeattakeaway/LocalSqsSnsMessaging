using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite;

/// <summary>One UpdateItem clause a bisection tries: its expression, values and (optionally) names.</summary>
public sealed record UpdateClause(
    string Expression,
    Dictionary<string, AttributeValue>? Values = null,
    Dictionary<string, string>? Names = null);

/// <summary>
/// The UpdateItem size-ceiling bisection the limits checks share (their <c>_accepted</c>,
/// <c>_bisect</c> and <c>pk_source</c>): reset an item to a base, try one UpdateItem, and find the
/// largest size the clause a builder makes for it is accepted at.
/// </summary>
public sealed class UpdateSizeBisection(IAmazonDynamoDB ddb, string keyName = "pk")
{
    public static readonly (string Code, string Message) OverLimit =
        ("ValidationException", "Item size to update has exceeded the maximum allowed size");

    public const int UpperBound = 410 * 1024;

    private int _pk;

    /// <summary><c>next(pk_source)</c>: <c>00000001</c>, <c>00000002</c>, ...</summary>
    public string NextPk() => (++_pk).ToString("D8", CultureInfo.InvariantCulture);

    /// <summary>Reset <paramref name="pk"/>'s item to <paramref name="baseItem"/> then attempt one UpdateItem; true if accepted.</summary>
    public async Task<bool> Accepted(string table, string pk, Dictionary<string, AttributeValue> baseItem, UpdateClause clause)
    {
        var item = Item((keyName, S(pk)));
        foreach (var (name, value) in baseItem)
        {
            item[name] = value;
        }

        await ddb.PutItemAsync(table, item);
        var error = await FidelityContext.CaptureError(() => ddb.UpdateItemAsync(Request(table, pk, clause)));
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

    /// <summary>The UpdateItem request for <paramref name="clause"/> against <paramref name="pk"/>.</summary>
    public UpdateItemRequest Request(string table, string pk, UpdateClause clause)
    {
        ArgumentNullException.ThrowIfNull(clause);
        var request = new UpdateItemRequest
        {
            TableName = table,
            Key = Item((keyName, S(pk))),
            UpdateExpression = clause.Expression,
        };
        if (clause.Values is not null)
        {
            request.ExpressionAttributeValues = clause.Values;
        }

        if (clause.Names is not null)
        {
            request.ExpressionAttributeNames = clause.Names;
        }

        return request;
    }

    /// <summary>
    /// (largest accepted n, smallest refused n) for the clause <paramref name="exprFn"/>(n) builds.
    /// With <paramref name="verify"/>, asserts they are adjacent and re-measures both once.
    /// </summary>
    public async Task<(int Lo, int Hi)> Bisect(
        string table, Dictionary<string, AttributeValue> baseItem, Func<int, UpdateClause> exprFn, bool verify = true)
    {
        ArgumentNullException.ThrowIfNull(exprFn);
        int lo = 1, hi = UpperBound;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            if (await Accepted(table, NextPk(), baseItem, exprFn(mid)))
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        if (!verify)
        {
            return (lo, hi);
        }

        Ensure(hi == lo + 1, $"bisection did not converge to adjacent bytes, got lo={lo} hi={hi}");
        Ensure(await Accepted(table, NextPk(), baseItem, exprFn(lo)), $"re-measurement refused the previously-accepted size {lo}");
        Ensure(!await Accepted(table, NextPk(), baseItem, exprFn(hi)), $"re-measurement accepted the previously-refused size {hi}");
        return (lo, hi);
    }

    /// <summary>The (code, message) <paramref name="fn"/> raises; an unexpected success raises instead.</summary>
    public static async Task<(string? Code, string? Message)> CodeAndMessage<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error is null
            ? throw Fail($"expected a refusal, but the call succeeded: {Py.Repr(result)}")
            : (error.Code, error.Message);
    }

    /// <summary>
    /// The checks' <c>_search</c>: the largest value in [<paramref name="lo"/>, <paramref name="hi"/>]
    /// for which <paramref name="fn"/> is true, given <c>fn(hi + 1)</c> is false.
    /// </summary>
    public static async Task<int> Search(Func<int, Task<bool>> fn, int lo, int hi)
    {
        ArgumentNullException.ThrowIfNull(fn);
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (await fn(mid))
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo;
    }
}
