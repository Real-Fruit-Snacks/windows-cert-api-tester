using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApiTester.Grpc;
using Certapi.Test;
using Google.Protobuf;
using Google.Protobuf.Reflection;
// Aliased to avoid colliding with this project's own ApiTester.Grpc.WellKnownTypes: Google.Protobuf
// keeps its well-known generated message types under the Google.Protobuf.WellKnownTypes namespace.
using WktTimestamp = Google.Protobuf.WellKnownTypes.Timestamp;
using WktDuration = Google.Protobuf.WellKnownTypes.Duration;
using WktStruct = Google.Protobuf.WellKnownTypes.Struct;
using WktValue = Google.Protobuf.WellKnownTypes.Value;
using WktListValue = Google.Protobuf.WellKnownTypes.ListValue;
using WktEmpty = Google.Protobuf.WellKnownTypes.Empty;
using WktFieldMask = Google.Protobuf.WellKnownTypes.FieldMask;
using WktAny = Google.Protobuf.WellKnownTypes.Any;

namespace ApiTester.Tests.Grpc;

/// <summary>Pure unit tests over descriptors and bytes for <see cref="ProtoJsonReader"/> and
/// <see cref="ProtoJsonWriter"/> — no server, no network, no channel. The two headline tests compare
/// our converter differentially against Google's own <see cref="JsonFormatter"/>/<see cref="JsonParser"/>
/// on the identical generated <see cref="AllTypes"/> message: that comparison is the justification for
/// this project owning a hand-rolled JSON&lt;-&gt;Protobuf layer at all (see the header comments in
/// ProtoJsonReader.cs / ProtoJsonWriter.cs for why JsonFormatter/JsonParser cannot be used directly
/// against runtime-built descriptors).</summary>
public class ProtoJsonTests
{
    // Rebuilt from AllTypes' own generated FileDescriptorProto bytes, exactly the way DescriptorPool
    // rebuilds descriptors from server-reflection bytes. The result has ClrType == null and no
    // Parser — the real condition ProtoJsonReader/Writer must operate under — unlike the generated
    // AllTypes.Descriptor, which is backed by CLR code these tests must not lean on. Now that
    // certapi_test.proto imports google/protobuf/*.proto, the closure this rebuilds spans multiple
    // files: FileDescriptor.BuildFromByteStrings requires every file's dependencies to precede it in
    // the input, so BuildRuntimeFileClosure walks the dependency graph depth-first below and emits
    // each file only after everything it depends on — the same dependency-first ordering
    // DescriptorPool's own TopologicalSort produces from server-reflection bytes.
    private static readonly IReadOnlyList<FileDescriptor> RuntimeFiles = BuildRuntimeFileClosure();

    private static IReadOnlyList<FileDescriptor> BuildRuntimeFileClosure()
    {
        var ordered = new List<FileDescriptorProto>();
        var visited = new HashSet<string>(StringComparer.Ordinal);

        void Visit(FileDescriptor file)
        {
            if (!visited.Add(file.Name)) return;
            foreach (var dependency in file.Dependencies) Visit(dependency);
            ordered.Add(file.ToProto());
        }

        Visit(AllTypes.Descriptor.File);

        return FileDescriptor.BuildFromByteStrings(ordered.Select(p => p.ToByteString()).ToList());
    }

    private static MessageDescriptor Runtime(string fullName) =>
        RuntimeFiles.SelectMany(f => f.MessageTypes).First(m => m.FullName == fullName);

    private static readonly MessageDescriptor AllTypesDescriptor = Runtime("certapi.test.AllTypes");

    private static readonly MessageDescriptor NestedDescriptor = Runtime("certapi.test.Nested");

    private static readonly MessageDescriptor AnyDescriptor = Runtime("google.protobuf.Any");

    /// <summary>Resolves a message by full name across the whole runtime-built closure — the stand-in
    /// for DescriptorPool's own lookup, which is what resolves an Any payload's type URL in production.</summary>
    private static MessageDescriptor? ResolveRuntimeMessage(string fullName) =>
        RuntimeFiles.SelectMany(f => f.MessageTypes).FirstOrDefault(m => m.FullName == fullName);

    private static AllTypes BuildFullyPopulatedAllTypes()
    {
        var message = new AllTypes
        {
            FDouble = 1.0 / 3.0,
            FFloat = 1.0f / 3.0f,
            FInt32 = -7,
            FInt64 = -123456789012345L,
            FUint32 = 42u,
            FUint64 = 18446744073709551615UL,
            FSint32 = -3,
            FSint64 = -9876543210L,
            FFixed32 = 99u,
            FFixed64 = 9999999999UL,
            FSfixed32 = -99,
            FSfixed64 = -9999999999L,
            FBool = true,
            FString = "hello",
            FBytes = ByteString.CopyFrom(0xDE, 0xAD, 0xBE, 0xEF, 0x01),
            FEnum = Color.Green,
            FNested = new Nested
            {
                Label = "outer",
                Depth = 1,
                Child = new Nested { Label = "inner", Depth = 2 }
            },
            SnakeCaseName = "snake value",
            ChoiceString = "picked"
        };
        message.RString.Add("a");
        message.RString.Add("b");
        message.RInt32.Add(1);
        message.RInt32.Add(-2);
        message.RInt32.Add(3);
        message.REnum.Add(Color.Red);
        message.REnum.Add(Color.Green);
        message.RNested.Add(new Nested { Label = "r1" });
        message.RNested.Add(new Nested { Label = "r2", Depth = 4 });
        message.MString["k1"] = "v1";
        message.MString["k2"] = "v2";
        message.MNested["a"] = new Nested { Label = "na" };
        message.MNested["b"] = new Nested { Label = "nb", Depth = 5 };
        message.MIntKey[7] = "seven";
        message.MIntKey[-3] = "neg-three";

        // Well-known-type fields: every one the fixture declares, populated with distinctive, FIXED
        // values chosen to expose a bug rather than merely exercise the happy path — non-zero
        // fractional seconds on the timestamp, a negative duration, a large Int64Value, a Struct
        // containing a nested object, and a FieldMask path needing snake_case-to-camelCase conversion.
        message.FTimestamp = new WktTimestamp { Seconds = 1700000000, Nanos = 123456789 };
        message.FDuration = new WktDuration { Seconds = -3, Nanos = -500000000 };
        message.WDouble = 1.5;
        message.WFloat = 2.5f;
        message.WInt64 = 123456789012345L;
        message.WUint64 = 18446744073709551615UL;
        message.WInt32 = 7;
        message.WUint32 = 42u;
        message.WBool = true;
        message.WString = "wrapped";
        message.WBytes = ByteString.CopyFrom(0xCA, 0xFE);
        message.FStruct = new WktStruct
        {
            Fields =
            {
                ["k"] = WktValue.ForString("v"),
                ["nested"] = WktValue.ForStruct(new WktStruct { Fields = { ["inner"] = WktValue.ForNumber(1) } })
            }
        };
        message.FValue = WktValue.ForNumber(3.5);
        message.FList = new WktListValue { Values = { WktValue.ForBool(true), WktValue.ForNull() } };
        message.FMask = new WktFieldMask { Paths = { "user.display_name", "photo" } };
        message.FEmpty = new WktEmpty();
        message.FAny = WktAny.Pack(new Nested { Label = "packed", Depth = 7 });
        message.RTimestamp.Add(new WktTimestamp { Seconds = 0, Nanos = 0 });
        message.RTimestamp.Add(new WktTimestamp { Seconds = 1700000000, Nanos = 500000000 });
        message.MTimestamp["a"] = new WktTimestamp { Seconds = -1, Nanos = 0 };
        message.MTimestamp["b"] = new WktTimestamp { Seconds = 1700000000, Nanos = 500500000 };

        return message;
    }

    private static void AssertJsonEquivalent(string expectedJson, string actualJson)
    {
        var expected = JsonNode.Parse(expectedJson);
        var actual = JsonNode.Parse(actualJson);
        Assert.True(JsonNode.DeepEquals(expected, actual),
            $"Expected:\n{expected?.ToJsonString()}\nActual:\n{actual?.ToJsonString()}");
    }

    // ---- The two headline differential tests ----

    [Fact]
    public void Reader_output_matches_JsonFormatter_for_a_message_with_every_field_populated()
    {
        var message = BuildFullyPopulatedAllTypes();

        // JsonFormatter.Default has an EMPTY TypeRegistry and throws on any Any, so — now that
        // FAny is populated — this must use a registry-backed formatter instead. A registry-backed
        // JsonFormatter is identical to .Default in every other respect, so this changes nothing else
        // about what this test proves.
        var registry = TypeRegistry.FromMessages(Nested.Descriptor);
        var formatter = new JsonFormatter(JsonFormatter.Settings.Default.WithTypeRegistry(registry));

        string ours = ProtoJsonReader.ToJson(AllTypesDescriptor, message.ToByteArray(), indented: false, ResolveRuntimeMessage);
        string google = formatter.Format(message);

        AssertJsonEquivalent(google, ours);
    }

