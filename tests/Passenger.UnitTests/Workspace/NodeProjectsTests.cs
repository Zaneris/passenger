using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>A folder's Node packages as their files say: the package manager from the nearest <c>packageManager</c> field or
/// lockfile up from each package (never npm where pnpm, Yarn or Bun locked it), the workspaces a root's field or pnpm's file
/// lists, each package with its own scripts in their order, and each manager's words for running one.</summary>
public sealed class NodeProjectsTests : IDisposable
{
    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void A_manifest_reads_its_name_scripts_in_order_package_manager_engines_and_workspaces()
    {
        NodeManifest read = NodeProjects.ReadManifest("""
            {
              // A comment some tools write.
              "name": "@acme/web",
              "packageManager": "pnpm@9.12.0+sha512.abc",
              "engines": { "node": ">=20" },
              "workspaces": ["packages/*", "apps/**"],
              "scripts": { "dev": "vite", "build": "vite build", "lint": 3, "test": "vitest", },
            }
            """)!;
        Assert.Equal("@acme/web", read.Name);
        Assert.Equal(["dev", "build", "test"], read.Scripts.Select(s => s.Name));
        Assert.Equal("vite build", read.Scripts[1].Command);
        Assert.Equal(NodeManager.Pnpm, NodeProjects.Named(read.PackageManager));
        Assert.Equal(">=20", read.Engines);
        Assert.Equal(["packages/*", "apps/**"], read.Workspaces);
    }

    [Fact]
    public void Yarns_object_of_workspaces_lists_its_packages_and_a_manifest_that_is_no_object_reads_as_none()
    {
        Assert.Equal(["packages/*"], NodeProjects.ReadManifest("""{ "workspaces": { "packages": ["packages/*"], "nohoist": ["**/x"] } }""")!.Workspaces);
        Assert.Null(NodeProjects.ReadManifest("[1, 2]"));
        Assert.Null(NodeProjects.ReadManifest("{ \"name\": "));
    }

    [Theory]
    [InlineData("pnpm@9.1.0", NodeManager.Pnpm)]
    [InlineData("yarn@4.5.1+sha224.abc", NodeManager.Yarn)]
    [InlineData("bun@1.1.30", NodeManager.Bun)]
    [InlineData("npm@10.8.2", NodeManager.Npm)]
    [InlineData("deno@2", null)]
    [InlineData("", null)]
    public void A_package_manager_field_names_its_manager_by_the_name_before_its_version(string field, NodeManager? manager) =>
        Assert.Equal(manager, NodeProjects.Named(field));

    [Fact]
    public void A_folder_with_several_lockfiles_takes_pnpms_yarns_or_buns_before_npms()
    {
        Assert.Equal((NodeManager.Pnpm, "pnpm-lock.yaml"), NodeProjects.OfLockfiles(["package-lock.json", "pnpm-lock.yaml"]));
        Assert.Equal((NodeManager.Yarn, "yarn.lock"), NodeProjects.OfLockfiles(["yarn.lock", "package-lock.json"]));
        Assert.Equal((NodeManager.Bun, "bun.lockb"), NodeProjects.OfLockfiles(["bun.lockb"]));
        Assert.Null(NodeProjects.OfLockfiles(["package.json", "README.md"]));
    }

    [Fact]
    public void Pnpms_workspace_file_lists_its_packages_as_a_block_or_a_flow_unquoted_without_comments()
    {
        Assert.Equal(["packages/*", "apps/**", "!**/test/**"], NodeProjects.PnpmWorkspaces("""
            # The workspace.
            packages:
              - 'packages/*'   # libraries
              - "apps/**"
              - '!**/test/**'
            catalog:
              - react
            """.Replace("\r\n", "\n", StringComparison.Ordinal)));
        Assert.Equal(["a/*", "b"], NodeProjects.PnpmWorkspaces("packages: ['a/*', \"b\"]\n"));
    }

    [Theory]
    [InlineData("packages/web", true)]
    [InlineData("packages/web/inner", false)]
    [InlineData("apps/site/admin", true)]
    [InlineData("packages/legacy", false)]
    [InlineData("tools", false)]
    public void Workspace_globs_take_in_a_package_by_its_folder_with_a_later_negation_leaving_it_out(string folder, bool taken) =>
        Assert.Equal(taken, NodeProjects.Covers(["packages/*", "./apps/**", "!packages/legacy"], folder));

