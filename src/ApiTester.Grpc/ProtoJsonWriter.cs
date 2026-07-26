using System.Globalization;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace ApiTester.Grpc;

// Why this file exists instead of Google.Protobuf.JsonParser: JsonParser needs an IMessage instance
// backed by a CLR type, and descriptors built at runtime via FileDescriptor.BuildFromByteStrings have
// ClrType == null and no Parser — there is no generated code for them. Google.Protobuf for C#, unlike
// the Java and Go runtimes, has no DynamicMessage that could stand in. So this project converts
// JSON<->Protobuf itself, descriptor-driven, on top of Google.Protobuf's own CodedInputStream /
// CodedOutputStream: the risky part (varints, zigzag, fixed widths, length delimiters) stays the
// library's code, and what we own here is only the field-mapping layer. Do not "fix" this back to
// JsonParser; it cannot work against runtime-built descriptors.
//
// Coverage: every scalar, message, repeated (packed and unpacked), map, enum, and oneof field kind
// proto3 JSON defines, following the canonical mapping JsonParser implements. Proven differentially in
// ProtoJsonTests against Google's own JsonParser/JsonFormatter over generated test types built from the
// same .proto — that comparison is what justifies owning this layer at all.

/// <summary>Encodes a JSON object to the Protobuf wire form of a descriptor, following the canonical
/// proto3 JSON mapping.</summary>
internal static class ProtoJsonWriter
{
    private const int MaxDepth = 100;

    // System.Text.Json's own JsonDocument caps object/array nesting at 64 by default — below our own
    // message-nesting cap — so a request that is deep but still within MaxDepth would otherwise fail
    // during parsing itself, with a raw, unhelpful System.Text.Json.JsonException, before Encode's own
    // depth check ever runs. Raised generously past MaxDepth so our own check — reported as a clear
    // GrpcJsonException naming the field — is what actually governs.
    private static readonly JsonDocumentOptions ParseOptions = new() { MaxDepth = MaxDepth * 4 };

