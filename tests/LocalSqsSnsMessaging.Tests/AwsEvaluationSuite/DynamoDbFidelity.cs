using System.Collections;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite;

/// <summary>
/// boto3's attribute values, written the way the Python checks write them. Import with
/// <c>using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;</c>, so
/// <c>{'pk': {'S': 'a'}, 'n': {'N': '1'}}</c> becomes <c>Item(("pk", S("a")), ("n", N("1")))</c>.
/// </summary>
public static class Ddb
{
    public static AttributeValue S(string value) => new() { S = value };

    public static AttributeValue N(string value) => new() { N = value };

    public static AttributeValue B(byte[] value) => new() { B = new MemoryStream(value) };

    /// <summary>A binary of <paramref name="text"/>'s UTF-8 bytes: Python's <c>b'text'</c>.</summary>
    public static AttributeValue B(string text) => B(Encoding.UTF8.GetBytes(text));

    public static AttributeValue SS(params string[] members) => new() { SS = [.. members] };

    public static AttributeValue NS(params string[] members) => new() { NS = [.. members] };

    public static AttributeValue BS(params byte[][] members) => new() { BS = [.. members.Select(m => new MemoryStream(m))] };

    public static AttributeValue L(params AttributeValue[] elements) => new() { L = [.. elements] };

    public static AttributeValue M(params (string Name, AttributeValue Value)[] entries) => new() { M = Item(entries) };

    public static AttributeValue Bool(bool value) => new() { BOOL = value };

    /// <summary><c>{'NULL': True}</c>.</summary>
    public static AttributeValue Null() => new() { NULL = true };

    /// <summary>An item or key: <c>{'pk': {'S': 'a'}}</c>.</summary>
    public static Dictionary<string, AttributeValue> Item(params (string Name, AttributeValue Value)[] entries)
    {
        var item = new Dictionary<string, AttributeValue>(StringComparer.Ordinal);
        foreach (var (name, value) in entries)
        {
            item[name] = value;
        }

        return item;
    }

    /// <summary><c>ExpressionAttributeNames</c>: <c>{'#n': 'name'}</c>.</summary>
    public static Dictionary<string, string> Names(params (string Placeholder, string Name)[] entries)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (placeholder, name) in entries)
        {
            names[placeholder] = name;
        }

        return names;
    }

    /// <summary>A binary attribute's bytes (boto3 hands back <c>bytes</c>).</summary>
    public static byte[] Bytes(MemoryStream? stream) => stream?.ToArray() ?? [];
}

/// <summary>
/// Python's <c>repr()</c> and <c>==</c> over what boto3 hands a check: an attribute value is the
/// dict <c>{'S': 'a'}</c>, an item a dict of them, a set a list. Assertion messages use
/// <see cref="Repr"/> so they read as the Python check's <c>{x!r}</c> does.
/// </summary>
public static class Py
{
    /// <summary><c>repr(value)</c>.</summary>
    public static string Repr(object? value)
    {
        var builder = new StringBuilder();
        Write(builder, value, sortKeys: false);
        return builder.ToString();
    }

    /// <summary>
    /// Python's <c>a == b</c> over boto3-shaped values: dicts (items, maps, attribute values)
    /// compare without regard to order, lists (and sets, which boto3 returns as lists) in order.
    /// </summary>
    public static bool Equal(object? a, object? b) => string.Equals(Canonical(a), Canonical(b), StringComparison.Ordinal);

    /// <summary>The value as boto3 would hand it to the check: an attribute value as its one-key dict.</summary>
    public static Dictionary<string, object?> Boto(AttributeValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.S is not null)
        {
            return new() { ["S"] = value.S };
        }

        if (value.N is not null)
        {
            return new() { ["N"] = value.N };
        }

        if (value.B is not null)
        {
            return new() { ["B"] = value.B.ToArray() };
        }

        if (value.SS is not null)
        {
            return new() { ["SS"] = value.SS };
        }

        if (value.NS is not null)
        {
            return new() { ["NS"] = value.NS };
        }

        if (value.BS is not null)
        {
            return new() { ["BS"] = value.BS.Select(b => b.ToArray()).ToList() };
        }

        if (value.M is not null)
        {
            return new() { ["M"] = value.M };
        }

        if (value.L is not null)
        {
            return new() { ["L"] = value.L };
        }

        if (value.NULL is not null)
        {
            return new() { ["NULL"] = value.NULL };
        }

        if (value.BOOL is not null)
        {
            return new() { ["BOOL"] = value.BOOL };
        }

