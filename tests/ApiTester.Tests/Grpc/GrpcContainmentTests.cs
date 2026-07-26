using System.Reflection;
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
        AssertNoGrpcReference(typeof(TransportOptions).Assembly, "ApiTester.Core");
    }

    [Fact]
    public void App_assembly_references_no_grpc_or_protobuf_assembly()
    {
        // MainWindow, deliberately: this test previously reached for RequestModel, which moved to
        // ApiTester.Core long ago, so both tests inspected Core and the application's half of the
        // promise was never actually checked. The assembly-name assertion below is what stops that
        // recurring — a type that migrates between projects now fails loudly instead of silently
        // re-testing the wrong assembly.
        AssertNoGrpcReference(typeof(MainWindow).Assembly, "ApiTester.App");
    }

    private static void AssertNoGrpcReference(Assembly assembly, string expectedName)
    {
        Assert.Equal(expectedName, assembly.GetName().Name);

        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), a => a.Name is not null &&
            (a.Name.StartsWith("Grpc.", StringComparison.Ordinal) || a.Name == "Google.Protobuf"));
    }
}
