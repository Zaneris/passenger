using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using ICSharpCode.Decompiler.Metadata;

namespace Passenger.Debugger.Decompilation;
internal sealed class DebuggingAssemblyResolver(List<string> modulePaths) : IAssemblyResolver
{
    private readonly List<string> _modulePaths = modulePaths;
    private readonly record struct AssemblyIdentity(string Name, Version Version, ImmutableArray<byte> PublicKeyToken);

    public Task<MetadataFile?> ResolveAsync(IAssemblyReference name) => Task.FromResult(Resolve(name));
    public Task<MetadataFile?> ResolveModuleAsync(MetadataFile mainModule, string moduleName) => Task.FromResult(ResolveModule(mainModule, moduleName));

    // The module paths are the debuggee's own, a list already in memory: there is no file system reading to hold.
    public IDisposable? BeginSnapshot() => null;

    public MetadataFile? Resolve(IAssemblyReference name)
    {
        string? exactMatch = null;
        string? highestVersionMatch = null;
        Version? highestVersion = null;

        foreach (string path in _modulePaths)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            AssemblyIdentity? identity = TryReadAssemblyIdentity(path);
            if (identity is null)
            {
                continue;
            }

            if (!string.Equals(identity.Value.Name, name.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            byte[] requestedToken = name.PublicKeyToken ?? [];
            ImmutableArray<byte> identityToken = identity.Value.PublicKeyToken;

            if (identity.Value.Version == name.Version && identityToken.SequenceEqual(requestedToken))
            {
                exactMatch = path;
                break;
            }

            if (highestVersion is null || identity.Value.Version > highestVersion)
            {
                highestVersion = identity.Value.Version;
                highestVersionMatch = path;
            }
        }

        string? chosen = exactMatch ?? highestVersionMatch;
        if (chosen is null)
        {
            return null;
        }

        return new PEFile(chosen, PEStreamOptions.PrefetchMetadata);
    }

    public MetadataFile? ResolveModule(MetadataFile mainModule, string moduleName)
    {
        // Multi-module assemblies: look for the module file next to the main module.
        string? baseDirectory = Path.GetDirectoryName(mainModule.FileName);
        if (baseDirectory is null)
        {
            return null;
        }

        string moduleFileName = Path.Combine(baseDirectory, moduleName);
        if (!File.Exists(moduleFileName))
        {
            return null;
        }

        return new PEFile(moduleFileName, PEStreamOptions.PrefetchMetadata);
    }

    private static AssemblyIdentity? TryReadAssemblyIdentity(string path)
    {
        try
        {
            using PEReader peReader = new PEReader(File.OpenRead(path));
            if (peReader.HasMetadata is false)
            {
                return null;
            }

            MetadataReader metadataReader = peReader.GetMetadataReader();
            AssemblyDefinition assemblyDef = metadataReader.GetAssemblyDefinition();

            string name = metadataReader.GetString(assemblyDef.Name);
            Version version = assemblyDef.Version;
            ImmutableArray<byte> publicKeyToken = ComputePublicKeyToken(metadataReader, assemblyDef);

            return new AssemblyIdentity(name, version, publicKeyToken);
        }
        catch (Exception error) when (Failures.OfReading(error) || error is CryptographicException)
        {
            return null;
        }
    }

    private static ImmutableArray<byte> ComputePublicKeyToken(MetadataReader reader, AssemblyDefinition assemblyDef)
    {
        byte[] publicKeyOrToken = reader.GetBlobBytes(assemblyDef.PublicKey);
        if (publicKeyOrToken.Length == 0)
        {
            return ImmutableArray<byte>.Empty;
        }

        // If this is a full public key (not already a token), hash it down to an 8-byte token.
        if ((assemblyDef.Flags & System.Reflection.AssemblyFlags.PublicKey) != 0 &&
            publicKeyOrToken.Length > 8)
        {
            using SHA1 sha1 = System.Security.Cryptography.SHA1.Create();
            byte[] hash = sha1.ComputeHash(publicKeyOrToken);
            // Public key token = last 8 bytes of SHA-1 hash, reversed
            byte[] token = new byte[8];
            for (int i = 0; i < 8; i++)
            {
                token[i] = hash[hash.Length - 1 - i];
            }

            return [.. token];
        }

        return [.. publicKeyOrToken];
    }
}