        return [];
    }

    /// <summary>A DynamoDB Streams attribute value as boto3 would hand it to the check.</summary>
    public static Dictionary<string, object?> Boto(Amazon.DynamoDBStreams.Model.AttributeValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Boto(new AttributeValue
        {
            S = value.S,
            N = value.N,
            B = value.B,
            SS = value.SS,
            NS = value.NS,
            BS = value.BS,
            M = value.M?.ToDictionary(e => e.Key, e => ToDynamoDb(e.Value), StringComparer.Ordinal),
            L = value.L?.Select(ToDynamoDb).ToList(),
            NULL = value.NULL,
            BOOL = value.BOOL,
        });

        static AttributeValue ToDynamoDb(Amazon.DynamoDBStreams.Model.AttributeValue v)
        {
            return new AttributeValue
            {
                S = v.S,
                N = v.N,
                B = v.B,
                SS = v.SS,
                NS = v.NS,
                BS = v.BS,
                M = v.M?.ToDictionary(e => e.Key, e => ToDynamoDb(e.Value), StringComparer.Ordinal),
                L = v.L?.Select(ToDynamoDb).ToList(),
                NULL = v.NULL,
                BOOL = v.BOOL,
            };
        }
    }

    private static string Canonical(object? value)
    {
        var builder = new StringBuilder();
        Write(builder, value, sortKeys: true);
        return builder.ToString();
    }

    private static void Write(StringBuilder builder, object? value, bool sortKeys)
    {
        switch (value)
        {
            case null:
                builder.Append("None");
                break;
            case bool b:
                builder.Append(b ? "True" : "False");
                break;
            case string s:
                WriteString(builder, s);
                break;
            case byte[] bytes:
                WriteBytes(builder, bytes);
                break;
            case MemoryStream stream:
                WriteBytes(builder, stream.ToArray());
                break;
            case AttributeValue attribute:
                Write(builder, Boto(attribute), sortKeys);
                break;
            case Amazon.DynamoDBStreams.Model.AttributeValue streamsAttribute:
                Write(builder, Boto(streamsAttribute), sortKeys);
                break;
            case double d:
                builder.Append(PythonFloat(d));
                break;
            case float f:
                builder.Append(PythonFloat(f));
                break;
            case decimal m:
                builder.Append("Decimal('").Append(m.ToString(CultureInfo.InvariantCulture)).Append("')");
                break;
            case IFormattable number when value is int or long or short or byte or uint or ulong:
                builder.Append(number.ToString(null, CultureInfo.InvariantCulture));
                break;
            case JsonElement json:
                WriteJson(builder, json, sortKeys);
                break;
            case IDictionary dictionary:
            {
                var entries = new List<(string Key, object? Value)>();
                foreach (DictionaryEntry entry in dictionary)
                {
                    entries.Add((Repr(entry.Key), entry.Value));
                }

                if (sortKeys)
                {
                    entries.Sort((x, y) => string.CompareOrdinal(x.Key, y.Key));
                }

                builder.Append('{');
                for (var i = 0; i < entries.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(", ");
                    }

                    builder.Append(entries[i].Key).Append(": ");
                    Write(builder, entries[i].Value, sortKeys);
                }

                builder.Append('}');
                break;
            }

            case ConstantClass constant:
                WriteString(builder, constant.Value);
                break;
            case IEnumerable sequence:
            {
                builder.Append('[');
                var first = true;
                foreach (var element in sequence)
                {
                    if (!first)
                    {
                        builder.Append(", ");
                    }

                    first = false;
                    Write(builder, element, sortKeys);
                }

                builder.Append(']');
                break;
            }

            case System.Runtime.CompilerServices.ITuple tuple:
            {
                builder.Append('(');
                for (var i = 0; i < tuple.Length; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(", ");
                    }

                    Write(builder, tuple[i], sortKeys);
                }

                builder.Append(tuple.Length == 1 ? ",)" : ")");
                break;
            }

            default:
                builder.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }

    private static void WriteJson(StringBuilder builder, JsonElement json, bool sortKeys)
    {
        switch (json.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var entries = json.EnumerateObject().Select(p => (Key: p.Name, p.Value)).ToList();
                if (sortKeys)
                {
                    entries.Sort((x, y) => string.CompareOrdinal(x.Key, y.Key));
                }

                builder.Append('{');
                for (var i = 0; i < entries.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(", ");
                    }

                    WriteString(builder, entries[i].Key);
                    builder.Append(": ");
                    WriteJson(builder, entries[i].Value, sortKeys);
                }

                builder.Append('}');
                break;
            }

            case JsonValueKind.Array:
                builder.Append('[');
                var first = true;
                foreach (var element in json.EnumerateArray())
                {
                    if (!first)
                    {
                        builder.Append(", ");
                    }

                    first = false;
                    WriteJson(builder, element, sortKeys);
                }

                builder.Append(']');
                break;
            case JsonValueKind.String:
                WriteString(builder, json.GetString()!);
                break;
            case JsonValueKind.True:
                builder.Append("True");
                break;
            case JsonValueKind.False:
                builder.Append("False");
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                builder.Append("None");
                break;
            default:
                builder.Append(json.GetRawText());
                break;
        }
    }

    private static string PythonFloat(double d)
    {
        if (double.IsNaN(d))
        {
            return "nan";
        }

        if (double.IsInfinity(d))
        {
            return d > 0 ? "inf" : "-inf";
        }

        var text = d.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.', StringComparison.Ordinal) || text.Contains('E', StringComparison.Ordinal) ? text : text + ".0";
    }

    /// <summary>Python's string repr: single quotes unless the text holds one and no double quote.</summary>
    private static void WriteString(StringBuilder builder, string text)
    {
        var quote = text.Contains('\'', StringComparison.Ordinal) && !text.Contains('"', StringComparison.Ordinal) ? '"' : '\'';
        builder.Append(quote);
        foreach (var rune in text.EnumerateRunes())
        {
            switch (rune.Value)
            {
                case '\\':
                    builder.Append(@"\\");
                    break;
                case '\n':
                    builder.Append(@"\n");
                    break;
                case '\r':
                    builder.Append(@"\r");
                    break;
                case '\t':
                    builder.Append(@"\t");
                    break;
                default:
                    if (rune.Value == quote)
                    {
                        builder.Append('\\').Append(quote);
                    }
                    else if (rune.Value < 0x20 || rune.Value == 0x7F)
                    {
                        builder.Append(CultureInfo.InvariantCulture, $"\\x{rune.Value:x2}");
                    }
                    else if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format
                        or UnicodeCategory.Surrogate or UnicodeCategory.OtherNotAssigned or UnicodeCategory.LineSeparator
                        or UnicodeCategory.ParagraphSeparator or UnicodeCategory.SpaceSeparator && rune.Value != ' ')
                    {
                        builder.Append(rune.Value switch
                        {
                            <= 0xFF => "\\x" + rune.Value.ToString("x2", CultureInfo.InvariantCulture),
                            <= 0xFFFF => "\\u" + rune.Value.ToString("x4", CultureInfo.InvariantCulture),
                            _ => "\\U" + rune.Value.ToString("x8", CultureInfo.InvariantCulture),
                        });
                    }
                    else
                    {
                        builder.Append(rune.ToString());
                    }

                    break;
            }
        }

        builder.Append(quote);
    }

    private static void WriteBytes(StringBuilder builder, byte[] bytes)
    {
        var quote = bytes.Contains((byte)'\'') && !bytes.Contains((byte)'"') ? '"' : '\'';
        builder.Append('b').Append(quote);
        foreach (var b in bytes)
        {
            switch (b)
            {
                case (byte)'\\':
                    builder.Append(@"\\");
                    break;
                case (byte)'\n':
                    builder.Append(@"\n");
                    break;
                case (byte)'\r':
                    builder.Append(@"\r");
                    break;
                case (byte)'\t':
                    builder.Append(@"\t");
                    break;
                default:
                    if (b == quote)
                    {
                        builder.Append('\\').Append(quote);
                    }
                    else if (b is < 0x20 or >= 0x7F)
                    {
                        builder.Append(CultureInfo.InvariantCulture, $"\\x{b:x2}");
                    }
                    else
                    {
                        builder.Append((char)b);
                    }

                    break;
            }
        }

        builder.Append(quote);
    }
}

