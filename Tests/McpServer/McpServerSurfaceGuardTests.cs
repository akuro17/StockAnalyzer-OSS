using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using StockAnalyzer.McpServer;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// Run 9, the server-side half of the surface guard (the gateway's own surface is guarded in Core.Tests): the McpServer assembly itself
/// must not reference any API that writes to the disk, starts a process, opens a network connection, edits the registry, loads code or
/// calls native code. The scan reads the assembly's metadata (type and member references), so a later change that adds such a call fails here
/// even when no test exercises it. The scanner is proven against the test assembly, which does write files.
/// </summary>
public sealed class McpServerSurfaceGuardTests
{
    private static readonly string[] ForbiddenTypePrefixes =
    {
        "System.Net.Http.",
        "System.Net.Sockets.",
        "System.Net.WebClient",
        "System.Net.WebRequest",
        "System.Diagnostics.Process",
        "Microsoft.Win32.Registry",
    };

    private static readonly Dictionary<string, string[]> ForbiddenMembers = new(StringComparer.Ordinal)
    {
        ["System.IO.File"] = new[]
        {
            "WriteAllText", "WriteAllBytes", "WriteAllLines", "WriteAllTextAsync", "WriteAllBytesAsync", "WriteAllLinesAsync",
            "AppendAllText", "AppendAllLines", "AppendAllTextAsync", "AppendAllLinesAsync", "AppendText",
            "Create", "CreateText", "Delete", "Move", "Copy", "Replace", "Open", "OpenWrite", "SetAttributes", "SetLastWriteTime", "SetLastWriteTimeUtc",
            "CreateSymbolicLink", "Encrypt", "Decrypt",
        },
        ["System.IO.Directory"] = new[] { "CreateDirectory", "Delete", "Move", "SetCurrentDirectory", "CreateSymbolicLink" },
        ["System.IO.FileInfo"] = new[] { "Create", "CreateText", "Delete", "MoveTo", "CopyTo", "OpenWrite", "AppendText", "Replace" },
        ["System.IO.DirectoryInfo"] = new[] { "Create", "CreateSubdirectory", "Delete", "MoveTo" },
        ["System.Reflection.Assembly"] = new[] { "Load", "LoadFrom", "LoadFile", "UnsafeLoadFrom" },
        ["System.Environment"] = new[] { "SetEnvironmentVariable", "Exit", "FailFast" },
    };

    private static readonly string[] ForbiddenTypeExact =
    {
        "System.IO.FileStream",
        "System.IO.FileSystemWatcher",
        "System.Runtime.Loader.AssemblyLoadContext",
        "System.Activator",
    };

    /// <summary>Everything in the assembly's metadata that belongs to the forbidden surface, as readable lines.</summary>
    private static List<string> Offenders(Assembly assembly)
    {
        using FileStream stream = File.OpenRead(assembly.Location);
        using var pe = new PEReader(stream);
        MetadataReader metadata = pe.GetMetadataReader();
        var found = new List<string>();

        string TypeName(TypeReference reference)
        {
            string name = metadata.GetString(reference.Name);
            string ns = metadata.GetString(reference.Namespace);
            return ns.Length == 0 ? name : $"{ns}.{name}";
        }

        foreach (TypeReferenceHandle handle in metadata.TypeReferences)
        {
            string full = TypeName(metadata.GetTypeReference(handle));
            if (ForbiddenTypePrefixes.Any(prefix => full.StartsWith(prefix, StringComparison.Ordinal)) || ForbiddenTypeExact.Contains(full, StringComparer.Ordinal))
            {
                found.Add($"type {full}");
            }
        }

        foreach (MemberReferenceHandle handle in metadata.MemberReferences)
        {
            MemberReference member = metadata.GetMemberReference(handle);
            if (member.Parent.Kind != HandleKind.TypeReference)
            {
                continue;
            }

            string owner = TypeName(metadata.GetTypeReference((TypeReferenceHandle)member.Parent));
            string name = metadata.GetString(member.Name);
            if (ForbiddenMembers.TryGetValue(owner, out string[]? names) && names.Contains(name, StringComparer.Ordinal))
            {
                found.Add($"member {owner}.{name}");
            }
        }

        foreach (MethodDefinitionHandle handle in metadata.MethodDefinitions)
        {
            MethodDefinition method = metadata.GetMethodDefinition(handle);
            if (!method.GetImport().Module.IsNil)
            {
                found.Add($"native call {metadata.GetString(method.Name)}");
            }
        }

        return found;
    }

    [Fact]
    public void TheMcpServerAssemblyReferencesNoWritingProcessNetworkRegistryOrNativeApi()
    {
        List<string> offenders = Offenders(typeof(McpServerComposition).Assembly);

        Assert.True(offenders.Count == 0, "McpServer must stay read-only; found: " + string.Join("; ", offenders.Distinct()));
    }

    [Fact]
    public void TheScannerSeesTheWritesOfAnAssemblyThatHasThem()
    {
        List<string> offenders = Offenders(typeof(McpServerSurfaceGuardTests).Assembly);

        Assert.Contains(offenders, line => line.StartsWith("member System.IO.File.", StringComparison.Ordinal));
    }
}
