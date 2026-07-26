using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApiTester.Grpc;
using Certapi.Test;
using Google.Protobuf;
using Google.Protobuf.Reflection;

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
    // AllTypes.Descriptor, which is backed by CLR code these tests must not lean on.
    private static readonly IReadOnlyList<FileDescriptor> RuntimeFiles =
        FileDescriptor.BuildFromByteStrings(new[] { AllTypes.Descriptor.File.ToProto().ToByteString() });

    private static readonly MessageDescriptor AllTypesDescriptor =
        RuntimeFiles[0].MessageTypes.First(m => m.Name == "AllTypes");

    private static readonly MessageDescriptor NestedDescriptor =
        RuntimeFiles[0].MessageTypes.First(m => m.Name == "Nested");

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

        string ours = ProtoJsonReader.ToJson(AllTypesDescriptor, message.ToByteArray(), indented: false);
        string google = JsonFormatter.Default.Format(message);

        AssertJsonEquivalent(google, ours);
    }

    [Fact]
    public void Writer_output_matches_JsonParser_for_a_message_with_every_field_populated()
    {
        var message = BuildFullyPopulatedAllTypes();
        string json = JsonFormatter.Default.Format(message);

        byte[] ourBytes = ProtoJsonWriter.ToProtobuf(AllTypesDescriptor, json);
        var ourParsed = AllTypes.Parser.ParseFrom(ourBytes);
        var googleParsed = JsonParser.Default.Parse<AllTypes>(json);

        Assert.Equal(googleParsed, ourParsed);
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

    private static string BuildDeeplyNestedJson(int depth)
    {
        string inner = "{}";
        for (int i = 0; i < depth; i++) inner = $$"""{"child":{{inner}}}""";
        return inner;
    }
}