/// <summary>botocore's <c>WaiterError</c>: a waiter met a failure state or ran out of attempts.</summary>
public sealed class WaiterException : Exception
{
    public WaiterException() { }

    public WaiterException(string message) : base(message) { }

    public WaiterException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// boto3's DynamoDB waiters and the raw JSON requests some checks send, on the check's virtual clock.
/// </summary>
public static class DynamoDbChecks
{
    /// <summary>
    /// <c>ddb.get_waiter('table_exists').wait(TableName=..., WaiterConfig={"Delay": d, "MaxAttempts": n})</c>:
    /// DescribeTable until the table is ACTIVE, retrying while it is not found, waiting
    /// <paramref name="delaySeconds"/> between attempts. botocore's defaults are 20 s and 25 attempts.
    /// </summary>
    public static async Task WaitForTableExists(this IAmazonDynamoDB ddb, string tableName, double delaySeconds = 20, int maxAttempts = 25)
    {
        ArgumentNullException.ThrowIfNull(ddb);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var table = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = tableName })).Table;
                if (table?.TableStatus?.Value == "ACTIVE")
                {
                    return;
                }
            }
            catch (ResourceNotFoundException)
            {
            }
            catch (AmazonServiceException ex)
            {
                throw new WaiterException($"Waiter TableExists failed: An error occurred ({ex.ErrorCode}): {ex.Message}", ex);
            }

            if (attempt >= maxAttempts)
            {
                throw new WaiterException("Waiter TableExists failed: Max attempts exceeded");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(delaySeconds));
        }
    }

    /// <summary>
    /// <c>ddb.get_waiter('table_not_exists').wait(...)</c>: DescribeTable until it answers
    /// <c>ResourceNotFoundException</c>. botocore's defaults are 20 s and 25 attempts.
    /// </summary>
    public static async Task WaitForTableNotExists(this IAmazonDynamoDB ddb, string tableName, double delaySeconds = 20, int maxAttempts = 25)
    {
        ArgumentNullException.ThrowIfNull(ddb);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = tableName });
            }
            catch (ResourceNotFoundException)
            {
                return;
            }
            catch (AmazonServiceException ex)
            {
                throw new WaiterException($"Waiter TableNotExists failed: An error occurred ({ex.ErrorCode}): {ex.Message}", ex);
            }

            if (attempt >= maxAttempts)
            {
                throw new WaiterException("Waiter TableNotExists failed: Max attempts exceeded");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(delaySeconds));
        }
    }

    /// <summary>The cancellation reasons of a <c>TransactionCanceledException</c>: <c>exc.response['CancellationReasons']</c>.</summary>
    public static List<CancellationReason>? CancellationReasons(this AwsError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return (error.Exception as TransactionCanceledException)?.CancellationReasons;
    }

    /// <summary>
    /// POSTs a hand-written JSON body to DynamoDB (or DynamoDB Streams, when <paramref name="operation"/>
    /// names a full target such as <c>DynamoDBStreams_20120810.ListStreams</c>), for what the .NET SDK
    /// can't send: wrong JSON types, unknown members, a check that sets
    /// <c>Config(parameter_validation=False)</c> to send what boto3 would refuse, or a check that reads
    /// the raw response (headers, <c>__type</c>).
    /// </summary>
    public static async Task<RawDynamoDbResponse> DynamoDbRaw(
        this FidelityContext ctx,
        string operation,
        string jsonBody,
        IReadOnlyDictionary<string, string>? headers = null,
        string contentType = "application/x-amz-json-1.0")
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var target = operation.Contains('.', StringComparison.Ordinal) ? operation : "DynamoDB_20120810." + operation;
        var streams = target.StartsWith("DynamoDBStreams_", StringComparison.Ordinal);
        var http = ctx.RawHttpClient(streams ? LocalSqsSnsMessaging.Http.AwsServiceType.DynamoDbStreams : LocalSqsSnsMessaging.Http.AwsServiceType.DynamoDb);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/");
        request.Content = new StringContent(jsonBody, Encoding.UTF8);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        request.Headers.TryAddWithoutValidation("X-Amz-Target", target);
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            if (!request.Headers.TryAddWithoutValidation(name, value))
            {
                request.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }

        using var response = await http.SendAsync(request, ctx.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(ctx.CancellationToken);
        var responseHeaders = response.Headers.Concat(response.Content.Headers)
            .ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        return new RawDynamoDbResponse(response.StatusCode, body, responseHeaders);
    }
}