    [Fact]
    public void Writer_output_matches_JsonParser_for_a_message_with_every_field_populated()
    {
        var message = BuildFullyPopulatedAllTypes();

        // See the reader test above: JsonParser.Default also has an empty TypeRegistry and throws on
        // any Any, so this needs the same registry-backed instances.
        var registry = TypeRegistry.FromMessages(Nested.Descriptor);
        var formatter = new JsonFormatter(JsonFormatter.Settings.Default.WithTypeRegistry(registry));
        var parser = new JsonParser(JsonParser.Settings.Default.WithTypeRegistry(registry));
        string json = formatter.Format(message);

        byte[] ourBytes = ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, json, ResolveRuntimeMessage);
        var ourParsed = AllTypes.Parser.ParseFrom(ourBytes);
        var googleParsed = parser.Parse<AllTypes>(json);

        Assert.Equal(googleParsed, ourParsed);
    }

    // ---- Well-known-type descriptor closure plumbing (slice A) ----
    // These two tests prove the fixture and the multi-file runtime closure are wired up correctly;
    // they do not exercise any well-known-type conversion behavior, which later slices own.

    [Fact]
    public void The_multi_file_dependency_closure_resolves_well_known_types_as_runtime_descriptors_with_no_clr_type()
    {
        // f_timestamp = 28. Looking it up by field number (rather than by name) keeps this test
        // agnostic to whether the descriptor uses the proto field name or the JSON name.
        var timestampField = AllTypesDescriptor.FindFieldByNumber(28);
        Assert.Equal("google.protobuf.Timestamp", timestampField.MessageType.FullName);

        // This is the load-bearing assertion: it pins the exact condition (ClrType == null on a
        // runtime-built descriptor) that forces well-known-type detection to go by full type name
        // rather than by CLR type, which is the whole reason ProtoJsonReader/Writer exist at all.
        var resolvedTimestamp = ResolveRuntimeMessage("google.protobuf.Timestamp");
        Assert.NotNull(resolvedTimestamp);
        Assert.Null(resolvedTimestamp!.ClrType);
        Assert.Null(timestampField.MessageType.ClrType);
    }

    // ---- Well-known types: Timestamp and Duration (slice C) ----
    // Wrappers, Struct/Value/ListValue, FieldMask, and Any are out of scope for this slice (D-G land
    // them); until then they keep rendering as ordinary nested messages.

    [Fact]
    public void Reader_renders_timestamp_and_duration_fields_identically_to_JsonFormatter_in_singular_repeated_and_map_positions()
    {
        var message = new AllTypes
        {
            FTimestamp = new WktTimestamp { Seconds = 1700000000, Nanos = 123000000 },
            FDuration = new WktDuration { Seconds = -3, Nanos = -500000000 },
        };
        message.RTimestamp.Add(new WktTimestamp { Seconds = 0, Nanos = 0 });
        message.RTimestamp.Add(new WktTimestamp { Seconds = 1700000000, Nanos = 500000000 });
        message.MTimestamp["a"] = new WktTimestamp { Seconds = -1, Nanos = 0 };
        message.MTimestamp["b"] = new WktTimestamp { Seconds = 1700000000, Nanos = 500500000 };

        string ours = ProtoJsonReader.ToJson(AllTypesDescriptor, message.ToByteArray(), indented: false);
        string google = JsonFormatter.Default.Format(message);

        AssertJsonEquivalent(google, ours);
    }

    [Fact]
    public void Writer_encodes_timestamp_and_duration_fields_to_bytes_that_parse_identically_to_JsonParser()
    {
        var message = new AllTypes
        {
            FTimestamp = new WktTimestamp { Seconds = 1700000000, Nanos = 123000000 },
            FDuration = new WktDuration { Seconds = -3, Nanos = -500000000 },
        };
        message.RTimestamp.Add(new WktTimestamp { Seconds = 0, Nanos = 0 });
        message.RTimestamp.Add(new WktTimestamp { Seconds = 1700000000, Nanos = 500000000 });
        message.MTimestamp["a"] = new WktTimestamp { Seconds = -1, Nanos = 0 };
        message.MTimestamp["b"] = new WktTimestamp { Seconds = 1700000000, Nanos = 500500000 };
        string json = JsonFormatter.Default.Format(message);

        byte[] ourBytes = ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, json);
        var ourParsed = AllTypes.Parser.ParseFrom(ourBytes);
        var googleParsed = JsonParser.Default.Parse<AllTypes>(json);

        Assert.Equal(googleParsed, ourParsed);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1700000000, 500000000)]
    [InlineData(1700000000, 500500000)]
    [InlineData(1700000000, 123456789)]
    public void Timestamp_renders_with_exactly_the_canonical_number_of_fractional_digits(long seconds, int nanos)
    {
        var message = new AllTypes { FTimestamp = new WktTimestamp { Seconds = seconds, Nanos = nanos } };

        string ours = ProtoJsonReader.ToJson(AllTypesDescriptor, message.ToByteArray(), indented: false);
        string google = JsonFormatter.Default.Format(message);

        AssertJsonEquivalent(google, ours);

        string rendered = JsonDocument.Parse(ours).RootElement.GetProperty("fTimestamp").GetString()!;
        int dot = rendered.IndexOf('.');
        int fractionalDigits = dot < 0 ? 0 : rendered.IndexOf('Z') - dot - 1;
        int expectedDigits = nanos == 0 ? 0 : nanos % 1000 != 0 ? 9 : nanos % 1000000 != 0 ? 6 : 3;
        Assert.Equal(expectedDigits, fractionalDigits);
    }

    [Fact]
    public void A_pre_epoch_negative_timestamp_renders_and_round_trips()
    {
        var message = new AllTypes { FTimestamp = new WktTimestamp { Seconds = -1, Nanos = 0 } };
        string google = JsonFormatter.Default.Format(message);

        string ours = ProtoJsonReader.ToJson(AllTypesDescriptor, message.ToByteArray(), indented: false);
        AssertJsonEquivalent(google, ours);

        byte[] ourBytes = ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, google);
        Assert.Equal(JsonParser.Default.Parse<AllTypes>(google), AllTypes.Parser.ParseFrom(ourBytes));
    }

    [Fact]
    public void A_1960s_date_timestamp_renders_and_round_trips()
    {
        // 1960-01-01T00:00:00Z is seconds = -315619200 relative to the Unix epoch.
        var message = new AllTypes { FTimestamp = new WktTimestamp { Seconds = -315619200, Nanos = 0 } };
        string google = JsonFormatter.Default.Format(message);

        string ours = ProtoJsonReader.ToJson(AllTypesDescriptor, message.ToByteArray(), indented: false);
        AssertJsonEquivalent(google, ours);

        byte[] ourBytes = ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, google);
        Assert.Equal(JsonParser.Default.Parse<AllTypes>(google), AllTypes.Parser.ParseFrom(ourBytes));
    }

    [Fact]
    public void The_exact_unix_epoch_which_transmits_as_zero_bytes_still_renders_as_a_timestamp_string()
    {
        var message = new AllTypes { FTimestamp = new WktTimestamp { Seconds = 0, Nanos = 0 } };

        string ours = ProtoJsonReader.ToJson(AllTypesDescriptor, message.ToByteArray(), indented: false);

        Assert.Equal("1970-01-01T00:00:00Z",
            JsonDocument.Parse(ours).RootElement.GetProperty("fTimestamp").GetString());
    }

    [Theory]
    [InlineData(-3, 0, "-3s")]
    [InlineData(0, -500000000, "-0.500s")]
    [InlineData(1, 500000000, "1.500s")]
    [InlineData(0, 0, "0s")]
    [InlineData(315576000000, 0, "315576000000s")]
    public void Duration_renders_with_the_expected_canonical_string(long seconds, int nanos, string expected)
    {
        var message = new AllTypes { FDuration = new WktDuration { Seconds = seconds, Nanos = nanos } };

        string ours = ProtoJsonReader.ToJson(AllTypesDescriptor, message.ToByteArray(), indented: false);
        string google = JsonFormatter.Default.Format(message);

        AssertJsonEquivalent(google, ours);
        Assert.Equal(expected, JsonDocument.Parse(ours).RootElement.GetProperty("fDuration").GetString());
    }

    [Theory]
    [InlineData("""{"fTimestamp":"2023-11-14T22:13:20Z"}""")]
    [InlineData("""{"fDuration":"1.500s"}""")]
    public void Json_string_in_produces_the_identical_json_string_out_for_timestamp_and_duration(string json)
    {
        byte[] bytes = ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, json);
        string roundTripped = ProtoJsonReader.ToJson(AllTypesDescriptor, bytes, indented: false);

        AssertJsonEquivalent(json, roundTripped);
    }

    [Fact]
    public void A_numeric_utc_offset_timestamp_encodes_to_the_same_bytes_as_the_equivalent_Z_form()
    {
        byte[] offsetBytes = ProtoJsonWriter.ToProtobuf(AllTypesDescriptor,
            """{"fTimestamp":"2023-11-14T22:13:20+01:00"}""");
        byte[] zBytes = ProtoJsonWriter.ToProtobuf(AllTypesDescriptor,
            """{"fTimestamp":"2023-11-14T21:13:20Z"}""");

        Assert.Equal(zBytes, offsetBytes);
    }

    [Fact]
    public void A_malformed_timestamp_string_in_input_json_names_the_field()
    {
        var ex = Assert.Throws<GrpcJsonException>(() =>
            ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, """{"fTimestamp":"not-a-time"}"""));

        Assert.Contains("fTimestamp", ex.Message);
    }

    [Fact]
    public void A_malformed_duration_string_in_input_json_names_the_field()
    {
        var ex = Assert.Throws<GrpcJsonException>(() =>
            ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, """{"fDuration":"not-a-duration"}"""));

        Assert.Contains("fDuration", ex.Message);
    }

    [Fact]
    public void A_timestamp_given_as_the_ordinary_seconds_and_nanos_object_still_encodes_successfully()
    {
        byte[] bytes = ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, """{"fTimestamp":{"seconds":5,"nanos":0}}""");

        var parsed = AllTypes.Parser.ParseFrom(bytes);
        Assert.Equal(5, parsed.FTimestamp.Seconds);
    }

    [Fact]
    public void A_non_normalized_timestamp_on_the_wire_falls_back_to_the_ordinary_object_rendering_instead_of_throwing()
    {
        // Hand-build a Timestamp submessage whose nanos field (2000000000) is out of the [0, 999999999]
        // range Protobuf requires — non-normalized, the way a buggy server might send it.
        byte[] timestampBytes;
        using (var buffer = new MemoryStream())
        {
            using (var output = new CodedOutputStream(buffer, leaveOpen: true))
            {
                output.WriteTag(1, WireFormat.WireType.Varint); // Timestamp.seconds
                output.WriteInt64(1700000000);
                output.WriteTag(2, WireFormat.WireType.Varint); // Timestamp.nanos
                output.WriteInt32(2000000000);
                output.Flush();
            }
            timestampBytes = buffer.ToArray();
        }

        byte[] outerBytes;
        using (var buffer = new MemoryStream())
        {
            using (var output = new CodedOutputStream(buffer, leaveOpen: true))
            {
                output.WriteTag(28, WireFormat.WireType.LengthDelimited); // f_timestamp
                output.WriteBytes(ByteString.CopyFrom(timestampBytes));
                output.Flush();
            }
            outerBytes = buffer.ToArray();
        }

        string json = ProtoJsonReader.ToJson(AllTypesDescriptor, outerBytes, indented: false);

        using var document = JsonDocument.Parse(json);
        var fTimestamp = document.RootElement.GetProperty("fTimestamp");
        Assert.Equal(JsonValueKind.Object, fTimestamp.ValueKind);
        Assert.True(fTimestamp.TryGetProperty("nanos", out var nanosValue));
        Assert.Equal(2000000000, nanosValue.GetInt32());
    }

    // ---- Well-known types: the nine wrapper types (slice D) ----
    // Struct/Value/ListValue, FieldMask, and Any are still out of scope (slices E-G land them); until
    // then they keep rendering as ordinary nested messages.

    [Fact]
    public void Reader_renders_wrapper_fields_identically_to_JsonFormatter()
    {
        var message = new AllTypes
        {
            WDouble = 1.5,
            WFloat = 2.5f,
            WInt64 = 123456789012345L,
            WUint64 = 18446744073709551615UL,
            WInt32 = 7,
            WUint32 = 42u,
            WBool = true,
            WString = "hello",
            WBytes = ByteString.CopyFrom(0xDE, 0xAD, 0xBE, 0xEF)
        };

        string ours = ProtoJsonReader.ToJson(AllTypesDescriptor, message.ToByteArray(), indented: false);
        string google = JsonFormatter.Default.Format(message);

        AssertJsonEquivalent(google, ours);
    }

    [Fact]
    public void Writer_encodes_wrapper_fields_to_bytes_that_parse_identically_to_JsonParser()
    {
        var message = new AllTypes
        {
            WDouble = 1.5,
            WFloat = 2.5f,
            WInt64 = 123456789012345L,
            WUint64 = 18446744073709551615UL,
            WInt32 = 7,
            WUint32 = 42u,
            WBool = true,
            WString = "hello",
            WBytes = ByteString.CopyFrom(0xDE, 0xAD, 0xBE, 0xEF)
        };
        string json = JsonFormatter.Default.Format(message);

        byte[] ourBytes = ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, json);
        var ourParsed = AllTypes.Parser.ParseFrom(ourBytes);
        var googleParsed = JsonParser.Default.Parse<AllTypes>(json);

        Assert.Equal(googleParsed, ourParsed);
    }

    [Fact]
    public void A_wrapper_holding_its_type_default_still_renders_as_the_bare_default_not_an_empty_object()
    {
        var message = new AllTypes { WInt32 = 0, WString = "", WBool = false };

        string ours = ProtoJsonReader.ToJson(AllTypesDescriptor, message.ToByteArray(), indented: false);
        string google = JsonFormatter.Default.Format(message);

        // Differential first: proves our rendering matches Google's for the exact case a naive
        // implementation gets wrong, since a wrapper carrying its default value serializes to a
        // zero-length submessage on the wire — indistinguishable, on the wire alone, from "absent".
        AssertJsonEquivalent(google, ours);

        using var document = JsonDocument.Parse(ours);
        Assert.Equal(0, document.RootElement.GetProperty("wInt32").GetInt32());
        Assert.Equal("", document.RootElement.GetProperty("wString").GetString());
        Assert.False(document.RootElement.GetProperty("wBool").GetBoolean());
    }

    [Fact]
    public void A_wrapper_field_that_was_never_set_is_omitted_from_the_output_entirely()
    {
        var message = new AllTypes { FInt32 = 5 }; // wrapper fields deliberately left unset

        string ours = ProtoJsonReader.ToJson(AllTypesDescriptor, message.ToByteArray(), indented: false);

        using var document = JsonDocument.Parse(ours);
        Assert.False(document.RootElement.TryGetProperty("wInt32", out _));
        Assert.False(document.RootElement.TryGetProperty("wString", out _));
        Assert.False(document.RootElement.TryGetProperty("wBool", out _));
    }

    [Fact]
    public void Int64Value_and_UInt64Value_render_as_json_strings_not_numbers()
    {
        var message = new AllTypes { WInt64 = 123456789012345L, WUint64 = 18446744073709551615UL };

        string ours = ProtoJsonReader.ToJson(AllTypesDescriptor, message.ToByteArray(), indented: false);

        using var document = JsonDocument.Parse(ours);
        var wInt64 = document.RootElement.GetProperty("wInt64");
        var wUint64 = document.RootElement.GetProperty("wUint64");
        Assert.Equal(JsonValueKind.String, wInt64.ValueKind);
        Assert.Equal("123456789012345", wInt64.GetString());
        Assert.Equal(JsonValueKind.String, wUint64.ValueKind);
        Assert.Equal("18446744073709551615", wUint64.GetString());
    }

    [Fact]
    public void Int64Value_accepts_both_a_json_number_and_a_json_string_and_both_encode_identically()
    {
        byte[] bytesFromNumber = ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, """{"wInt64":123456789012345}""");
        byte[] bytesFromString = ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, """{"wInt64":"123456789012345"}""");

        Assert.Equal(bytesFromString, bytesFromNumber);
    }

    [Fact]
    public void BytesValue_round_trips_through_base64_and_accepts_url_safe_unpadded_input()
    {
        byte[] raw = { 0xDE, 0xAD, 0xBE, 0xEF, 0x01 };
        string standardBase64 = Convert.ToBase64String(raw);
        Assert.EndsWith("=", standardBase64); // proves this sample actually needs padding
        string urlSafeUnpadded = standardBase64.TrimEnd('=').Replace('+', '-').Replace('/', '_');

        byte[] bytesFromStandard = ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, $$"""{"wBytes":"{{standardBase64}}"}""");
        byte[] bytesFromUrlSafe = ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, $$"""{"wBytes":"{{urlSafeUnpadded}}"}""");

        string outputStandard = ProtoJsonReader.ToJson(AllTypesDescriptor, bytesFromStandard, indented: false);
        string outputUrlSafe = ProtoJsonReader.ToJson(AllTypesDescriptor, bytesFromUrlSafe, indented: false);

        using var document = JsonDocument.Parse(outputStandard);
        Assert.Equal(standardBase64, document.RootElement.GetProperty("wBytes").GetString());
        AssertJsonEquivalent(outputStandard, outputUrlSafe);
    }

    [Fact]
    public void DoubleValue_and_FloatValue_wrappers_survive_NaN_and_infinity_in_both_directions()
    {
        byte[] bytes = ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, """{"wDouble":"NaN","wFloat":"Infinity"}""");
        string output = ProtoJsonReader.ToJson(AllTypesDescriptor, bytes, indented: false);

        using var document = JsonDocument.Parse(output);
        Assert.Equal("NaN", document.RootElement.GetProperty("wDouble").GetString());
        Assert.Equal("Infinity", document.RootElement.GetProperty("wFloat").GetString());

        byte[] negBytes = ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, """{"wFloat":"-Infinity"}""");
        string negOutput = ProtoJsonReader.ToJson(AllTypesDescriptor, negBytes, indented: false);
        Assert.Equal("-Infinity", JsonDocument.Parse(negOutput).RootElement.GetProperty("wFloat").GetString());
    }

    [Theory]
    [InlineData("""{"wInt32":7}""")]
    [InlineData("""{"wString":"x"}""")]
    [InlineData("""{"wBool":true}""")]
    [InlineData("""{"wDouble":1.5}""")]
    [InlineData("""{"wBytes":"3q2+7w=="}""")]
    public void Bare_wrapper_value_json_in_produces_the_identical_json_out(string json)
    {
        byte[] bytes = ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, json);
        string roundTripped = ProtoJsonReader.ToJson(AllTypesDescriptor, bytes, indented: false);

        AssertJsonEquivalent(json, roundTripped);
    }

    [Fact]
    public void A_malformed_wrapper_value_in_input_json_names_the_field()
    {
        var ex = Assert.Throws<GrpcJsonException>(() =>
            ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, """{"wInt32":"not-a-number"}"""));

        Assert.Contains("wInt32", ex.Message);
    }

    [Fact]
    public void A_wrapper_given_as_the_ordinary_value_object_still_encodes_to_the_same_bytes_as_the_bare_form()
    {
        byte[] fromObject = ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, """{"wInt32":{"value":7}}""");
        byte[] fromBareValue = ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, """{"wInt32":7}""");

        Assert.Equal(fromBareValue, fromObject);
    }

    // ---- Well-known types: Struct, Value, ListValue, and Empty (slice E) ----
    // FieldMask and Any are still out of scope (slices F-G land them); until then they keep rendering
    // as ordinary nested messages.

    [Fact]
    public void Reader_renders_struct_value_list_and_empty_fields_identically_to_JsonFormatter()
    {
        var message = new AllTypes
        {
            FStruct = new WktStruct { Fields = { ["k"] = WktValue.ForString("v") } },
            FValue = WktValue.ForNumber(3.5),
            FList = new WktListValue { Values = { WktValue.ForBool(true), WktValue.ForNull() } },
            FEmpty = new WktEmpty()
        };

        string ours = ProtoJsonReader.ToJson(AllTypesDescriptor, message.ToByteArray(), indented: false);
        string google = JsonFormatter.Default.Format(message);

        AssertJsonEquivalent(google, ours);
    }

    [Fact]
    public void Writer_encodes_struct_value_list_and_empty_fields_to_bytes_that_parse_identically_to_JsonParser()
    {
        var message = new AllTypes
        {
            FStruct = new WktStruct { Fields = { ["k"] = WktValue.ForString("v") } },
            FValue = WktValue.ForNumber(3.5),
            FList = new WktListValue { Values = { WktValue.ForBool(true), WktValue.ForNull() } },
            FEmpty = new WktEmpty()
        };
        string json = JsonFormatter.Default.Format(message);

        byte[] ourBytes = ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, json);
        var ourParsed = AllTypes.Parser.ParseFrom(ourBytes);
        var googleParsed = JsonParser.Default.Parse<AllTypes>(json);

        Assert.Equal(googleParsed, ourParsed);
    }

    [Fact]
    public void A_struct_containing_null_a_nested_object_and_an_array_round_trips_through_writer_then_reader()
    {
        // The headline case for this slice: NULL, a nested object, and an array all inside one
        // Struct, in a single round trip.
        const string json = """{"fStruct":{"a":null,"b":{"c":1},"d":[1,2,3]}}""";

        byte[] bytes = ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, json);
        string roundTripped = ProtoJsonReader.ToJson(AllTypesDescriptor, bytes, indented: false);

        AssertJsonEquivalent(json, roundTripped);
    }

    [Fact]
    public void A_struct_entry_explicitly_set_to_null_survives_encoding_instead_of_vanishing()
    {
        const string json = """{"fStruct":{"a":null}}""";

        byte[] bytes = ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, json);
        string roundTripped = ProtoJsonReader.ToJson(AllTypesDescriptor, bytes, indented: false);

        using var document = JsonDocument.Parse(roundTripped);
        var fStruct = document.RootElement.GetProperty("fStruct");
        Assert.True(fStruct.TryGetProperty("a", out var aValue));
        Assert.Equal(JsonValueKind.Null, aValue.ValueKind);
    }

    [Fact]
    public void A_value_field_set_to_json_null_round_trips_as_null_and_matches_JsonParser()
    {
        const string json = """{"fValue":null}""";

        byte[] ourBytes = ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, json);
        var ourParsed = AllTypes.Parser.ParseFrom(ourBytes);
        var googleParsed = JsonParser.Default.Parse<AllTypes>(json);
        Assert.Equal(googleParsed, ourParsed);

        string roundTripped = ProtoJsonReader.ToJson(AllTypesDescriptor, ourBytes, indented: false);
        using var document = JsonDocument.Parse(roundTripped);
        Assert.True(document.RootElement.TryGetProperty("fValue", out var fValue));
        Assert.Equal(JsonValueKind.Null, fValue.ValueKind);
    }

    [Fact]
    public void A_value_holding_false_or_zero_round_trips_as_false_or_zero_not_as_null_or_omitted()
    {
        byte[] falseBytes = ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, """{"fValue":false}""");
        byte[] zeroBytes = ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, """{"fValue":0}""");

        string falseJson = ProtoJsonReader.ToJson(AllTypesDescriptor, falseBytes, indented: false);
        string zeroJson = ProtoJsonReader.ToJson(AllTypesDescriptor, zeroBytes, indented: false);

        var falseValue = JsonDocument.Parse(falseJson).RootElement.GetProperty("fValue");
        var zeroValue = JsonDocument.Parse(zeroJson).RootElement.GetProperty("fValue");
        Assert.Equal(JsonValueKind.False, falseValue.ValueKind);
        Assert.Equal(JsonValueKind.Number, zeroValue.ValueKind);
        Assert.Equal(0, zeroValue.GetDouble());
    }

    [Theory]
    [InlineData("""{"fValue":"hello"}""")]
    [InlineData("""{"fValue":{"a":1,"b":"two"}}""")]
    [InlineData("""{"fValue":[1,"two",false]}""")]
    public void A_value_holding_a_string_a_nested_struct_or_a_list_round_trips(string json)
    {
        byte[] bytes = ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, json);
        string roundTripped = ProtoJsonReader.ToJson(AllTypesDescriptor, bytes, indented: false);

        AssertJsonEquivalent(json, roundTripped);
    }

    [Fact]
    public void A_list_value_with_mixed_element_kinds_round_trips()
    {
        const string json = """{"fList":["s",1,null,{"a":1},[1,2]]}""";

        byte[] bytes = ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, json);
        string roundTripped = ProtoJsonReader.ToJson(AllTypesDescriptor, bytes, indented: false);

        AssertJsonEquivalent(json, roundTripped);
    }

    [Fact]
    public void An_empty_struct_renders_as_an_empty_object_and_an_empty_list_value_as_an_empty_array()
    {
        var message = new AllTypes { FStruct = new WktStruct(), FList = new WktListValue() };

        string ours = ProtoJsonReader.ToJson(AllTypesDescriptor, message.ToByteArray(), indented: false);
        string google = JsonFormatter.Default.Format(message);

        AssertJsonEquivalent(google, ours);

        using var document = JsonDocument.Parse(ours);
        Assert.Equal(JsonValueKind.Object, document.RootElement.GetProperty("fStruct").ValueKind);
        Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("fList").ValueKind);
    }

    [Fact]
    public void A_deeply_nested_list_value_past_the_depth_cap_fails_clearly_on_write()
    {
        string json = "{\"fList\":" + BuildDeeplyNestedListJson(150) + "}";

        var ex = Assert.Throws<GrpcJsonException>(() => ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, json));

        // Pinned to the depth-cap message specifically (not just "any GrpcJsonException"), so this
        // test cannot pass for the wrong reason (e.g. a "not an array" error) — it must actually
        // exercise ListValue's own recursion hitting Encode's MaxDepth check.
        Assert.Contains("depth", ex.Message);
    }

    [Fact]
    public void Empty_field_renders_as_an_empty_object_identically_to_JsonFormatter_and_encodes_successfully()
    {
        var message = new AllTypes { FEmpty = new WktEmpty() };

        string ours = ProtoJsonReader.ToJson(AllTypesDescriptor, message.ToByteArray(), indented: false);
        string google = JsonFormatter.Default.Format(message);
        AssertJsonEquivalent(google, ours);

        byte[] bytes = ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, """{"fEmpty":{}}""");
        var parsed = AllTypes.Parser.ParseFrom(bytes);
        Assert.NotNull(parsed.FEmpty);
    }

    [Fact]
    public void A_struct_given_a_non_object_names_the_field()
    {
        var ex = Assert.Throws<GrpcJsonException>(() =>
            ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, """{"fStruct":"nope"}"""));

        Assert.Contains("fStruct", ex.Message);
    }

    [Fact]
    public void A_list_value_given_a_non_array_names_the_field()
    {
        var ex = Assert.Throws<GrpcJsonException>(() =>
            ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, """{"fList":"nope"}"""));

        Assert.Contains("fList", ex.Message);
    }

    private static string BuildDeeplyNestedListJson(int depth)
    {
        string inner = "[]";
        for (int i = 0; i < depth; i++) inner = $"[{inner}]";
        return inner;
    }

    // ---- Well-known types: FieldMask (slice F) ----
    // Any is still out of scope (slice G lands it); until then it keeps rendering as an ordinary
    // nested message.

    [Fact]
    public void Reader_renders_a_field_mask_identically_to_JsonFormatter()
    {
        var message = new AllTypes
        {
            FMask = new WktFieldMask { Paths = { "user.display_name", "photo" } }
        };

        string ours = ProtoJsonReader.ToJson(AllTypesDescriptor, message.ToByteArray(), indented: false);
        string google = JsonFormatter.Default.Format(message);

        AssertJsonEquivalent(google, ours);
    }

    [Fact]
    public void Writer_encodes_a_field_mask_to_bytes_that_parse_identically_to_JsonParser()
    {
        var message = new AllTypes
        {
            FMask = new WktFieldMask { Paths = { "user.display_name", "photo" } }
        };
        string json = JsonFormatter.Default.Format(message);

        byte[] ourBytes = ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, json);
        var ourParsed = AllTypes.Parser.ParseFrom(ourBytes);
        var googleParsed = JsonParser.Default.Parse<AllTypes>(json);

        Assert.Equal(googleParsed, ourParsed);
    }

    [Fact]
    public void Wire_paths_render_as_the_exact_comma_joined_lowerCamelCase_string_and_encode_back_to_the_same_wire_paths()
    {
        var message = new AllTypes
        {
            FMask = new WktFieldMask { Paths = { "user.display_name", "photo" } }
        };

        string ours = ProtoJsonReader.ToJson(AllTypesDescriptor, message.ToByteArray(), indented: false);
        using var document = JsonDocument.Parse(ours);
        Assert.Equal("user.displayName,photo", document.RootElement.GetProperty("fMask").GetString());

        byte[] bytes = ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, """{"fMask":"user.displayName,photo"}""");
        var parsed = AllTypes.Parser.ParseFrom(bytes);
        Assert.Equal(new[] { "user.display_name", "photo" }, parsed.FMask.Paths);
    }

    // These four inputs are the empirical edge-behavior findings for this slice, each pinned against
    // Google's own JsonParser<AllTypes> rather than a hand-written expectation:
    //  - "user.displayName,photo" — the ordinary case; converts to two wire paths.
    //  - ""                       — the empty mask; Google produces ZERO paths, not one empty path
    //                               (JsonParser.MergeFieldMask splits with StringSplitOptions.RemoveEmptyEntries).
    //  - "user.display_name"      — already snake_case; Google's ToSnakeCase REJECTS any literal '_' in
    //                               the JSON-form path and throws InvalidProtocolBufferException.
    //  - "userID"                 — consecutive capitals collapse to a SINGLE leading underscore
    //                               ("user_id"), not one underscore per capital ("user_i_d").
    [Theory]
    [InlineData("""{"fMask":"user.displayName,photo"}""")]
    [InlineData("""{"fMask":""}""")]
    [InlineData("""{"fMask":"userID"}""")]
    public void Field_mask_json_input_encodes_to_the_same_wire_paths_JsonParser_produces(string json)
    {
        var googleParsed = JsonParser.Default.Parse<AllTypes>(json);

        byte[] ourBytes = ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, json);
        var ourParsed = AllTypes.Parser.ParseFrom(ourBytes);

        Assert.Equal(googleParsed.FMask.Paths, ourParsed.FMask.Paths);
    }

    [Fact]
    public void A_field_mask_path_already_in_snake_case_is_rejected_identically_to_JsonParser()
    {
        const string json = """{"fMask":"user.display_name"}""";

        // Google's own parser throws for this input too — confirming ours must reject it rather than
        // silently accepting an underscore Google would not. The exception TYPE differs by design: ours
        // is this converter's own GrpcJsonException (so the CLI can map it to exit 3), not Google's
        // InvalidProtocolBufferException.
        Assert.Throws<Google.Protobuf.InvalidProtocolBufferException>(() => JsonParser.Default.Parse<AllTypes>(json));

        var ex = Assert.Throws<GrpcJsonException>(() => ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, json));
        Assert.Contains("fMask", ex.Message);
    }

    [Fact]
    public void A_field_mask_set_with_no_paths_renders_as_an_empty_string_identically_to_JsonFormatter()
    {
        var message = new AllTypes { FMask = new WktFieldMask() };

        string ours = ProtoJsonReader.ToJson(AllTypesDescriptor, message.ToByteArray(), indented: false);
        string google = JsonFormatter.Default.Format(message);

        AssertJsonEquivalent(google, ours);
        Assert.Equal("", JsonDocument.Parse(ours).RootElement.GetProperty("fMask").GetString());
    }

    [Fact]
    public void The_canonical_field_mask_string_round_trips_through_writer_then_reader_unchanged()
    {
        const string json = """{"fMask":"user.displayName,photo"}""";

        byte[] bytes = ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, json);
        string roundTripped = ProtoJsonReader.ToJson(AllTypesDescriptor, bytes, indented: false);

        AssertJsonEquivalent(json, roundTripped);
    }

    [Fact]
    public void A_number_where_a_field_mask_string_belongs_names_the_field()
    {
        var ex = Assert.Throws<GrpcJsonException>(() =>
            ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, """{"fMask":123}"""));

        Assert.Contains("fMask", ex.Message);
    }

    [Fact]
    public void A_field_mask_path_with_an_uppercase_letter_on_the_wire_falls_back_to_the_ordinary_object_rendering_instead_of_throwing()
    {
        // Hand-build a FieldMask submessage whose single path is "userID" — an uppercase letter makes
        // it unrepresentable as lowerCamelCase, the way a buggy (or non-conforming) server might send
        // it — the same fall-back-rather-than-fail rule the non-normalized-Timestamp test proves.
        byte[] fieldMaskBytes;
        using (var buffer = new MemoryStream())
        {
            using (var output = new CodedOutputStream(buffer, leaveOpen: true))
            {
                output.WriteTag(1, WireFormat.WireType.LengthDelimited); // FieldMask.paths
                output.WriteString("userID");
                output.Flush();
            }
            fieldMaskBytes = buffer.ToArray();
        }

        byte[] outerBytes;
        using (var buffer = new MemoryStream())
        {
            using (var output = new CodedOutputStream(buffer, leaveOpen: true))
            {
                output.WriteTag(42, WireFormat.WireType.LengthDelimited); // f_mask
                output.WriteBytes(ByteString.CopyFrom(fieldMaskBytes));
                output.Flush();
            }
            outerBytes = buffer.ToArray();
        }

        string json = ProtoJsonReader.ToJson(AllTypesDescriptor, outerBytes, indented: false);

        using var document = JsonDocument.Parse(json);
        var fMask = document.RootElement.GetProperty("fMask");
        Assert.Equal(JsonValueKind.Object, fMask.ValueKind);
        Assert.True(fMask.TryGetProperty("paths", out var pathsValue));
        Assert.Equal("userID", pathsValue.EnumerateArray().Single().GetString());
    }

    [Fact]
    public void A_field_mask_given_as_the_ordinary_paths_object_still_encodes_to_the_same_bytes_as_the_canonical_string_form()
    {
        byte[] fromObject = ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, """{"fMask":{"paths":["user.display_name"]}}""");
        byte[] fromCanonicalString = ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, """{"fMask":"user.displayName"}""");

        Assert.Equal(fromCanonicalString, fromObject);
    }

    // ---- Well-known types: Any (slice G) ----
    // JsonFormatter.Default/JsonParser.Default have an EMPTY TypeRegistry and throw on an Any, so the
    // differential tests below build their own registry rather than using the .Default instances.
    private static readonly TypeRegistry AnyTypeRegistry =
        TypeRegistry.FromMessages(Nested.Descriptor, WktTimestamp.Descriptor, WktEmpty.Descriptor);
    private static readonly JsonFormatter AnyFormatter =
        new(JsonFormatter.Settings.Default.WithTypeRegistry(AnyTypeRegistry));
    private static readonly JsonParser AnyParser =
        new(JsonParser.Settings.Default.WithTypeRegistry(AnyTypeRegistry));

    [Fact]
    public void Reader_renders_an_any_wrapping_an_ordinary_message_with_fields_inline_identically_to_JsonFormatter()
    {
        var message = new AllTypes
        {
            FAny = WktAny.Pack(new Nested { Label = "packed", Depth = 7 })
        };

        string ours = ProtoJsonReader.ToJson(AllTypesDescriptor, message.ToByteArray(), indented: false, ResolveRuntimeMessage);
        string google = AnyFormatter.Format(message);

        AssertJsonEquivalent(google, ours);
    }

    [Fact]
    public void Writer_encodes_an_any_wrapping_an_ordinary_message_to_bytes_that_parse_identically_to_JsonParser()
    {
        var message = new AllTypes
        {
            FAny = WktAny.Pack(new Nested { Label = "packed", Depth = 7 })
        };
        string json = AnyFormatter.Format(message);

        byte[] ourBytes = ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, json, ResolveRuntimeMessage);
        var ourParsed = AllTypes.Parser.ParseFrom(ourBytes);
        var googleParsed = AnyParser.Parse<AllTypes>(json);

        Assert.Equal(googleParsed, ourParsed);
    }

    [Fact]
    public void An_any_wrapping_a_timestamp_nests_the_payload_under_value_and_matches_JsonFormatter()
    {
        var message = new AllTypes
        {
            FAny = WktAny.Pack(new WktTimestamp { Seconds = 1700000000, Nanos = 500000000 })
        };

        string ours = ProtoJsonReader.ToJson(AllTypesDescriptor, message.ToByteArray(), indented: false, ResolveRuntimeMessage);
        string google = AnyFormatter.Format(message);
        AssertJsonEquivalent(google, ours);

        using var document = JsonDocument.Parse(ours);
        var fAny = document.RootElement.GetProperty("fAny");
        Assert.Equal("type.googleapis.com/google.protobuf.Timestamp", fAny.GetProperty("@type").GetString());
        Assert.Equal(JsonValueKind.String, fAny.GetProperty("value").ValueKind);
        // Exactly "@type" and "value" — nothing from the payload inlined alongside them.
        Assert.Equal(2, fAny.EnumerateObject().Count());
    }

    [Fact]
    public void An_any_wrapping_empty_nests_the_payload_under_value_as_an_empty_object_and_matches_JsonFormatter()
    {
        var message = new AllTypes { FAny = WktAny.Pack(new WktEmpty()) };

        string ours = ProtoJsonReader.ToJson(AllTypesDescriptor, message.ToByteArray(), indented: false, ResolveRuntimeMessage);
        string google = AnyFormatter.Format(message);
        AssertJsonEquivalent(google, ours);

        using var document = JsonDocument.Parse(ours);
        var fAny = document.RootElement.GetProperty("fAny");
        Assert.Equal("type.googleapis.com/google.protobuf.Empty", fAny.GetProperty("@type").GetString());
        var value = fAny.GetProperty("value");
        Assert.Equal(JsonValueKind.Object, value.ValueKind);
        Assert.Empty(value.EnumerateObject());
    }

    [Fact]
    public void An_any_with_a_null_resolver_renders_the_type_and_base64_value_fallback_without_throwing()
    {
        var message = new AllTypes { FAny = WktAny.Pack(new Nested { Label = "n", Depth = 1 }) };

        // No resolver argument at all — the default.
        string ours = ProtoJsonReader.ToJson(AllTypesDescriptor, message.ToByteArray(), indented: false);

        using var document = JsonDocument.Parse(ours);
        var fAny = document.RootElement.GetProperty("fAny");
        Assert.Equal("type.googleapis.com/certapi.test.Nested", fAny.GetProperty("@type").GetString());
        Assert.Equal(JsonValueKind.String, fAny.GetProperty("value").ValueKind);

        byte[] payloadBytes = Convert.FromBase64String(fAny.GetProperty("value").GetString()!);
        Assert.Equal(new Nested { Label = "n", Depth = 1 }, Nested.Parser.ParseFrom(payloadBytes));
    }

    [Fact]
    public void An_any_whose_resolver_returns_null_for_the_type_name_renders_the_same_fallback_without_throwing()
    {
        var message = new AllTypes { FAny = WktAny.Pack(new Nested { Label = "n", Depth = 1 }) };

        string ours = ProtoJsonReader.ToJson(AllTypesDescriptor, message.ToByteArray(), indented: false, _ => null);

        using var document = JsonDocument.Parse(ours);
        var fAny = document.RootElement.GetProperty("fAny");
        Assert.Equal("type.googleapis.com/certapi.test.Nested", fAny.GetProperty("@type").GetString());
        Assert.Equal(JsonValueKind.String, fAny.GetProperty("value").ValueKind);
    }

    [Fact]
    public void A_message_with_a_bad_any_and_other_populated_fields_still_renders_the_other_fields_correctly()
    {
        // A well-formed type_url paired with a payload that cannot possibly decode (a single 0xFF byte
        // is a truncated varint) — the kind of corrupt-Any-alongside-good-data a buggy server might send.
        byte[] badAnyBytes;
        using (var buffer = new MemoryStream())
        {
            using (var output = new CodedOutputStream(buffer, leaveOpen: true))
            {
                output.WriteTag(1, WireFormat.WireType.LengthDelimited); // Any.type_url
                output.WriteString("type.googleapis.com/certapi.test.Nested");
                output.WriteTag(2, WireFormat.WireType.LengthDelimited); // Any.value
                output.WriteBytes(ByteString.CopyFrom(new byte[] { 0xFF }));
                output.Flush();
            }
            badAnyBytes = buffer.ToArray();
        }

        byte[] outerBytes;
        using (var buffer = new MemoryStream())
        {
            using (var output = new CodedOutputStream(buffer, leaveOpen: true))
            {
                output.WriteTag(3, WireFormat.WireType.Varint); // f_int32
                output.WriteInt32(42);
                output.WriteTag(44, WireFormat.WireType.LengthDelimited); // f_any
                output.WriteBytes(ByteString.CopyFrom(badAnyBytes));
                output.WriteTag(14, WireFormat.WireType.LengthDelimited); // f_string
                output.WriteString("still here");
                output.Flush();
            }
            outerBytes = buffer.ToArray();
        }

        string json = ProtoJsonReader.ToJson(AllTypesDescriptor, outerBytes, indented: false, ResolveRuntimeMessage);

        using var document = JsonDocument.Parse(json);
        Assert.Equal(42, document.RootElement.GetProperty("fInt32").GetInt32());
        Assert.Equal("still here", document.RootElement.GetProperty("fString").GetString());
        var fAny = document.RootElement.GetProperty("fAny");
        Assert.Equal("type.googleapis.com/certapi.test.Nested", fAny.GetProperty("@type").GetString());
        Assert.Equal(JsonValueKind.String, fAny.GetProperty("value").ValueKind); // degraded, not thrown
    }

    [Fact]
    public void Payload_bytes_that_do_not_decode_against_the_resolved_descriptor_degrade_to_the_fallback_rather_than_throwing()
    {
        byte[] anyBytes;
        using (var buffer = new MemoryStream())
        {
            using (var output = new CodedOutputStream(buffer, leaveOpen: true))
            {
                output.WriteTag(1, WireFormat.WireType.LengthDelimited); // Any.type_url
                output.WriteString("type.googleapis.com/google.protobuf.Timestamp");
                output.WriteTag(2, WireFormat.WireType.LengthDelimited); // Any.value
                output.WriteBytes(ByteString.CopyFrom(new byte[] { 0xFF })); // truncated varint
                output.Flush();
            }
            anyBytes = buffer.ToArray();
        }

        byte[] outerBytes;
        using (var buffer = new MemoryStream())
        {
            using (var output = new CodedOutputStream(buffer, leaveOpen: true))
            {
                output.WriteTag(44, WireFormat.WireType.LengthDelimited); // f_any
                output.WriteBytes(ByteString.CopyFrom(anyBytes));
                output.Flush();
            }
            outerBytes = buffer.ToArray();
        }

        string json = ProtoJsonReader.ToJson(AllTypesDescriptor, outerBytes, indented: false, ResolveRuntimeMessage);

        using var document = JsonDocument.Parse(json);
        var fAny = document.RootElement.GetProperty("fAny");
        Assert.Equal("type.googleapis.com/google.protobuf.Timestamp", fAny.GetProperty("@type").GetString());
        Assert.Equal(Convert.ToBase64String(new byte[] { 0xFF }), fAny.GetProperty("value").GetString());
    }

    [Theory]
    [InlineData("""{"fAny":{"@type":"type.googleapis.com/certapi.test.Nested","label":"n","depth":3}}""")]
    [InlineData("""{"fAny":{"@type":"type.googleapis.com/google.protobuf.Timestamp","value":"2023-11-14T22:13:20Z"}}""")]
    [InlineData("""{"fAny":{"@type":"type.googleapis.com/unknown.NotRegistered","value":"AQID"}}""")]
    public void An_any_json_round_trips_through_writer_then_reader_for_resolved_ordinary_resolved_well_known_and_unresolved_shapes(string json)
    {
        byte[] bytes = ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, json, ResolveRuntimeMessage);
        string roundTripped = ProtoJsonReader.ToJson(AllTypesDescriptor, bytes, indented: false, ResolveRuntimeMessage);

        AssertJsonEquivalent(json, roundTripped);
    }

    [Fact]
    public void A_type_url_with_no_slash_at_all_is_treated_as_the_whole_type_name_and_does_not_crash()
    {
        byte[] anyBytes;
        using (var buffer = new MemoryStream())
        {
            using (var output = new CodedOutputStream(buffer, leaveOpen: true))
            {
                output.WriteTag(1, WireFormat.WireType.LengthDelimited); // Any.type_url
                output.WriteString("certapi.test.Nested"); // no '/' at all
                output.Flush();
            }
            anyBytes = buffer.ToArray();
        }

        byte[] outerBytes;
        using (var buffer = new MemoryStream())
        {
            using (var output = new CodedOutputStream(buffer, leaveOpen: true))
            {
                output.WriteTag(44, WireFormat.WireType.LengthDelimited); // f_any
                output.WriteBytes(ByteString.CopyFrom(anyBytes));
                output.Flush();
            }
            outerBytes = buffer.ToArray();
        }

        string json = ProtoJsonReader.ToJson(AllTypesDescriptor, outerBytes, indented: false, ResolveRuntimeMessage);

        using var document = JsonDocument.Parse(json);
        var fAny = document.RootElement.GetProperty("fAny");
        Assert.Equal("certapi.test.Nested", fAny.GetProperty("@type").GetString());
        // Empty payload bytes resolve and decode to a zero-length Nested — inlined with nothing else
        // to show, so "@type" is the only property.
        Assert.Single(fAny.EnumerateObject());
    }

    [Fact]
    public void An_any_without_at_type_still_encodes_through_the_ordinary_typeUrl_value_path_for_backward_compatibility()
    {
        byte[] raw = { 1, 2, 3 };
        string base64 = Convert.ToBase64String(raw);
        string json = $$$"""{"fAny":{"typeUrl":"type.googleapis.com/certapi.test.Nested","value":"{{{base64}}}"}}""";

        byte[] bytes = ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, json, ResolveRuntimeMessage);
        var parsed = AllTypes.Parser.ParseFrom(bytes);

        Assert.Equal("type.googleapis.com/certapi.test.Nested", parsed.FAny.TypeUrl);
        Assert.Equal(ByteString.CopyFrom(raw), parsed.FAny.Value);
    }

    [Fact]
    public void An_at_type_that_is_not_a_string_names_the_field()
    {
        var ex = Assert.Throws<GrpcJsonException>(() =>
            ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, """{"fAny":{"@type":123}}""", ResolveRuntimeMessage));

        Assert.Contains("fAny", ex.Message);
    }

    [Fact]
    public void An_unresolved_any_with_invalid_base64_in_value_names_the_field()
    {
        var ex = Assert.Throws<GrpcJsonException>(() =>
            ProtoJsonWriter.ToProtobuf(AllTypesDescriptor,
                """{"fAny":{"@type":"type.googleapis.com/unknown.NotRegistered","value":"!!!not-base64!!!"}}""",
                ResolveRuntimeMessage));

        Assert.Contains("fAny", ex.Message);
    }

    // ---- Targeted tests for rules a single big round trip can hide ----

    [Fact]
    public void Json_to_protobuf_to_json_round_trip_preserves_scalar_nested_repeated_and_enum_fields()
    {
        const string json = """
            {"fInt32":5,"fString":"hi","fEnum":"COLOR_RED","fNested":{"label":"n","depth":2},"rString":["a","b"]}
            """;

        byte[] bytes = ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, json);
        string roundTripped = ProtoJsonReader.ToJson(AllTypesDescriptor, bytes, indented: false);

        AssertJsonEquivalent(json, roundTripped);
    }

    [Fact]
    public void Int64_family_fields_render_as_json_strings_and_accept_both_number_and_string_input()
    {
        byte[] bytesFromNumber = ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, """{"fInt64":123456789012345}""");
        byte[] bytesFromString = ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, """{"fInt64":"123456789012345"}""");

        string outputFromNumber = ProtoJsonReader.ToJson(AllTypesDescriptor, bytesFromNumber, indented: false);
        string outputFromString = ProtoJsonReader.ToJson(AllTypesDescriptor, bytesFromString, indented: false);

        using var document = JsonDocument.Parse(outputFromNumber);
        var fInt64 = document.RootElement.GetProperty("fInt64");
        Assert.Equal(JsonValueKind.String, fInt64.ValueKind);
        Assert.Equal("123456789012345", fInt64.GetString());
        AssertJsonEquivalent(outputFromNumber, outputFromString);
    }

    [Fact]
    public void Bytes_field_round_trips_through_base64_and_accepts_url_safe_unpadded_input()
    {
        byte[] raw = { 0xDE, 0xAD, 0xBE, 0xEF, 0x01 };
        string standardBase64 = Convert.ToBase64String(raw);
        Assert.EndsWith("=", standardBase64); // proves this sample actually needs padding
        string urlSafeUnpadded = standardBase64.TrimEnd('=').Replace('+', '-').Replace('/', '_');

        byte[] bytesFromStandard = ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, $$"""{"fBytes":"{{standardBase64}}"}""");
        byte[] bytesFromUrlSafe = ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, $$"""{"fBytes":"{{urlSafeUnpadded}}"}""");

        string outputStandard = ProtoJsonReader.ToJson(AllTypesDescriptor, bytesFromStandard, indented: false);
        string outputUrlSafe = ProtoJsonReader.ToJson(AllTypesDescriptor, bytesFromUrlSafe, indented: false);

        using var document = JsonDocument.Parse(outputStandard);
        Assert.Equal(standardBase64, document.RootElement.GetProperty("fBytes").GetString());
        AssertJsonEquivalent(outputStandard, outputUrlSafe);
    }

    [Fact]
    public void Enum_field_renders_bare_number_for_an_unknown_wire_value_and_name_for_a_known_one()
    {
        byte[] knownBytes = ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, """{"fEnum":"COLOR_RED"}""");
        string knownJson = ProtoJsonReader.ToJson(AllTypesDescriptor, knownBytes, indented: false);
        Assert.Equal("COLOR_RED", JsonDocument.Parse(knownJson).RootElement.GetProperty("fEnum").GetString());

        byte[] unknownBytes = ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, """{"fEnum":99}""");
        string unknownJson = ProtoJsonReader.ToJson(AllTypesDescriptor, unknownBytes, indented: false);
        var element = JsonDocument.Parse(unknownJson).RootElement.GetProperty("fEnum");
        Assert.Equal(JsonValueKind.Number, element.ValueKind);
        Assert.Equal(99, element.GetInt32());
    }

    [Fact]
    public void An_unknown_enum_name_in_input_json_names_the_field_and_value()
    {
        var ex = Assert.Throws<GrpcJsonException>(() =>
            ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, """{"fEnum":"NOPE"}"""));

        Assert.Contains("fEnum", ex.Message);
        Assert.Contains("NOPE", ex.Message);
    }

    [Fact]
    public void NaN_and_infinity_values_survive_both_directions_for_double_and_float_fields()
    {
        byte[] bytes = ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, """{"fDouble":"NaN","fFloat":"Infinity"}""");
        string output = ProtoJsonReader.ToJson(AllTypesDescriptor, bytes, indented: false);

        using var document = JsonDocument.Parse(output);
        Assert.Equal("NaN", document.RootElement.GetProperty("fDouble").GetString());
        Assert.Equal("Infinity", document.RootElement.GetProperty("fFloat").GetString());

        byte[] negBytes = ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, """{"fFloat":"-Infinity"}""");
        string negOutput = ProtoJsonReader.ToJson(AllTypesDescriptor, negBytes, indented: false);
        Assert.Equal("-Infinity", JsonDocument.Parse(negOutput).RootElement.GetProperty("fFloat").GetString());
    }

    [Fact]
    public void A_packed_and_an_unpacked_encoding_of_the_same_repeated_field_decode_identically()
    {
        byte[] packedBytes = ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, """{"rInt32":[1,2,3]}""");

        byte[] unpackedBytes;
        using (var buffer = new MemoryStream())
        {
            using (var output = new CodedOutputStream(buffer, leaveOpen: true))
            {
                foreach (int v in new[] { 1, 2, 3 })
                {
                    output.WriteTag(19, WireFormat.WireType.Varint); // r_int32 = field 19
                    output.WriteInt32(v);
                }
                output.Flush();
            }
            unpackedBytes = buffer.ToArray();
        }

        string packedJson = ProtoJsonReader.ToJson(AllTypesDescriptor, packedBytes, indented: false);
        string unpackedJson = ProtoJsonReader.ToJson(AllTypesDescriptor, unpackedBytes, indented: false);

        AssertJsonEquivalent(packedJson, unpackedJson);
        Assert.Equal(new[] { 1, 2, 3 },
            JsonDocument.Parse(packedJson).RootElement.GetProperty("rInt32").EnumerateArray().Select(e => e.GetInt32()));
    }

    [Fact]
    public void String_keyed_message_valued_and_integer_keyed_maps_round_trip()
    {
        const string json = """
            {"mString":{"k1":"v1","k2":"v2"},"mNested":{"a":{"label":"na"},"b":{"label":"nb","depth":3}},"mIntKey":{"7":"seven","-3":"neg"}}
            """;

        byte[] bytes = ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, json);
        string output = ProtoJsonReader.ToJson(AllTypesDescriptor, bytes, indented: false);

        AssertJsonEquivalent(json, output);
    }

    [Fact]
    public void An_empty_map_is_omitted_from_the_output()
    {
        byte[] bytes = ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, """{"mString":{}}""");
        string output = ProtoJsonReader.ToJson(AllTypesDescriptor, bytes, indented: false);

        using var document = JsonDocument.Parse(output);
        Assert.False(document.RootElement.TryGetProperty("mString", out _));
    }

    [Fact]
    public void Only_the_set_oneof_member_appears_in_the_output()
    {
        byte[] bytes = ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, """{"choiceString":"picked"}""");
        string output = ProtoJsonReader.ToJson(AllTypesDescriptor, bytes, indented: false);

        using var document = JsonDocument.Parse(output);
        Assert.Equal("picked", document.RootElement.GetProperty("choiceString").GetString());
        Assert.False(document.RootElement.TryGetProperty("choiceInt", out _));
    }

    [Fact]
    public void Setting_two_members_of_the_same_oneof_in_input_json_names_both_fields()
    {
        var ex = Assert.Throws<GrpcJsonException>(() =>
            ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, """{"choiceString":"a","choiceInt":1}"""));

        Assert.Contains("choiceString", ex.Message);
        Assert.Contains("choiceInt", ex.Message);
    }

    [Fact]
    public void The_last_oneof_member_on_the_wire_is_the_one_that_is_emitted()
    {
        byte[] stringThenInt;
        using (var buffer = new MemoryStream())
        {
            using (var output = new CodedOutputStream(buffer, leaveOpen: true))
            {
                output.WriteTag(26, WireFormat.WireType.LengthDelimited); // choice_string
                output.WriteString("first");
                output.WriteTag(27, WireFormat.WireType.Varint); // choice_int
                output.WriteInt32(5);
                output.Flush();
            }
            stringThenInt = buffer.ToArray();
        }

        string json = ProtoJsonReader.ToJson(AllTypesDescriptor, stringThenInt, indented: false);

        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.TryGetProperty("choiceString", out _));
        Assert.Equal(5, document.RootElement.GetProperty("choiceInt").GetInt32());
    }

    [Fact]
    public void Both_snake_case_and_camel_case_input_are_accepted_and_output_uses_camel_case()
    {
        byte[] fromSnake = ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, """{"snake_case_name":"v"}""");
        byte[] fromCamel = ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, """{"snakeCaseName":"v"}""");

        string outputFromSnake = ProtoJsonReader.ToJson(AllTypesDescriptor, fromSnake, indented: false);
        string outputFromCamel = ProtoJsonReader.ToJson(AllTypesDescriptor, fromCamel, indented: false);

        AssertJsonEquivalent(outputFromSnake, outputFromCamel);
        using var document = JsonDocument.Parse(outputFromSnake);
        Assert.True(document.RootElement.TryGetProperty("snakeCaseName", out var value));
        Assert.Equal("v", value.GetString());
    }

    [Fact]
    public void An_unknown_field_number_on_the_wire_is_skipped_leaving_known_fields_intact()
    {
        byte[] bytes;
        using (var buffer = new MemoryStream())
        {
            using (var output = new CodedOutputStream(buffer, leaveOpen: true))
            {
                output.WriteTag(3, WireFormat.WireType.Varint); // f_int32
                output.WriteInt32(5);
                output.WriteTag(999, WireFormat.WireType.Varint); // not declared on AllTypes
                output.WriteInt32(123);
                output.Flush();
            }
            bytes = buffer.ToArray();
        }

        string json = ProtoJsonReader.ToJson(AllTypesDescriptor, bytes, indented: false);

        using var document = JsonDocument.Parse(json);
        Assert.Equal(5, document.RootElement.GetProperty("fInt32").GetInt32());
    }

    [Fact]
    public void An_unknown_top_level_json_property_names_it()
    {
        var ex = Assert.Throws<GrpcJsonException>(() =>
            ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, """{"bogus":1}"""));

        Assert.Contains("bogus", ex.Message);
    }

    [Fact]
    public void An_unknown_nested_json_property_names_it_with_its_full_path()
    {
        var ex = Assert.Throws<GrpcJsonException>(() =>
            ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, """{"fNested":{"bogus":1}}"""));

        Assert.Contains("fNested.bogus", ex.Message);
    }

    [Fact]
    public void A_string_where_a_boolean_belongs_names_the_field()
    {
        var ex = Assert.Throws<GrpcJsonException>(() =>
            ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, """{"fBool":"nope"}"""));

        Assert.Contains("fBool", ex.Message);
    }

    [Fact]
    public void An_object_where_an_array_belongs_names_the_field()
    {
        var ex = Assert.Throws<GrpcJsonException>(() =>
            ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, """{"rString":{}}"""));

        Assert.Contains("rString", ex.Message);
    }

    [Fact]
    public void Empty_or_whitespace_input_json_encodes_as_an_empty_message()
    {
        Assert.Empty(ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, ""));
        Assert.Empty(ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, "   "));
    }

    [Fact]
    public void A_nested_message_chain_past_the_depth_cap_fails_clearly_on_write()
    {
        string json = "{\"fNested\":" + BuildDeeplyNestedJson(150) + "}";

        Assert.Throws<GrpcJsonException>(() => ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, json));
    }

    [Fact]
    public void A_nested_message_chain_past_the_depth_cap_fails_clearly_on_read()
    {
        byte[] bytes = Array.Empty<byte>();
        for (int i = 0; i < 150; i++)
        {
            using var buffer = new MemoryStream();
            using (var output = new CodedOutputStream(buffer, leaveOpen: true))
            {
                output.WriteTag(3, WireFormat.WireType.LengthDelimited); // Nested.child
                output.WriteBytes(ByteString.CopyFrom(bytes));
                output.Flush();
            }
            bytes = buffer.ToArray();
        }

        Assert.Throws<GrpcJsonException>(() => ProtoJsonReader.ToJson(NestedDescriptor, bytes, indented: false));
    }

    [Fact]
    public void A_deeply_chained_any_wrapping_any_degrades_to_the_fallback_instead_of_recursing_without_bound()
    {
        const string anyTypeUrl = "type.googleapis.com/google.protobuf.Any";

        // Each iteration wraps the previous payload in one more resolvable google.protobuf.Any, so
        // every level actually resolves and the reader must recurse to expand it. The innermost
        // (built first) wraps an empty payload — an honestly empty Any.
        byte[] bytes = Array.Empty<byte>();
        for (int i = 0; i < 150; i++)
        {
            using var buffer = new MemoryStream();
            using (var output = new CodedOutputStream(buffer, leaveOpen: true))
            {
                output.WriteTag(1, WireFormat.WireType.LengthDelimited); // Any.type_url
                output.WriteString(anyTypeUrl);
                output.WriteTag(2, WireFormat.WireType.LengthDelimited); // Any.value
                output.WriteBytes(ByteString.CopyFrom(bytes));
                output.Flush();
            }
            bytes = buffer.ToArray();
        }

        string json = ProtoJsonReader.ToJson(AnyDescriptor, bytes, indented: false, ResolveRuntimeMessage);

        // JsonDocument's own default read-side MaxDepth (64) is unrelated to this converter's depth
        // cap: a correctly bounded render still nests deeper than 64 real "@type"/"value" levels
        // before it degrades to the fallback, so parsing needs its own generously raised MaxDepth —
        // mirroring why ProtoJsonWriter.ParseOptions raises the same limit on the write side.
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 1000 });
        Assert.Equal(anyTypeUrl, document.RootElement.GetProperty("@type").GetString());

        // Walk the "value" chain one level at a time. A pathological chain this deep must degrade
        // to the base64 "value" fallback well before reaching the literal bottom of the 150-deep
        // chain built above — if it never does, the render path expanded every level unbounded,
        // which is the defect this test guards against.
        JsonElement current = document.RootElement;
        bool foundFallback = false;
        for (int i = 0; i < 150; i++)
        {
            var value = current.GetProperty("value");
            if (value.ValueKind == JsonValueKind.String)
            {
                foundFallback = true;
                break;
            }
            Assert.Equal(JsonValueKind.Object, value.ValueKind);
            current = value;
        }

        Assert.True(foundFallback,
            "expected the Any-of-Any chain to degrade to the base64 fallback before reaching the bottom of the chain");
    }

    private static string BuildDeeplyNestedJson(int depth)
    {
        string inner = "{}";
        for (int i = 0; i < depth; i++) inner = $$"""{"child":{{inner}}}""";
        return inner;
    }
}
