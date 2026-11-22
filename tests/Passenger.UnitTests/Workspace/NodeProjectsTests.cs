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

    /// <summary>A folder's packages take their manager, scripts and workspace from their manifests, lockfiles and
    /// workspace files.</summary>
    [Fact]
    public void A_folders_packages_are_read_from_their_manifests_lockfiles_and_workspaces() => Cases.AllApart<NodeProjectsTests>(
        (nameof(A_manifest_reads_its_name_scripts_in_order_package_manager_engines_and_workspaces),
            test => test.A_manifest_reads_its_name_scripts_in_order_package_manager_engines_and_workspaces()),
        (nameof(A_folder_with_several_lockfiles_takes_pnpms_yarns_or_buns_before_npms),
            test => test.A_folder_with_several_lockfiles_takes_pnpms_yarns_or_buns_before_npms()),
        (nameof(Workspace_globs_take_in_a_package_by_its_folder_with_a_later_negation_leaving_it_out),
            _ => Workspace_globs_take_in_a_package_by_its_folder_with_a_later_negation_leaving_it_out()),
        (nameof(Npm_takes_a_double_dash_before_a_scripts_arguments_and_the_others_pass_them_on_themselves),
            test => test.Npm_takes_a_double_dash_before_a_scripts_arguments_and_the_others_pass_them_on_themselves()),
        (nameof(A_folders_packages_take_their_manager_from_the_nearest_field_or_lockfile_and_their_workspace_from_its_root),
            test => test.A_folders_packages_take_their_manager_from_the_nearest_field_or_lockfile_and_their_workspace_from_its_root()),
        (nameof(A_package_no_file_locks_runs_with_npm_and_a_lockfile_above_the_folder_open_but_outside_a_package_says_nothing),
            test => test.A_package_no_file_locks_runs_with_npm_and_a_lockfile_above_the_folder_open_but_outside_a_package_says_nothing()));

    /// <summary>Pre and post scripts are listed apart only where the package's manager, by its version and settings,
    /// runs none.</summary>
    [Fact]
    public void Pre_and_post_scripts_are_listed_as_each_package_manager_runs_them() => Cases.AllApart<NodeProjectsTests>(
        (nameof(Pre_and_post_scripts_are_listed_apart_only_where_the_packages_manager_runs_none_with_its_script),
            _ => Pre_and_post_scripts_are_listed_apart_only_where_the_packages_manager_runs_none_with_its_script()));

    private void A_manifest_reads_its_name_scripts_in_order_package_manager_engines_and_workspaces()
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

    private void A_folder_with_several_lockfiles_takes_pnpms_yarns_or_buns_before_npms()
    {
        Assert.Equal((NodeManager.Pnpm, "pnpm-lock.yaml"), NodeProjects.OfLockfiles(["package-lock.json", "pnpm-lock.yaml"]));
        Assert.Equal((NodeManager.Yarn, "yarn.lock"), NodeProjects.OfLockfiles(["yarn.lock", "package-lock.json"]));
        Assert.Equal((NodeManager.Bun, "bun.lockb"), NodeProjects.OfLockfiles(["bun.lockb"]));
        Assert.Null(NodeProjects.OfLockfiles(["package.json", "README.md"]));
    }

    private static void Workspace_globs_take_in_a_package_by_its_folder_with_a_later_negation_leaving_it_out() => Cases.EachApart<NodeProjectsTests, (string Folder, bool Taken)>(
    [
        ("packages/web", true),
        ("packages/web/inner", false),
        ("packages/legacy", false),
        ("tools", false),
    ], (test, row) => test.Workspace_globs_take_in_a_package_by_its_folder_with_a_later_negation_leaving_it_out(row.Folder, row.Taken));

    private void Workspace_globs_take_in_a_package_by_its_folder_with_a_later_negation_leaving_it_out(string folder, bool taken) =>
        Assert.Equal(taken, NodeProjects.Covers(["packages/*", "./apps/**", "!packages/legacy"], folder));

    private void Npm_takes_a_double_dash_before_a_scripts_arguments_and_the_others_pass_them_on_themselves()
    {
        Assert.Equal(["run", "dev", "--", "--port", "3000"], NodeProjects.RunArguments(NodeManager.Npm, "dev", ["--port", "3000"]));
        Assert.Equal(["run", "dev"], NodeProjects.RunArguments(NodeManager.Npm, "dev", []));
        Assert.Equal(["run", "dev", "--port", "3000"], NodeProjects.RunArguments(NodeManager.Pnpm, "dev", ["--port", "3000"]));
        Assert.Equal("yarn", NodeProjects.ProgramOf(NodeManager.Yarn));
    }

    private const string Hooked = """{ "name": "app", "scripts": { "prebuild": "gen", "build": "tsc", "postbuild": "copy" } }""";

    /// <summary>prebuild and postbuild are left out of a package's scripts only where its own manager runs them with build,
    /// as its version and its settings say: npm and Bun, Yarn 1 and pnpm with enablePrePostScripts on (its default from 9)
    /// run them; Yarn 2 and later never do, nor pnpm with the setting off (its default from 6 to 8), nor npm told to
    /// ignore scripts, and a script of theirs alone is a script of its own.</summary>
    private static void Pre_and_post_scripts_are_listed_apart_only_where_the_packages_manager_runs_none_with_its_script() => Cases.EachApart<NodeProjectsTests, (string Manager, string Files, bool Listed)>(
    [
        ("npm", "package-lock.json={}", false),
        ("npm, told to ignore scripts", "package-lock.json={}|.npmrc=ignore-scripts=true", true),
        ("Yarn 4 by its field", "yarn.lock=__metadata:\n  version: 8\n|field=yarn@4.5.1", true),
        ("Yarn 1", "yarn.lock=# THIS IS AN AUTOGENERATED FILE. DO NOT EDIT THIS FILE DIRECTLY.\n# yarn lockfile v1\n", false),
        ("pnpm 9 and later by default", "pnpm-lock.yaml=lockfileVersion: '9.0'\n", false),
        ("pnpm 11, which reads no such setting from .npmrc", "pnpm-lock.yaml=lockfileVersion: '9.0'\n|.npmrc=enable-pre-post-scripts=false\n|field=pnpm@11.2.0", false),
        ("pnpm 8 by default", "pnpm-lock.yaml=lockfileVersion: '6.0'\n", true),
    ], (test, row) => test.Pre_and_post_scripts_are_listed_apart_only_where_the_packages_manager_runs_none_with_its_script(row.Manager, row.Files, row.Listed));

    private void Pre_and_post_scripts_are_listed_apart_only_where_the_packages_manager_runs_none_with_its_script(string manager, string files, bool listed)
    {
        string field = string.Empty;
        foreach (string file in files.Split('|'))
        {
            (string name, string text) = (file[..file.IndexOf('=', StringComparison.Ordinal)], file[(file.IndexOf('=', StringComparison.Ordinal) + 1)..]);
            if (name == "field")
            {
                field = $", \"packageManager\": \"{text}\"";
            }
            else
            {
                Write(name, text);
            }
        }
        Write("package.json", Hooked.Replace("\"app\"", $"\"app\"{field}", StringComparison.Ordinal));
        NodePackage package = NodeProjects.Read(root, [Full("package.json")]).Single();
        string[] expected = listed ? ["prebuild", "build", "postbuild"] : ["build"];
        Assert.True(expected.SequenceEqual(package.Runnable.Select(s => s.Name)),
            $"With {manager}, the scripts listed were {string.Join(", ", package.Runnable.Select(s => s.Name))}, where {string.Join(", ", expected)} were to be");
    }

    private void A_folders_packages_take_their_manager_from_the_nearest_field_or_lockfile_and_their_workspace_from_its_root()
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

    private void A_package_no_file_locks_runs_with_npm_and_a_lockfile_above_the_folder_open_but_outside_a_package_says_nothing()
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