    /// <summary>A JSON object encoded as the Protobuf wire form of <paramref name="descriptor"/>. An
    /// empty or whitespace-only <paramref name="json"/> is treated as <c>{}</c> (an empty message):
    /// plenty of methods take no arguments, and forcing callers to pass an explicit empty object would
    /// be noise.</summary>
    internal static byte[] ToProtobuf(MessageDescriptor descriptor, string json)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json, ParseOptions);
        return Encode(descriptor, document.RootElement, path: "", depth: 0);
    }

    private static byte[] Encode(MessageDescriptor descriptor, JsonElement element, string path, int depth)
    {
        if (depth > MaxDepth)
            throw new GrpcJsonException($"'{Label(path, descriptor)}': message nesting exceeds the maximum supported depth ({MaxDepth}).");
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new GrpcJsonException(
                $"'{Label(path, descriptor)}': expected a JSON object for message '{descriptor.FullName}', found {Describe(element.ValueKind)}.");
        }

        // Track JSON property names in source order so an unrecognized property can be named
        // deterministically, and matched properties are struck off as fields consume them.
        var propertyOrder = new List<string>();
        var remaining = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            propertyOrder.Add(property.Name);
            remaining.Add(property.Name);
        }

        using var buffer = new MemoryStream();
        using var output = new CodedOutputStream(buffer, leaveOpen: true);

        // A non-synthetic oneof only allows one member set at a time; track which member (if any)
        // this message has already seen so a second one can be reported by name.
        var setOneofFields = new Dictionary<OneofDescriptor, string>();

        foreach (var field in descriptor.Fields.InFieldNumberOrder())
        {
            if (!TryGetField(element, field, out var value, out var matchedName)) continue;
            remaining.Remove(matchedName);
            if (value.ValueKind == JsonValueKind.Null) continue; // absent, per proto3 JSON mapping

            string fieldPath = string.IsNullOrEmpty(path) ? field.JsonName : $"{path}.{field.JsonName}";

            if (field.RealContainingOneof is { } oneof)
            {
                if (setOneofFields.TryGetValue(oneof, out var earlier))
                {
                    throw new GrpcJsonException(
                        $"'{Label(path, descriptor)}': fields '{earlier}' and '{field.JsonName}' are both set, but only one member of oneof '{oneof.Name}' may be present.");
                }
                setOneofFields[oneof] = field.JsonName;
            }

            WriteField(output, field, value, fieldPath, depth);
        }

        output.Flush();

        if (remaining.Count > 0)
        {
            string offending = propertyOrder.First(remaining.Contains);
            throw new GrpcJsonException(
                $"Unrecognized field '{(string.IsNullOrEmpty(path) ? offending : $"{path}.{offending}")}' for message '{descriptor.FullName}'.");
        }

        return buffer.ToArray();
    }

    private static void WriteField(CodedOutputStream output, FieldDescriptor field, JsonElement value, string path, int depth)
    {
        if (field.IsMap) { WriteMapField(output, field, value, path, depth); return; }
        if (field.IsRepeated) { WriteRepeatedField(output, field, value, path, depth); return; }
        WriteSingularValue(output, field, value, path, depth);
    }

    /// <summary>Writes one non-repeated field's tag and value. Also used, directly, for each element
    /// of a non-packable repeated field (string/bytes/message — the tag simply repeats), and for a map
    /// entry's key and value fields (whose "field number" is 1 or 2 within the synthetic entry
    /// message).</summary>
    private static void WriteSingularValue(CodedOutputStream output, FieldDescriptor field, JsonElement value, string path, int depth)
    {
        switch (field.FieldType)
        {
            case FieldType.Double:
                output.WriteTag(field.FieldNumber, WireFormat.WireType.Fixed64);
                output.WriteDouble(ParseDouble(value, field, path));
                break;
            case FieldType.Float:
                output.WriteTag(field.FieldNumber, WireFormat.WireType.Fixed32);
                output.WriteFloat(ParseFloat(value, field, path));
                break;
            case FieldType.Int32:
                output.WriteTag(field.FieldNumber, WireFormat.WireType.Varint);
                output.WriteInt32(ParseInt32(value, field, path));
                break;
            case FieldType.Int64:
                output.WriteTag(field.FieldNumber, WireFormat.WireType.Varint);
                output.WriteInt64(ParseInt64(value, field, path));
                break;
            case FieldType.UInt32:
                output.WriteTag(field.FieldNumber, WireFormat.WireType.Varint);
                output.WriteUInt32(ParseUInt32(value, field, path));
                break;
            case FieldType.UInt64:
                output.WriteTag(field.FieldNumber, WireFormat.WireType.Varint);
                output.WriteUInt64(ParseUInt64(value, field, path));
                break;
            case FieldType.SInt32:
                output.WriteTag(field.FieldNumber, WireFormat.WireType.Varint);
                output.WriteSInt32(ParseInt32(value, field, path));
                break;
            case FieldType.SInt64:
                output.WriteTag(field.FieldNumber, WireFormat.WireType.Varint);
                output.WriteSInt64(ParseInt64(value, field, path));
                break;
            case FieldType.Fixed32:
                output.WriteTag(field.FieldNumber, WireFormat.WireType.Fixed32);
                output.WriteFixed32(ParseUInt32(value, field, path));
                break;
            case FieldType.Fixed64:
                output.WriteTag(field.FieldNumber, WireFormat.WireType.Fixed64);
                output.WriteFixed64(ParseUInt64(value, field, path));
                break;
            case FieldType.SFixed32:
                output.WriteTag(field.FieldNumber, WireFormat.WireType.Fixed32);
                output.WriteSFixed32(ParseInt32(value, field, path));
                break;
            case FieldType.SFixed64:
                output.WriteTag(field.FieldNumber, WireFormat.WireType.Fixed64);
                output.WriteSFixed64(ParseInt64(value, field, path));
                break;

            case FieldType.Bool:
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    throw new GrpcJsonException(
                        $"'{path}': expected a boolean for field '{field.JsonName}', found {Describe(value.ValueKind)}.");
                }
                output.WriteTag(field.FieldNumber, WireFormat.WireType.Varint);
                output.WriteBool(value.GetBoolean());
                break;

            case FieldType.String:
                if (value.ValueKind != JsonValueKind.String)
                {
                    throw new GrpcJsonException(
                        $"'{path}': expected a string for field '{field.JsonName}', found {Describe(value.ValueKind)}.");
                }
                output.WriteTag(field.FieldNumber, WireFormat.WireType.LengthDelimited);
                output.WriteString(value.GetString()!);
                break;

            case FieldType.Bytes:
                output.WriteTag(field.FieldNumber, WireFormat.WireType.LengthDelimited);
                output.WriteBytes(ParseBytes(value, field, path));
                break;

            case FieldType.Enum:
                output.WriteTag(field.FieldNumber, WireFormat.WireType.Varint);
                output.WriteEnum(ResolveEnumValue(field, value, path));
                break;

            case FieldType.Message:
                if (value.ValueKind != JsonValueKind.Object)
                {
                    throw new GrpcJsonException(
                        $"'{path}': expected a JSON object for field '{field.JsonName}', found {Describe(value.ValueKind)}.");
                }
                byte[] nested = Encode(field.MessageType, value, path, depth + 1);
                output.WriteTag(field.FieldNumber, WireFormat.WireType.LengthDelimited);
                // A message field's wire encoding — length followed by the submessage's raw bytes —
                // is structurally identical to a bytes field's, so WriteBytes is the correct way to
                // emit already-encoded submessage bytes without an IMessage instance to hand it.
                output.WriteBytes(ByteString.CopyFrom(nested));
                break;

            case FieldType.Group:
                throw new GrpcJsonException(
                    $"'{path}': field '{field.JsonName}' uses the proto2 'group' wire type, which proto3 cannot express and this build does not support.");

            default:
                throw new GrpcJsonException(
                    $"'{path}': field '{field.JsonName}' has an unrecognized Protobuf field type ({field.FieldType}).");
        }
    }

    /// <summary>Writes the raw value only (no tag) for one element of a packed repeated field. Only
    /// ever called for a packable <see cref="FieldType"/> (see <see cref="IsPackable"/>) — string,
    /// bytes, message, and group are never packed and go through <see cref="WriteSingularValue"/>
    /// instead, tag included, once per element.</summary>
    private static void WriteRawScalarValue(CodedOutputStream output, FieldDescriptor field, JsonElement value, string path)
    {
        switch (field.FieldType)
        {
            case FieldType.Double: output.WriteDouble(ParseDouble(value, field, path)); break;
            case FieldType.Float: output.WriteFloat(ParseFloat(value, field, path)); break;
            case FieldType.Int32: output.WriteInt32(ParseInt32(value, field, path)); break;
            case FieldType.Int64: output.WriteInt64(ParseInt64(value, field, path)); break;
            case FieldType.UInt32: output.WriteUInt32(ParseUInt32(value, field, path)); break;
            case FieldType.UInt64: output.WriteUInt64(ParseUInt64(value, field, path)); break;
            case FieldType.SInt32: output.WriteSInt32(ParseInt32(value, field, path)); break;
            case FieldType.SInt64: output.WriteSInt64(ParseInt64(value, field, path)); break;
            case FieldType.Fixed32: output.WriteFixed32(ParseUInt32(value, field, path)); break;
            case FieldType.Fixed64: output.WriteFixed64(ParseUInt64(value, field, path)); break;
            case FieldType.SFixed32: output.WriteSFixed32(ParseInt32(value, field, path)); break;
            case FieldType.SFixed64: output.WriteSFixed64(ParseInt64(value, field, path)); break;
            case FieldType.Bool:
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    throw new GrpcJsonException(
                        $"'{path}': expected a boolean for field '{field.JsonName}', found {Describe(value.ValueKind)}.");
                }
                output.WriteBool(value.GetBoolean());
                break;
            case FieldType.Enum:
                output.WriteEnum(ResolveEnumValue(field, value, path));
                break;
            default:
                // Unreachable: WriteRepeatedField only calls this for a packable FieldType, and every
                // packable FieldType is handled above.
                throw new GrpcJsonException(
                    $"'{path}': field '{field.JsonName}' has Protobuf type {field.FieldType}, which is not a packable repeated type.");
        }
    }

    private static void WriteRepeatedField(CodedOutputStream output, FieldDescriptor field, JsonElement value, string path, int depth)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new GrpcJsonException(
                $"'{path}': expected a JSON array for repeated field '{field.JsonName}', found {Describe(value.ValueKind)}.");
        }

        if (IsPackable(field.FieldType))
        {
            // Packed by default in proto3: one tag, one length, the concatenated raw values.
            byte[] packedBytes;
            using (var innerBuffer = new MemoryStream())
            {
                using (var innerOutput = new CodedOutputStream(innerBuffer, leaveOpen: true))
                {
                    int index = 0;
                    foreach (var item in value.EnumerateArray())
                    {
                        WriteRawScalarValue(innerOutput, field, item, $"{path}[{index}]");
                        index++;
                    }
                    innerOutput.Flush();
                }
                packedBytes = innerBuffer.ToArray();
            }
            if (packedBytes.Length == 0) return; // an explicit empty array behaves like an absent field
            output.WriteTag(field.FieldNumber, WireFormat.WireType.LengthDelimited);
            output.WriteBytes(ByteString.CopyFrom(packedBytes));
            return;
        }

        // Non-packable (string, bytes, message): repeat the tag once per element.
        int i = 0;
        foreach (var item in value.EnumerateArray())
        {
            WriteSingularValue(output, field, item, $"{path}[{i}]", depth);
            i++;
        }
    }

    private static void WriteMapField(CodedOutputStream output, FieldDescriptor field, JsonElement value, string path, int depth)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new GrpcJsonException(
                $"'{path}': expected a JSON object for map field '{field.JsonName}', found {Describe(value.ValueKind)}.");
        }

        var keyField = field.MessageType.FindFieldByNumber(1);
        var valueField = field.MessageType.FindFieldByNumber(2);

        foreach (var property in value.EnumerateObject())
        {
            string entryPath = $"{path}.{property.Name}";
            var keyElement = ParseMapKey(keyField, property.Name, entryPath);

            byte[] entryBytes;
            using (var entryBuffer = new MemoryStream())
            {
                using (var entryOutput = new CodedOutputStream(entryBuffer, leaveOpen: true))
                {
                    WriteSingularValue(entryOutput, keyField, keyElement, entryPath, depth);
                    if (property.Value.ValueKind != JsonValueKind.Null)
                        WriteSingularValue(entryOutput, valueField, property.Value, entryPath, depth + 1);
                    entryOutput.Flush();
                }
                entryBytes = entryBuffer.ToArray();
            }

            output.WriteTag(field.FieldNumber, WireFormat.WireType.LengthDelimited);
            output.WriteBytes(ByteString.CopyFrom(entryBytes));
        }
    }

    /// <summary>A map's JSON property name, reparsed into the JSON representation the key field's own
    /// type expects: a quoted JSON string for a string key, or the bare literal (<c>7</c>,
    /// <c>true</c>) for every other legal key type — which is exactly what the property name's raw
    /// text already looks like for those types.</summary>
    private static JsonElement ParseMapKey(FieldDescriptor keyField, string propertyName, string path)
    {
        string literal = keyField.FieldType == FieldType.String
            ? JsonSerializer.Serialize(propertyName)
            : propertyName;
        try
        {
            using var document = JsonDocument.Parse(literal);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new GrpcJsonException(
                $"'{path}': map key '{propertyName}' is not valid for key type {keyField.FieldType}.");
        }
    }

    private static double ParseDouble(JsonElement value, FieldDescriptor field, string path)
    {
        if (value.ValueKind == JsonValueKind.Number) return value.GetDouble();
        if (value.ValueKind == JsonValueKind.String)
        {
            string s = value.GetString()!;
            if (s == "NaN") return double.NaN;
            if (s == "Infinity") return double.PositiveInfinity;
            if (s == "-Infinity") return double.NegativeInfinity;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)) return parsed;
        }
        throw new GrpcJsonException(
            $"'{path}': expected a number for field '{field.JsonName}', found {Describe(value.ValueKind)}.");
    }

    private static float ParseFloat(JsonElement value, FieldDescriptor field, string path)
    {
        if (value.ValueKind == JsonValueKind.Number) return value.GetSingle();
        if (value.ValueKind == JsonValueKind.String)
        {
            string s = value.GetString()!;
            if (s == "NaN") return float.NaN;
            if (s == "Infinity") return float.PositiveInfinity;
            if (s == "-Infinity") return float.NegativeInfinity;
            if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)) return parsed;
        }
        throw new GrpcJsonException(
            $"'{path}': expected a number for field '{field.JsonName}', found {Describe(value.ValueKind)}.");
    }

    private static int ParseInt32(JsonElement value, FieldDescriptor field, string path)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int n)) return n;
        if (value.ValueKind == JsonValueKind.String &&
            int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int s)) return s;
        throw new GrpcJsonException(
            $"'{path}': expected a number or numeric string for field '{field.JsonName}', found {Describe(value.ValueKind)}.");
    }

    private static uint ParseUInt32(JsonElement value, FieldDescriptor field, string path)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out uint n)) return n;
        if (value.ValueKind == JsonValueKind.String &&
            uint.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint s)) return s;
        throw new GrpcJsonException(
            $"'{path}': expected a number or numeric string for field '{field.JsonName}', found {Describe(value.ValueKind)}.");
    }

    private static long ParseInt64(JsonElement value, FieldDescriptor field, string path)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long n)) return n;
        if (value.ValueKind == JsonValueKind.String &&
            long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long s)) return s;
        throw new GrpcJsonException(
            $"'{path}': expected a number or numeric string for field '{field.JsonName}', found {Describe(value.ValueKind)}.");
    }

    private static ulong ParseUInt64(JsonElement value, FieldDescriptor field, string path)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out ulong n)) return n;
        if (value.ValueKind == JsonValueKind.String &&
            ulong.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong s)) return s;
        throw new GrpcJsonException(
            $"'{path}': expected a number or numeric string for field '{field.JsonName}', found {Describe(value.ValueKind)}.");
    }

    /// <summary>Accepts standard base64 (padded) as well as the URL-safe alphabet and missing padding,
    /// since a real sender is not obliged to use the canonical form on the way in.</summary>
    private static ByteString ParseBytes(JsonElement value, FieldDescriptor field, string path)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new GrpcJsonException(
                $"'{path}': expected a base64 string for field '{field.JsonName}', found {Describe(value.ValueKind)}.");
        }
        string raw = value.GetString()!;
        string normalized = raw.Replace('-', '+').Replace('_', '/');
        int padding = (4 - normalized.Length % 4) % 4;
        normalized = normalized.PadRight(normalized.Length + padding, '=');
        try
        {
            return ByteString.CopyFrom(Convert.FromBase64String(normalized));
        }
        catch (FormatException)
        {
            throw new GrpcJsonException($"'{path}': field '{field.JsonName}' is not valid base64.");
        }
    }

    private static int ResolveEnumValue(FieldDescriptor field, JsonElement value, string path)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            string name = value.GetString()!;
            var found = field.EnumType.FindValueByName(name);
            if (found is null)
            {
                throw new GrpcJsonException(
                    $"'{path}': unknown enum value '{name}' for field '{field.JsonName}' ({field.EnumType.FullName}).");
            }
            return found.Number;
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)) return number;

        throw new GrpcJsonException(
            $"'{path}': expected an enum name or number for field '{field.JsonName}', found {Describe(value.ValueKind)}.");
    }

    /// <summary>The packable Protobuf types: every numeric type plus bool and enum. string, bytes,
    /// message, and group are never packed regardless of repetition.</summary>
    private static bool IsPackable(FieldType type) => type switch
    {
        FieldType.String or FieldType.Bytes or FieldType.Message or FieldType.Group => false,
        _ => true
    };

    private static bool TryGetField(JsonElement element, FieldDescriptor field, out JsonElement value, out string matchedName)
    {
        if (element.TryGetProperty(field.JsonName, out value)) { matchedName = field.JsonName; return true; }
        if (field.JsonName != field.Name && element.TryGetProperty(field.Name, out value)) { matchedName = field.Name; return true; }
        value = default;
        matchedName = "";
        return false;
    }

    private static string Label(string path, MessageDescriptor descriptor) =>
        string.IsNullOrEmpty(path) ? descriptor.FullName : path;

    private static string Describe(JsonValueKind kind) => kind switch
    {
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        JsonValueKind.Array => "an array",
        JsonValueKind.Object => "an object",
        JsonValueKind.Null => "null",
        _ => kind.ToString()
    };
}
