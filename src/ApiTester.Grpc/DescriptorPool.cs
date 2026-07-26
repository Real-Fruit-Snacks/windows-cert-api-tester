using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace ApiTester.Grpc;

/// <summary>Turns server-reflection bytes into real <see cref="Google.Protobuf.Reflection"/>
/// descriptors, once per <see cref="GrpcCaller"/> instance. <see cref="Google.Protobuf.Reflection.FileDescriptorProto"/>
/// is public in Google.Protobuf 3.35.1 (confirmed against the pinned package), so this pool parses
/// reflection bytes with it directly rather than needing the raw-<see cref="CodedInputStream"/>
/// fallback the brief allowed for.</summary>
internal sealed class DescriptorPool(ReflectionClient reflection)
{
    // Far more than any real dependency graph should ever need. A server that keeps introducing
    // filenames past this bound is either malicious or broken, and failing loudly beats hanging.
    private const int MaxFilesFetched = 100;

    private readonly Dictionary<string, FileDescriptorProto> _protos = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ServiceDescriptor> _services = new(StringComparer.Ordinal);

    // An Any's type URL names a message, not a service, and expanding one needs a by-name lookup over
    // everything reflection has fetched — which is a strictly larger set than any single file's
    // dependency closure (a service's own input/output types and their transitive dependencies),
    // because the payload type an Any carries is not necessarily imported by the message that holds
    // it. This is exactly what makes expansion work for a payload type the response message does not
    // itself import: the server may have advertised that type's file for an entirely unrelated
    // service, and this index is shared across every service this pool has ever resolved.
    private readonly Dictionary<string, MessageDescriptor> _messages = new(StringComparer.Ordinal);

    /// <summary>The descriptor for a fully-qualified service name, built from the file the server
    /// reports plus the transitive closure of its dependencies. Cached: a `grpc call` should cost
    /// one reflection round trip, not one per lookup.</summary>
    internal async Task<ServiceDescriptor> FindServiceAsync(string serviceFullName, CancellationToken ct)
    {
        if (_services.TryGetValue(serviceFullName, out var cached)) return cached;

        var roots = await reflection.FileContainingSymbolAsync(serviceFullName, ct);
        foreach (var bytes in roots) AddProto(FileDescriptorProto.Parser.ParseFrom(bytes));

        await ResolveDependenciesAsync(ct);

        Rebuild();

        if (_services.TryGetValue(serviceFullName, out var found)) return found;
        throw new GrpcMethodNotFoundException(
            $"Service '{serviceFullName}' was not found among the descriptors the server returned for it.");
    }

    /// <summary>Resolves a message by its full name across every file server reflection has fetched so
    /// far — the production resolver behind google.protobuf.Any expansion. Returns null when the name
    /// is not among the descriptors fetched so far, which is the caller's cue to fall back to the
    /// visible base64 rendering rather than fail the call (see ProtoJsonReader/Writer's Any
    /// handling).</summary>
    internal MessageDescriptor? FindMessage(string fullName) =>
        _messages.TryGetValue(fullName, out var found) ? found : null;

    private void AddProto(FileDescriptorProto proto) => _protos.TryAdd(proto.Name, proto);

    /// <summary>Do not assume the server returned the transitive closure of dependencies — server
    /// reflection implementations differ on this. Fetch every dependency filename not yet collected,
    /// and repeat until nothing is missing. Filenames already collected can never be requested again,
    /// which is what keeps a cyclic dependency graph from looping here; the file-count cap below is
    /// the backstop for a server that keeps naming files we have never seen before.</summary>
    private async Task ResolveDependenciesAsync(CancellationToken ct)
    {
        while (true)
        {
            var missing = _protos.Values
                .SelectMany(p => p.Dependency)
                .Where(dep => !_protos.ContainsKey(dep))
                .Distinct()
                .ToList();
            if (missing.Count == 0) break;

            foreach (var filename in missing)
            {
                if (_protos.Count >= MaxFilesFetched)
                {
                    // GrpcMethodNotFoundException fits best of the five declared exception types:
                    // functionally, this means the service being looked up could not be resolved via
                    // reflection because its dependency graph never stopped growing.
                    throw new GrpcMethodNotFoundException(
                        $"Server reflection dependency resolution exceeded {MaxFilesFetched} files while " +
                        $"fetching '{filename}'; the server may be reporting an unbounded dependency graph.");
                }
                var files = await reflection.FileByFilenameAsync(filename, ct);
                foreach (var bytes in files) AddProto(FileDescriptorProto.Parser.ParseFrom(bytes));
            }
        }
    }

    /// <summary>Rebuilds every file collected so far (previous calls' files included) into real
    /// descriptors and re-populates the service cache from all of them — not just the one that was
    /// asked for — so a second service living in an already-fetched file costs nothing.</summary>
    private void Rebuild()
    {
        var ordered = TopologicalSort(_protos);
        var built = FileDescriptor.BuildFromByteStrings(ordered.Select(p => p.ToByteString()).ToList());

        _services.Clear();
        _messages.Clear();
        foreach (var file in built)
        {
            foreach (var service in file.Services)
                _services[service.FullName] = service;
            foreach (var message in file.MessageTypes)
                AddMessageRecursive(message);
        }
    }

    /// <summary>Indexes a message and every message nested inside it, however deep. A nested message
    /// (e.g. certapi.test.Outer.Inner) is a perfectly legal Any payload, but it is not in its file's
    /// top-level MessageTypes list at all — only walking NestedTypes recursively finds it.</summary>
    private void AddMessageRecursive(MessageDescriptor message)
    {
        _messages[message.FullName] = message;
        foreach (var nested in message.NestedTypes) AddMessageRecursive(nested);
    }

    /// <summary><see cref="FileDescriptor.BuildFromByteStrings"/> requires each file's dependencies to
    /// precede it in the input; feeding it unsorted input fails in a confusing way. A cycle among the
    /// collected files (which a well-formed compiled `.proto` set could never contain, but a
    /// malicious/buggy server's raw bytes are not obligated to be well-formed) is reported here rather
    /// than looping.</summary>
    private static List<FileDescriptorProto> TopologicalSort(Dictionary<string, FileDescriptorProto> protos)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<FileDescriptorProto>();

        void Visit(string name)
        {
            if (visited.Contains(name)) return;
            if (!protos.TryGetValue(name, out var proto)) return;
            if (!visiting.Add(name))
            {
                throw new GrpcMethodNotFoundException(
                    $"Circular dependency detected among the file descriptors the server returned, involving '{name}'.");
            }
            foreach (var dependency in proto.Dependency) Visit(dependency);
            visiting.Remove(name);
            visited.Add(name);
            ordered.Add(proto);
        }

        foreach (var name in protos.Keys) Visit(name);
        return ordered;
    }
}
