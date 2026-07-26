using System.IO;
using System.Text;
using Certapi.Test;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace ApiTester.Tests.Grpc;

/// <summary>Ruling 8 (a deterministic fixture, checked in or generated at build time, never
/// fetched): builds the exact bytes `protoc --descriptor_set_out=... --include_imports
/// certapi_test.proto` would produce, but does it in process from the generated
/// <see cref="CertapiTestReflection"/> descriptor this test project already compiles from
/// certapi_test.proto — no protoc invocation, no build-time plumbing, nothing downloaded. A
/// generated <see cref="FileDescriptor"/> already carries its own compiled bytes
/// (<c>SerializedData</c>) and its resolved dependency graph (<c>Dependencies</c>), which is
/// exactly what a FileDescriptorSet is a container of.</summary>
internal static class DescriptorSetFixture
{
    /// <summary>The complete dependency closure: certapi_test.proto plus every well-known-type
    /// .proto it imports, each dependency ordered before whatever imports it — matching what
    /// `--include_imports` guarantees. De-duplicated by filename, since several messages in
    /// certapi_test.proto import the same well-known types.</summary>
    internal static byte[] Complete()
    {
        var set = new FileDescriptorSet();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void AddWithDependencies(FileDescriptor file)
        {
            foreach (var dependency in file.Dependencies) AddWithDependencies(dependency);
            if (seen.Add(file.Name))
            {
                set.File.Add(FileDescriptorProto.Parser.ParseFrom(file.SerializedData));
            }
        }

        AddWithDependencies(CertapiTestReflection.Descriptor);
        return set.ToByteArray();
    }

    /// <summary>The complete set with one file removed — exactly the shape of a descriptor set
    /// produced by a `protoc` run that forgot `--include_imports`, where the root .proto is present
    /// and the files it imports are not.</summary>
    internal static byte[] MissingImport(string fileToOmit)
    {
        var set = FileDescriptorSet.Parser.ParseFrom(Complete());
        var kept = set.File.Where(f => f.Name != fileToOmit).ToList();
        if (kept.Count == set.File.Count)
        {
            throw new InvalidOperationException(
                $"Fixture error: the complete descriptor set contains no file named '{fileToOmit}'.");
        }
        set.File.Clear();
        set.File.AddRange(kept);
        return set.ToByteArray();
    }

    /// <summary>A minimal but genuine `.proto` source file, for the classic mistake of passing the
    /// source where the compiled descriptor set belongs.</summary>
    internal const string ProtoSource = """
        syntax = "proto3";
        package certapi.test.fixture;

        message FixtureMessage {
          string label = 1;
        }
        """;

    /// <summary>Writes descriptor-set bytes to a fresh temp file and returns its path. Tests delete
    /// it in a `finally`, mirroring the temp-file discipline in GrpcCliTests.cs.</summary>
    internal static string WriteTemp(byte[] bytes, string extension = ".protoset")
    {
        string path = Path.Combine(Path.GetTempPath(), $"certapi-protoset-{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Writes UTF-8 text (no BOM) to a fresh temp file, the same way <see cref="WriteTemp"/>
    /// writes binary bytes — for fixtures that are genuine text, such as <see cref="ProtoSource"/>.</summary>
    internal static string WriteTempText(string text, string extension = ".proto")
    {
        string path = Path.Combine(Path.GetTempPath(), $"certapi-protoset-{Guid.NewGuid():N}{extension}");
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }
}