    [Fact]
    public void Npm_takes_a_double_dash_before_a_scripts_arguments_and_the_others_pass_them_on_themselves()
    {
        Assert.Equal(["run", "dev", "--", "--port", "3000"], NodeProjects.RunArguments(NodeManager.Npm, "dev", ["--port", "3000"]));
        Assert.Equal(["run", "dev"], NodeProjects.RunArguments(NodeManager.Npm, "dev", []));
        Assert.Equal(["run", "dev", "--port", "3000"], NodeProjects.RunArguments(NodeManager.Pnpm, "dev", ["--port", "3000"]));
        Assert.Equal("yarn", NodeProjects.ProgramOf(NodeManager.Yarn));
    }

    [Fact]
    public void A_hook_run_around_another_script_is_no_script_of_its_own_but_one_named_alike_is()
    {
        NodePackage package = new("/p/package.json", "p", NodeManager.Npm, "", [new("prebuild", "x"), new("build", "y"), new("postbuild", "z"),
            new("prepare", "husky"), new("presentation", "w")], null, null);
        Assert.Equal(["build", "prepare", "presentation"], package.Runnable.Select(s => s.Name));
    }

    [Fact]
    public void A_folders_packages_take_their_manager_from_the_nearest_field_or_lockfile_and_their_workspace_from_its_root()
    {
        // A pnpm monorepo, its members locked at its root; one member names its own manager.
        Write("package.json", """{ "name": "mono", "scripts": { "build": "turbo build" } }""");
        Write("pnpm-workspace.yaml", "packages:\n  - 'packages/*'\n");
        Write("pnpm-lock.yaml", "lockfileVersion: '9.0'\n");
        Write("packages/web/package.json", """{ "name": "@mono/web", "scripts": { "dev": "vite" } }""");
        Write("packages/api/package.json", """{ "name": "@mono/api", "packageManager": "yarn@4.1.0" }""");
        // An ASP.NET Core app's front end beside it, locked by npm, and one with a lockfile the field overrides.
        Write("src/Shop/ClientApp/package.json", """{ "scripts": { "start": "ng serve" } }""");
        Write("src/Shop/ClientApp/package-lock.json", "{}");
        Write("src/Admin/ClientApp/package.json", """{ "name": "admin", "packageManager": "bun@1.1.0" }""");
        Write("src/Admin/ClientApp/package-lock.json", "{}");
        IReadOnlyList<NodePackage> packages = NodeProjects.Read(root,
            [.. new[] { "package.json", "packages/web/package.json", "packages/api/package.json", "src/Shop/ClientApp/package.json", "src/Admin/ClientApp/package.json" }
                .Select(Full)]);
        NodePackage Named(string name) => packages.Single(p => p.Name == name);
        Assert.Equal((NodeManager.Pnpm, "pnpm-lock.yaml", null), (Named("mono").Manager, Named("mono").ManagerSource, Named("mono").Root));
        Assert.Equal((NodeManager.Pnpm, Full("package.json")), (Named("@mono/web").Manager, Named("@mono/web").Root));
        Assert.Equal((NodeManager.Yarn, Full("package.json")), (Named("@mono/api").Manager, Named("@mono/api").Root));
        Assert.Equal((NodeManager.Npm, "package-lock.json", null), (Named("ClientApp").Manager, Named("ClientApp").ManagerSource, Named("ClientApp").Root));
        Assert.Equal(NodeManager.Bun, Named("admin").Manager);
        Assert.True(Named("mono").IsWorkspace(packages));
        Assert.False(Named("@mono/web").IsWorkspace(packages));
    }

    [Fact]
    public void A_package_no_file_locks_runs_with_npm_and_a_lockfile_above_the_folder_open_but_outside_a_package_says_nothing()
    {
        Write("package-lock.json", "{}");
        Write("site/web/package.json", """{ "name": "web" }""");
        IReadOnlyList<NodePackage> packages = NodeProjects.Read(Full("site"), [Full("site/web/package.json")]);
        Assert.Equal((NodeManager.Npm, "no lockfile names another"), (packages[0].Manager, packages[0].ManagerSource));
    }

    private string Full(string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    private void Write(string relative, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Full(relative))!);
        File.WriteAllText(Full(relative), text);
    }
}
