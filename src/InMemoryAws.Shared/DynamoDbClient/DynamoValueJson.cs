using System.Buffers;
using System.Text.Json;
#if NETSTANDARD2_0
using InMemoryAws.Http.Internal;
#endif

namespace InMemoryAws.DynamoDb;

/// <summary>
/// <c>AttributeValue</c> on the wire: a JSON object with exactly one data type member,
/// <c>{"S": "text"}</c>, <c>{"N": "1.5"}</c>, <c>{"M": {"a": {"BOOL": true}}}</c> and so on.
/// </summary>
internal static class DynamoValueJson
{
    public const string EmptyValueMessage =
        "Supplied AttributeValue is empty, must contain exactly one of the supported datatypes";

    public const string AmbiguousValueMessage =
        "Supplied AttributeValue has more than one datatypes set, must contain exactly one of the supported datatypes";

    /// <summary>
    /// Reads a value as the caller sent it. A value naming no data type, or more than one, is
    /// refused here, as DynamoDB's deserializer refuses it; every other rule (a number's
    /// precision, an empty set) is DynamoDB's to check where it reads the value.
    /// </summary>
    public static DynamoValue Read(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InternalSerializationException("Start of structure or map found where not expected");
        }

        DynamoValue? value = null;
        var types = 0;
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            DynamoValue? read = property.Name switch
            {
                "S" => DynamoValue.FromString(ReadString(property.Value)),
                "N" => DynamoValue.FromNumberText(ReadString(property.Value)),
                "B" => DynamoValue.FromBinary(ReadBlob(property.Value)),
                "SS" => DynamoValue.FromStringSet(ReadList(property.Value, ReadString)),
                "NS" => DynamoValue.FromNumberSet(ReadList(property.Value, ReadString)),
                "BS" => DynamoValue.FromBinarySet(ReadList(property.Value, ReadBlob)),
                "M" => DynamoValue.FromMap(ReadMap(property.Value)),
                "L" => DynamoValue.FromList(ReadList(property.Value, Read)),
                "NULL" => DynamoValue.FromNull(ReadBoolean(property.Value)),
                "BOOL" => DynamoValue.FromBoolean(ReadBoolean(property.Value)),
                _ => null,
            };

            if (read is not null)
            {
                value = read;
                types++;
            }
        }

        return types switch
        {
            0 => throw new InternalValidationException(EmptyValueMessage),
            1 => value!,
            _ => throw new InternalValidationException(AmbiguousValueMessage),
        };
    }

    /// <summary>Reads an item (or key): a map of attribute names to values.</summary>
    public static Dictionary<string, DynamoValue> ReadItem(JsonElement element) => ReadMap(element);

    private static Dictionary<string, DynamoValue> ReadMap(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InternalSerializationException("Start of structure or map found where not expected");
        }

        var map = new Dictionary<string, DynamoValue>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            map[property.Name] = Read(property.Value);
        }

        return map;
    }

    private static List<T> ReadList<T>(JsonElement element, Func<JsonElement, T> read)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new InternalSerializationException("Unexpected value type in payload");
        }

        var list = new List<T>(element.GetArrayLength());
        foreach (var item in element.EnumerateArray())
        {
            list.Add(read(item));
        }

        return list;
    }

    private static string ReadString(JsonElement element) =>
        element.ValueKind == JsonValueKind.String
            ? element.GetString()!
            : throw new InternalSerializationException("Unexpected value type in payload");

    private static bool ReadBoolean(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new InternalSerializationException("Unexpected value type in payload"),
    };

    private static byte[] ReadBlob(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String && element.TryGetBytesFromBase64(out var bytes))
        {
            return bytes;
        }

        throw new InternalSerializationException("Base64 encoded value is invalid");
    }

    public static void Write(Utf8JsonWriter writer, DynamoValue value)
    {
        writer.WriteStartObject();
        switch (value.Type)
        {
            case DynamoType.S:
                writer.WriteString("S", value.S);
                break;
            case DynamoType.N:
                writer.WriteString("N", value.N);
                break;
            case DynamoType.B:
                writer.WriteBase64String("B", value.B);
                break;
            case DynamoType.SS:
                writer.WriteStartArray("SS");
                foreach (var member in value.SS)
                {
                    writer.WriteStringValue(member);
                }

                writer.WriteEndArray();
                break;
            case DynamoType.NS:
                writer.WriteStartArray("NS");
                foreach (var member in value.NS)
                {
                    writer.WriteStringValue(member);
                }

                writer.WriteEndArray();
                break;
            case DynamoType.BS:
                writer.WriteStartArray("BS");
                foreach (var member in value.BS)
                {
                    writer.WriteBase64StringValue(member);
                }

                writer.WriteEndArray();
                break;
            case DynamoType.M:
                writer.WritePropertyName("M");
                WriteItem(writer, value.M);
                break;
            case DynamoType.L:
                writer.WriteStartArray("L");
                foreach (var element in value.L)
                {
                    Write(writer, element);
                }

                writer.WriteEndArray();
                break;
            case DynamoType.NULL:
                writer.WriteBoolean("NULL", value.NullStated);
                break;
            case DynamoType.BOOL:
                writer.WriteBoolean("BOOL", value.Bool);
                break;
        }

        writer.WriteEndObject();
    }

    public static void WriteItem(Utf8JsonWriter writer, IReadOnlyDictionary<string, DynamoValue> item)
    {
        writer.WriteStartObject();
        foreach (var entry in item)
        {
            writer.WritePropertyName(entry.Key);
            Write(writer, entry.Value);
        }

        writer.WriteEndObject();
    }

    /// <summary>The value as wire JSON, e.g. <c>{"S":"a"}</c>.</summary>
    public static string ToJson(DynamoValue value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(writer, value);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan.ToArray());
    }

    /// <summary>
    /// The value as DynamoDB's Java service renders an <c>AttributeValue</c> in a validation
    /// message: every member, the unset ones as <c>null</c>.
    /// </summary>
    public static string Render(DynamoValue value)
    {
        string Member(DynamoType type, Func<string> render) => value.Type == type ? render() : "null";

        return "AttributeValue("
            + "s=" + Member(DynamoType.S, () => value.S)
            + ", n=" + Member(DynamoType.N, () => value.N)
            + ", b=" + Member(DynamoType.B, () => JavaRendering.Blob(value.B))
            + ", iNT=null, dECIMAL=null, dOUBLE=null"
            + ", sS=" + Member(DynamoType.SS, () => JavaRendering.List(value.SS, s => s))
            + ", nS=" + Member(DynamoType.NS, () => JavaRendering.List(value.NS, s => s))
            + ", bS=" + Member(DynamoType.BS, () => JavaRendering.List(value.BS, JavaRendering.Blob))
            + ", iNTSET=null, dECIMALSET=null, dOUBLESET=null"
            + ", m=" + Member(DynamoType.M, () => JavaRendering.Map(value.M, Render))
            + ", l=" + Member(DynamoType.L, () => JavaRendering.List(value.L, Render))
            + ", nULL=" + Member(DynamoType.NULL, () => JavaRendering.Boolean(value.NullStated))
            + ", bOOL=" + Member(DynamoType.BOOL, () => JavaRendering.Boolean(value.Bool))
            + ")";
    }
}
