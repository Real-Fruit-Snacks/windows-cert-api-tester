using System.Globalization;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace ApiTester.Grpc;

// Why this file exists instead of Google.Protobuf.JsonFormatter: JsonFormatter needs an IMessage
// instance backed by a CLR type, and descriptors built at runtime via FileDescriptor.BuildFromByteStrings
// have ClrType == null and no Parser — there is no generated code for them. Google.Protobuf for C#,
// unlike the Java and Go runtimes, has no DynamicMessage that could stand in. So this project converts
// JSON<->Protobuf itself, descriptor-driven, on top of Google.Protobuf's own CodedInputStream /
// CodedOutputStream: the risky part (varints, zigzag, fixed widths, length delimiters) stays the
// library's code, and what we own here is only the field-mapping layer. Do not "fix" this back to
// JsonFormatter; it cannot work against runtime-built descriptors.
//
// Coverage: every scalar, message, repeated (packed and unpacked), map, enum, and oneof field kind
// proto3 JSON defines, following the canonical mapping JsonFormatter implements. Proven differentially
// in ProtoJsonTests against Google's own JsonParser/JsonFormatter over generated test types built from
// the same .proto — that comparison is what justifies owning this layer at all.

/// <summary>Renders a Protobuf payload as proto3-canonical JSON.</summary>
internal static class ProtoJsonReader
{
    private const int MaxDepth = 100;

