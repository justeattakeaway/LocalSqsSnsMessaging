// Translated from behavioral/dynamodb/check_x_amz_crc32_matches_body_on_success_and_error.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.x-amz-crc32-matches-body-on-success-and-error</summary>
/// <remarks>
/// The Python check reads each raw response through a botocore <c>before-parse</c> hook; the .NET
/// SDK has no such hook, so this port sends each request as boto3 would with
/// <see cref="DynamoDbChecks.DynamoDbRaw"/> and reads the status, headers and body from that.
/// </remarks>
[Category("AwsEvaluationSuite")]
public sealed class XAmzCrc32MatchesBodyOnSuccessAndError
{
    /// <summary>
    /// Every DynamoDB response, 200 or 400, carries an x-amz-crc32 header equal to the CRC32 of the raw body bytes,
    /// including a body with multi-byte UTF-8; SDKs that validate the header reject any other value.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.x-amz-crc32-matches-body-on-success-and-error")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var seen = new List<(string Op, int Status, IReadOnlyDictionary<string, string> Headers, byte[] Body)>();

        // ddb.meta.events.register("before-parse.dynamodb", capture): every request below goes
        // through Call, which records the raw response as the hook would.
        async Task<RawDynamoDbResponse> Call(string op, object body)
        {
            var response = await ctx.DynamoDbRaw(op, JsonSerializer.Serialize(body));
            seen.Add((op, (int)response.StatusCode, response.Headers, Encoding.UTF8.GetBytes(response.Body)));
            return response;
        }

        byte[] Last(string op, int status)
        {
            Ensure(seen.Count > 0, $"{op}: no response was captured");
            var (gotOp, gotStatus, headers, body) = seen[^1];
            Ensure((gotOp, gotStatus) == (op, status), $"captured {gotOp} {gotStatus}, expected {op} {status}");
            var lowered = headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value, StringComparer.Ordinal);
            var value = lowered.GetValueOrDefault("x-amz-crc32");
            Ensure(value is not null, $"{op} {status}: no x-amz-crc32 header among {Py.Repr(lowered.Keys.Order(StringComparer.Ordinal).ToList())}");
            Ensure(value.Length > 0 && value.All(char.IsAsciiDigit), $"{op} {status}: x-amz-crc32 {Py.Repr(value)} is not an unsigned decimal");
            var crc = Crc32(body);
            Ensure(
                ulong.Parse(value, CultureInfo.InvariantCulture) == crc,
                $"{op} {status}: x-amz-crc32 {Py.Repr(value)} != crc32 of the {body.Length}-byte body ({crc})");
            return body;
        }

        var table = FidelityContext.UniqueName("sim2real-behav-crc-");
        await Call("CreateTable", new
        {
            TableName = table,
            AttributeDefinitions = new[] { new { AttributeName = "pk", AttributeType = "S" } },
            KeySchema = new[] { new { AttributeName = "pk", KeyType = "HASH" } },
            BillingMode = "PAY_PER_REQUEST",
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        Last("CreateTable", 200);

        var clock = FidelityTime.StartNew();
        while ((await Call("DescribeTable", new { TableName = table })).Json?.GetProperty("Table").GetProperty("TableStatus").GetString() != "ACTIVE")
        {
            Ensure(clock.Elapsed < TimeSpan.FromSeconds(300.0), "the table never reached ACTIVE");
            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }

        Last("DescribeTable", 200);

        const string note = "Grüße — 東京 \U0001f600";
        var item = Item(("pk", S("k1")), ("note", S(note)), ("n", N("42")));
        await Call("PutItem", new
        {
            TableName = table,
            Item = new Dictionary<string, object>
            {
                ["pk"] = new { S = "k1" },
                ["note"] = new { S = note },
                ["n"] = new { N = "42" },
            },
        });
        Last("PutItem", 200);
        var got = await Call("GetItem", new { TableName = table, Key = new { pk = new { S = "k1" } }, ConsistentRead = true });
        var body = Last("GetItem", 200);
        object? gotItem = got.Json is { } json && json.TryGetProperty("Item", out var i) ? i : null;
        Ensure(Py.Equal(gotItem, item), $"GetItem returned {Py.Repr(gotItem)}");
        Ensure(body.Any(b => b >= 0x80), "the GetItem body carried no multi-byte UTF-8 for the checksum to cover");

        await Call("ListTables", new { Limit = 1 });
        Last("ListTables", 200);

        var (code, msg) = Error(await Call("PutItem", new
        {
            TableName = table,
            Item = new { pk = new { S = "k1" } },
            ConditionExpression = "attribute_not_exists(pk)",
        }));
        Ensure(
            (code, msg) == ("ConditionalCheckFailedException", "The conditional request failed"),
            $"the conditional put gave {Py.Repr(code)}: {Py.Repr(msg)}");
        Last("PutItem", 400);

        var missing = FidelityContext.UniqueName("sim2real-behav-crc-missing-");
        (code, msg) = Error(await Call("DescribeTable", new { TableName = missing }));
        Ensure(
            (code, msg) == ("ResourceNotFoundException", $"Requested resource not found: Table: {missing} not found"),
            $"describing a missing table gave {Py.Repr(code)}: {Py.Repr(msg)}");
        Last("DescribeTable", 400);

        (code, msg) = Error(await Call("GetItem", new { TableName = table, Key = new { pk = new { N = "1" } } }));
        Ensure(
            (code, msg) == ("ValidationException", "The provided key element does not match the schema"),
            $"a key of the wrong type gave {Py.Repr(code)}: {Py.Repr(msg)}");
        Last("GetItem", 400);
    }

    /// <summary>The (code, message) a response refuses with. An unexpected success raises, naming it.</summary>
    private static (string? Code, string? Message) Error(RawDynamoDbResponse response)
    {
        if ((int)response.StatusCode < 300)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {response.Body}");
        }

        return (response.ErrorCode, response.ErrorMessage);
    }

    /// <summary><c>zlib.crc32(body) &amp; 0xFFFFFFFF</c>.</summary>
    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }

    /// <summary>A cleanup running <paramref name="undo"/> where a failed delete raises; only <paramref name="notFound"/> means gone.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };
}
