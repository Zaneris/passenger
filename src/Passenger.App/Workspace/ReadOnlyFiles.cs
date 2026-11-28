using Passenger.Core;

namespace Passenger.App.Workspace;

/// <summary>The files read-only by where they lie, open or not, as their tabs show them (the half of
/// <see cref="OpenDocument.IsReadOnly"/> a path decides): decompiled, the licence, notices or release notes the app carries,
/// an MSBuild file of the .NET SDK or any file of a package as NuGet restored it, and the copies made to be read (a
/// container's file, a query, a source generator's code, a stylesheet a page links, a database object's definition). Every write of a file, open or not,
/// asks here first, so none reaches one, whatever writes: Replace in Files, a server's edit, an agent's tool. Safe from any
/// thread.</summary>
public static class ReadOnlyFiles
{
    /// <summary>Whether a file (a canonical path) is read-only by where it lies.</summary>
    public static bool Holds(string path) => OpenDocument.Decompiled(path) || Product.Carries(path) || Updates.ReleaseNotes.VersionOf(path) is not null
        || MSBuild.MSBuildFiles.IsInstalled(path) || Packages.PackageFolders.Holds(path) || Containers.ContainerFilesPane.IsCopy(path)
        || Endpoints.RequestLog.IsCommandCopy(path) || CSharp.GeneratedCode.IsCopy(path) || Web.RemoteStylesheets.IsCopy(path) || Database.DatabaseDdl.IsCopy(path);

    /// <summary>Why a file (a canonical path) read-only by where it lies is left as it was, said after its name where a list
    /// of such files names each ("Kit.cs (…)"): a package's naming the package; null for one that may be written.</summary>
    public static Words? Why(string path) => Packages.PackageFolders.PackageOf(path) is { } package ? Refusals.PackagesOwn(package)
        : Holds(path) ? FileSearch.ReadOnlyWhy : null;

    /// <summary>A change of a file (a canonical path) read-only by where it lies refused, the file called <paramref name="name"/>:
    /// a package's naming the package (<see cref="Refusals.OfPackage"/>); null for one that may be written.</summary>
    public static Words? Refused(string path, string name) => Packages.PackageFolders.PackageOf(path) is { } package ? Refusals.OfPackage(name, package)
        : Holds(path) ? Refusals.ReadOnly(name) : null;
}