/// <summary>A raw DynamoDB response, with the error botocore would parse from it.</summary>
public sealed record RawDynamoDbResponse(HttpStatusCode StatusCode, string Body, IReadOnlyDictionary<string, string> Headers)
{
    /// <summary>The body as JSON, or <see langword="null"/> when it isn't.</summary>
    public JsonElement? Json
    {
        get
        {
            try
            {
                using var document = JsonDocument.Parse(Body);
                return document.RootElement.Clone();
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    /// <summary>The body's <c>__type</c>, whole (<c>com.amazon.coral.validate#ValidationException</c>).</summary>
    public string? Type => Json is { ValueKind: JsonValueKind.Object } json && json.TryGetProperty("__type", out var type) ? type.GetString() : null;

    /// <summary>The error code as botocore reads it: <c>__type</c> after its last <c>#</c>.</summary>
    public string? ErrorCode => Type is { } type ? type[(type.LastIndexOf('#') + 1)..] : null;

    /// <summary>The error message as botocore reads it: <c>message</c>, else <c>Message</c>.</summary>
    public string? ErrorMessage =>
        Json is { ValueKind: JsonValueKind.Object } json
            ? json.TryGetProperty("message", out var m) ? m.GetString()
            : json.TryGetProperty("Message", out var m2) ? m2.GetString() : null
            : null;
}
