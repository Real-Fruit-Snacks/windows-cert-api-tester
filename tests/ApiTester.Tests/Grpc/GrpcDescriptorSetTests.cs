using System.IO;
using ApiTester.Grpc;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace ApiTester.Tests.Grpc;

/// <summary>Pins the tracer-bullet claim of this release: a compiled `FileDescriptorSet` — the
/// binary output of `protoc --descriptor_set_out=... --include_imports ...`, the same file
/// grpcurl's -protoset consumes — is a complete substitute for server reflection for the purpose
/// of learning what a service offers. No <see cref="GrpcTestServer"/>, no address, no socket
/// appears anywhere in this file: that absence is the point of the slice, not an omission.</summary>
public class GrpcDescriptorSetTests
{
    [Fact]
    public void Load_lists_the_services_and_methods_the_descriptor_set_declares()
    {
        string path = DescriptorSetFixture.WriteTemp(DescriptorSetFixture.Complete());
        try
        {
            var set = GrpcDescriptorSet.Load(path);

            var echo = Assert.Single(set.Services, s => s.Name == "certapi.test.Echo");

            var unary = Assert.Single(echo.Methods, m => m.Name == "Unary");
            Assert.False(unary.ClientStreaming);
            Assert.False(unary.ServerStreaming);
            Assert.Equal("certapi.test.EchoRequest", unary.InputType);
            Assert.Equal("certapi.test.EchoReply", unary.OutputType);

            var serverStream = Assert.Single(echo.Methods, m => m.Name == "ServerStream");
            Assert.False(serverStream.ClientStreaming);
            Assert.True(serverStream.ServerStreaming);

            var clientStream = Assert.Single(echo.Methods, m => m.Name == "ClientStream");
            Assert.True(clientStream.ClientStreaming);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void A_missing_descriptor_set_file_names_the_path()
    {
        string path = Path.Combine(Path.GetTempPath(), $"certapi-protoset-missing-{Guid.NewGuid():N}.protoset");

        var ex = Assert.Throws<GrpcDescriptorSetException>(() => GrpcDescriptorSet.Load(path));

        Assert.Contains(path, ex.Message);
    }

    [Fact]
    public void Bytes_that_are_not_a_descriptor_set_say_so_and_name_protoc()
    {
        string path = DescriptorSetFixture.WriteTemp(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });
        try
        {
            var ex = Assert.Throws<GrpcDescriptorSetException>(() => GrpcDescriptorSet.Load(path));

            Assert.Contains("descriptor set", ex.Message);
            Assert.Contains("--descriptor_set_out", ex.Message);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void A_descriptor_set_that_declares_no_files_says_so()
    {
        string path = DescriptorSetFixture.WriteTemp(new FileDescriptorSet().ToByteArray());
        try
        {
            var ex = Assert.Throws<GrpcDescriptorSetException>(() => GrpcDescriptorSet.Load(path));

            Assert.Contains("no files", ex.Message);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void A_descriptor_set_missing_an_import_names_the_missing_file_and_include_imports()
    {
        string path = DescriptorSetFixture.WriteTemp(
            DescriptorSetFixture.MissingImport("google/protobuf/timestamp.proto"));
        try
        {
            var ex = Assert.Throws<GrpcDescriptorSetException>(() => GrpcDescriptorSet.Load(path));

            Assert.Contains("google/protobuf/timestamp.proto", ex.Message);
            Assert.Contains("--include_imports", ex.Message);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void A_proto_source_file_passed_instead_of_a_descriptor_set_says_exactly_that()
    {
        string path = DescriptorSetFixture.WriteTempText(DescriptorSetFixture.ProtoSource);
        try
        {
            var ex = Assert.Throws<GrpcDescriptorSetException>(() => GrpcDescriptorSet.Load(path));

            Assert.Contains("looks like a .proto source file", ex.Message);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Random_bytes_are_not_mistaken_for_a_proto_source_file()
    {
        string path = DescriptorSetFixture.WriteTemp(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });
        try
        {
            var ex = Assert.Throws<GrpcDescriptorSetException>(() => GrpcDescriptorSet.Load(path));

            Assert.DoesNotContain(".proto source file", ex.Message);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void A_complete_descriptor_set_still_loads_after_the_closure_check()
    {
        string path = DescriptorSetFixture.WriteTemp(DescriptorSetFixture.Complete());
        try
        {
            var set = GrpcDescriptorSet.Load(path);

            Assert.Single(set.Services, s => s.Name == "certapi.test.Echo");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
