using ApiTester.App;
using ApiTester.Core;

namespace ApiTester.Tests.Grpc;

/// <summary>Phase D2's containment promise, made falsifiable: Grpc.Net.Client / Grpc.Reflection /
/// Google.Protobuf live only in the ApiTester.Grpc project, referenced only by ApiTester.Cli. If
/// either ApiTester.Core or ApiTester.App ever picks up a reference to one of those assemblies —
/// directly or by some future refactor routing through them — the desktop application and every
/// existing command would silently start carrying the gRPC dependency too. This test fails loudly
/// the moment that happens.</summary>
public class GrpcContainmentTests
{
    [Fact]
    public void Core_assembly_references_no_grpc_or_protobuf_assembly()
    {
        var referenced = typeof(TransportOptions).Assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(referenced, a => a.Name is not null &&
            (a.Name.StartsWith("Grpc.", StringComparison.Ordinal) || a.Name == "Google.Protobuf"));
    }

    [Fact]
    public void App_assembly_references_no_grpc_or_protobuf_assembly()
    {
        var referenced = typeof(RequestModel).Assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(referenced, a => a.Name is not null &&
            (a.Name.StartsWith("Grpc.", StringComparison.Ordinal) || a.Name == "Google.Protobuf"));
    }
}