    /// <summary>A Protobuf payload rendered as proto3-canonical JSON. A field absent from the wire is
    /// simply omitted from the output — proto3 default-omission, for free. Fields are emitted in
    /// ascending field-number order regardless of the order they arrived on the wire, so output is
    /// deterministic.</summary>
    internal static string ToJson(MessageDescriptor descriptor, byte[] payload, bool indented)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = indented }))
        {
            WriteObject(writer, descriptor, Decode(descriptor, payload, depth: 0));
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Parses <paramref name="payload"/> field-by-field into a field-number-keyed map. A
    /// value is the field's natural CLR representation: <see cref="double"/>, <see cref="float"/>,
    /// <see cref="int"/>, <see cref="long"/>, <see cref="uint"/>, <see cref="ulong"/>,
    /// <see cref="bool"/>, <see cref="string"/>, a raw <see cref="ByteString"/>, the raw enum number
    /// (<see cref="int"/>), a nested <c>Dictionary&lt;int, object&gt;</c> for a message field, a
    /// <c>List&lt;object&gt;</c> of the element representation for a repeated field, or a
    /// <c>Dictionary&lt;string, object&gt;</c> — keyed by the already-stringified map key — for a map
    /// field.</summary>
    private static Dictionary<int, object> Decode(MessageDescriptor descriptor, byte[] payload, int depth)
    {
        if (depth > MaxDepth)
            throw new GrpcJsonException($"'{descriptor.FullName}': message nesting exceeds the maximum supported depth ({MaxDepth}).");

        var fieldsByNumber = descriptor.Fields.InFieldNumberOrder().ToDictionary(f => f.FieldNumber);
        var values = new Dictionary<int, object>();

        using var input = new CodedInputStream(payload);
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            int fieldNumber = WireFormat.GetTagFieldNumber(tag);
            if (!fieldsByNumber.TryGetValue(fieldNumber, out var field))
            {
                // A server may legitimately send a field newer than the descriptor we fetched.
                input.SkipLastField();
                continue;
            }

            if (field.FieldType == FieldType.Group)
            {
                throw new GrpcJsonException(
                    $"Field '{field.JsonName}': the proto2 'group' wire type is not supported (proto3 cannot express it).");
            }

            if (field.IsMap)
            {
                byte[] entryBytes = input.ReadBytes().ToByteArray();
                var (key, entryValue) = DecodeMapEntry(field, entryBytes, depth);
                if (!values.TryGetValue(fieldNumber, out var existingMap))
                {
                    existingMap = new Dictionary<string, object>();
                    values[fieldNumber] = existingMap;
                }
                ((Dictionary<string, object>)existingMap)[key] = entryValue;
                continue;
            }

            if (field.IsRepeated)
            {
                if (!values.TryGetValue(fieldNumber, out var existingList))
                {
                    existingList = new List<object>();
                    values[fieldNumber] = existingList;
                }
                var list = (List<object>)existingList;

                // A packable field may legitimately arrive packed (one length-delimited run) or
                // unpacked (one tag per element); a sender is free to choose either.
                if (IsPackable(field.FieldType) && WireFormat.GetTagWireType(tag) == WireFormat.WireType.LengthDelimited)
                {
                    list.AddRange(ReadPackedRun(input, field));
                }
                else
                {
                    list.Add(ReadFieldValue(input, field, depth));
                }
                continue;
            }

            // A field belonging to a non-synthetic oneof clears any previously-read member of the
            // same group: the last one on the wire is the one that emits.
            if (field.RealContainingOneof is { } oneof)
            {
                foreach (var sibling in oneof.Fields) values.Remove(sibling.FieldNumber);
            }

            values[fieldNumber] = ReadFieldValue(input, field, depth);
        }

        return values;
    }

    /// <summary>Reads one value already known to belong to <paramref name="field"/> — a singular
    /// field, one element of an unpacked repeated field, or a map's key/value field.</summary>
    private static object ReadFieldValue(CodedInputStream input, FieldDescriptor field, int depth) => field.FieldType switch
    {
        FieldType.Double => input.ReadDouble(),
        FieldType.Float => input.ReadFloat(),
        FieldType.Int32 => input.ReadInt32(),
        FieldType.Int64 => input.ReadInt64(),
        FieldType.UInt32 => input.ReadUInt32(),
        FieldType.UInt64 => input.ReadUInt64(),
        FieldType.SInt32 => input.ReadSInt32(),
        FieldType.SInt64 => input.ReadSInt64(),
        FieldType.Fixed32 => input.ReadFixed32(),
        FieldType.Fixed64 => input.ReadFixed64(),
        FieldType.SFixed32 => input.ReadSFixed32(),
        FieldType.SFixed64 => input.ReadSFixed64(),
        FieldType.Bool => input.ReadBool(),
        FieldType.String => input.ReadString(),
        FieldType.Bytes => input.ReadBytes(),
        FieldType.Enum => input.ReadEnum(),
        FieldType.Message => Decode(field.MessageType, input.ReadBytes().ToByteArray(), depth + 1),
        FieldType.Group => throw new GrpcJsonException(
            $"Field '{field.JsonName}': the proto2 'group' wire type is not supported (proto3 cannot express it)."),
        _ => throw new GrpcJsonException($"Field '{field.JsonName}' has an unrecognized Protobuf field type ({field.FieldType}).")
    };

    /// <summary>Reads a packed run: a single length-delimited blob holding the concatenated raw values
    /// of a packable repeated field, with no per-element tags. Uses a fresh <see cref="CodedInputStream"/>
    /// over the extracted bytes and <see cref="CodedInputStream.IsAtEnd"/> to know when the run is
    /// exhausted, since <c>PushLimit</c>/<c>PopLimit</c> are not accessible here.</summary>
    private static List<object> ReadPackedRun(CodedInputStream outer, FieldDescriptor field)
    {
        byte[] blob = outer.ReadBytes().ToByteArray();
        var items = new List<object>();
        var inner = new CodedInputStream(blob);
        while (!inner.IsAtEnd)
        {
            // Packable types are all scalar (never Message), so depth cannot matter here.
            items.Add(ReadFieldValue(inner, field, depth: 0));
        }
        return items;
    }

    /// <summary>Decodes one map entry submessage (field 1 = key, field 2 = value) into the entry's
    /// JSON property name and the value's natural representation. A key or value never present on the
    /// wire (because it held its type's default) is reconstructed as that default — proto3's
    /// default-omission applies inside a map entry exactly as it does everywhere else, but a map entry
    /// must still produce a concrete key and value.</summary>
    private static (string Key, object Value) DecodeMapEntry(FieldDescriptor mapField, byte[] entryBytes, int depth)
    {
        var keyField = mapField.MessageType.FindFieldByNumber(1);
        var valueField = mapField.MessageType.FindFieldByNumber(2);
        object? keyValue = null;
        object? valueValue = null;

        using var input = new CodedInputStream(entryBytes);
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            int number = WireFormat.GetTagFieldNumber(tag);
            if (number == 1) keyValue = ReadFieldValue(input, keyField, depth);
            else if (number == 2) valueValue = ReadFieldValue(input, valueField, depth + 1);
            else input.SkipLastField();
        }

        keyValue ??= DefaultValueFor(keyField);
        valueValue ??= DefaultValueFor(valueField);

        return (StringifyKey(keyField, keyValue), valueValue);
    }

    private static object DefaultValueFor(FieldDescriptor field) => field.FieldType switch
    {
        FieldType.Double => 0d,
        FieldType.Float => 0f,
        FieldType.Int32 or FieldType.SInt32 or FieldType.SFixed32 => 0,
        FieldType.Int64 or FieldType.SInt64 or FieldType.SFixed64 => 0L,
        FieldType.UInt32 or FieldType.Fixed32 => 0u,
        FieldType.UInt64 or FieldType.Fixed64 => 0ul,
        FieldType.Bool => false,
        FieldType.String => "",
        FieldType.Bytes => ByteString.Empty,
        FieldType.Enum => 0,
        FieldType.Message => new Dictionary<int, object>(),
        _ => throw new GrpcJsonException($"Field '{field.JsonName}' has an unrecognized Protobuf field type ({field.FieldType}).")
    };

    private static string StringifyKey(FieldDescriptor keyField, object keyValue) => keyField.FieldType switch
    {
        FieldType.String => (string)keyValue,
        FieldType.Bool => (bool)keyValue ? "true" : "false",
        _ => Convert.ToString(keyValue, CultureInfo.InvariantCulture) ?? ""
    };

    private static void WriteObject(Utf8JsonWriter writer, MessageDescriptor descriptor, Dictionary<int, object> values)
    {
        writer.WriteStartObject();
        foreach (var field in descriptor.Fields.InFieldNumberOrder())
        {
            if (values.TryGetValue(field.FieldNumber, out var value)) WriteFieldValue(writer, field, value);
        }
        writer.WriteEndObject();
    }

    private static void WriteFieldValue(Utf8JsonWriter writer, FieldDescriptor field, object value)
    {
        if (field.IsMap)
        {
            var map = (Dictionary<string, object>)value;
            if (map.Count == 0) return; // omitted entirely when empty
            var valueField = field.MessageType.FindFieldByNumber(2);
            writer.WritePropertyName(field.JsonName);
            writer.WriteStartObject();
            foreach (var entry in map)
            {
                writer.WritePropertyName(entry.Key);
                WriteScalarOrMessage(writer, valueField, entry.Value);
            }
            writer.WriteEndObject();
            return;
        }

        if (field.IsRepeated)
        {
            var list = (List<object>)value;
            if (list.Count == 0) return; // omitted entirely when empty
            writer.WritePropertyName(field.JsonName);
            writer.WriteStartArray();
            foreach (var item in list) WriteScalarOrMessage(writer, field, item);
            writer.WriteEndArray();
            return;
        }

        writer.WritePropertyName(field.JsonName);
        WriteScalarOrMessage(writer, field, value);
    }

    private static void WriteScalarOrMessage(Utf8JsonWriter writer, FieldDescriptor field, object value)
    {
        switch (field.FieldType)
        {
            case FieldType.Double: WriteDoubleValue(writer, (double)value); break;
            case FieldType.Float: WriteFloatValue(writer, (float)value); break;
            case FieldType.Int32 or FieldType.SInt32 or FieldType.SFixed32:
                writer.WriteNumberValue((int)value);
                break;
            case FieldType.UInt32 or FieldType.Fixed32:
                writer.WriteNumberValue((uint)value);
                break;
            // A 64-bit integer is rendered as a JSON string: it does not survive a JavaScript number.
            case FieldType.Int64 or FieldType.SInt64 or FieldType.SFixed64:
                writer.WriteStringValue(((long)value).ToString(CultureInfo.InvariantCulture));
                break;
            case FieldType.UInt64 or FieldType.Fixed64:
                writer.WriteStringValue(((ulong)value).ToString(CultureInfo.InvariantCulture));
                break;
            case FieldType.Bool:
                writer.WriteBooleanValue((bool)value);
                break;
            case FieldType.String:
                writer.WriteStringValue((string)value);
                break;
            case FieldType.Bytes:
                writer.WriteStringValue(Convert.ToBase64String(((ByteString)value).ToByteArray()));
                break;
            case FieldType.Enum:
                int number = (int)value;
                var enumValue = field.EnumType.FindValueByNumber(number);
                // The wire may carry a number the descriptor does not name (a value added to the
                // enum after this client fetched its descriptor); render the bare number rather than
                // inventing a name or failing.
                if (enumValue is null) writer.WriteNumberValue(number);
                else writer.WriteStringValue(enumValue.Name);
                break;
            case FieldType.Message:
                WriteObject(writer, field.MessageType, (Dictionary<int, object>)value);
                break;
            default:
                throw new GrpcJsonException(
                    $"Field '{field.JsonName}' has an unrecognized Protobuf field type ({field.FieldType}) that this build cannot render.");
        }
    }

    private static void WriteDoubleValue(Utf8JsonWriter writer, double value)
    {
        if (double.IsNaN(value)) writer.WriteStringValue("NaN");
        else if (double.IsPositiveInfinity(value)) writer.WriteStringValue("Infinity");
        else if (double.IsNegativeInfinity(value)) writer.WriteStringValue("-Infinity");
        else writer.WriteNumberValue(value);
    }

    private static void WriteFloatValue(Utf8JsonWriter writer, float value)
    {
        if (float.IsNaN(value)) writer.WriteStringValue("NaN");
        else if (float.IsPositiveInfinity(value)) writer.WriteStringValue("Infinity");
        else if (float.IsNegativeInfinity(value)) writer.WriteStringValue("-Infinity");
        else writer.WriteNumberValue(value);
    }

    /// <summary>The packable Protobuf types: every numeric type plus bool and enum. string, bytes,
    /// message, and group are never packed regardless of repetition.</summary>
    private static bool IsPackable(FieldType type) => type switch
    {
        FieldType.String or FieldType.Bytes or FieldType.Message or FieldType.Group => false,
        _ => true
    };
}
