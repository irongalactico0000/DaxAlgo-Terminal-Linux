using FluentAssertions;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Infrastructure.Backtest.Fast;
using TradingTerminal.Infrastructure.Sidecar;
using Xunit;

namespace TradingTerminal.Tests.Infrastructure;

public sealed class RuntimeHelperResolverTests
{
    [Fact]
    public void Fast_backtester_resolves_native_helper_from_mac_bundle_resources()
    {
        using var tree = new TempTree();
        var baseDir = tree.Directory("DaxAlgo Terminal.app", "Contents", "MacOS");
        var helper = tree.File("DaxAlgo Terminal.app", "Contents", "Resources", "helpers", "tick_backtester");

        var resolved = FastBacktestServiceCollectionExtensions.ResolveBinary(baseDir, null, isWindows: false);

        resolved.Should().Be(helper);
    }

    [Fact]
    public void Fast_backtester_resolves_native_helper_from_user_application_data()
    {
        using var tree = new TempTree();
        var baseDir = tree.Directory("app", "MacOS");
        var userAppDir = tree.Directory("user", "DaxAlgoTerminal");
        var helper = tree.File("user", "DaxAlgoTerminal", "helpers", "tick_backtester.sh");

        var resolved = FastBacktestServiceCollectionExtensions.ResolveBinary(
            baseDir, userAppDir, isWindows: false);

        resolved.Should().Be(helper);
    }

    [Fact]
    public void Fast_backtester_ignores_windows_binary_on_mac()
    {
        using var tree = new TempTree();
        var baseDir = tree.Directory("app", "MacOS");
        tree.File("app", "MacOS", "tick_backtester.exe");

        var resolved = FastBacktestServiceCollectionExtensions.ResolveBinary(baseDir, null, isWindows: false);

        resolved.Should().BeNull();
    }

    [Fact]
    public void Fast_backtester_preserves_windows_development_lookup()
    {
        using var tree = new TempTree();
        var baseDir = tree.Directory("output");
        var helper = tree.File("output", "tools", "cpp-backtester", "bin", "tick_backtester.exe");

        var resolved = FastBacktestServiceCollectionExtensions.ResolveBinary(baseDir, null, isWindows: true);

        resolved.Should().Be(helper);
    }

    [Fact]
    public void Sidecar_resolves_native_helper_from_mac_bundle_resources()
    {
        using var tree = new TempTree();
        var baseDir = tree.Directory("DaxAlgo Terminal.app", "Contents", "MacOS");
        var helper = tree.File("DaxAlgo Terminal.app", "Contents", "Resources", "sidecar", "daxalgo-ml");

        var launch = SidecarHostService.ResolveLaunchForPlatform(
            new SidecarOptions(), 8765, baseDir, null, isWindows: false);

        launch.Should().NotBeNull();
        launch!.Value.FileName.Should().Be(helper);
        launch.Value.Args.Should().Equal("--port", "8765");
        launch.Value.WorkDir.Should().Be(Path.GetDirectoryName(helper));
    }

    [Fact]
    public void Sidecar_runs_user_installed_shell_launcher_through_bin_sh()
    {
        using var tree = new TempTree();
        var baseDir = tree.Directory("app", "MacOS");
        var userAppDir = tree.Directory("user", "DaxAlgoTerminal");
        var script = tree.File("user", "DaxAlgoTerminal", "sidecar", "daxalgo-ml.sh");

        var launch = SidecarHostService.ResolveLaunchForPlatform(
            new SidecarOptions(), 9123, baseDir, userAppDir, isWindows: false);

        launch.Should().NotBeNull();
        launch!.Value.FileName.Should().Be("/bin/sh");
        launch.Value.Args.Should().Equal(script, "--port", "9123");
    }

    [Fact]
    public void Sidecar_uses_mac_venv_python_for_user_installed_module()
    {
        using var tree = new TempTree();
        var baseDir = tree.Directory("app", "MacOS");
        var userAppDir = tree.Directory("user", "DaxAlgoTerminal");
        tree.File("user", "DaxAlgoTerminal", "sidecar", "daxalgo_ml", "app.py");
        var python = tree.File("user", "DaxAlgoTerminal", "sidecar", ".venv", "bin", "python3");

        var launch = SidecarHostService.ResolveLaunchForPlatform(
            new SidecarOptions(), 8765, baseDir, userAppDir, isWindows: false);

        launch.Should().NotBeNull();
        launch!.Value.FileName.Should().Be(python);
        launch.Value.Args.Should().Equal("-m", "daxalgo_ml.app", "--port", "8765");
        launch.Value.WorkDir.Should().Be(Path.Combine(userAppDir, "sidecar"));
    }

    [Fact]
    public void Sidecar_does_not_walk_source_tree_on_mac()
    {
        using var tree = new TempTree();
        var baseDir = tree.Directory("repo", "artifacts", "app");
        tree.File("repo", "tools", "python-ml", "daxalgo_ml", "app.py");

        var launch = SidecarHostService.ResolveLaunchForPlatform(
            new SidecarOptions(), 8765, baseDir, null, isWindows: false);

        launch.Should().BeNull();
    }

    [Fact]
    public void Sidecar_preserves_windows_frozen_executable_lookup()
    {
        using var tree = new TempTree();
        var baseDir = tree.Directory("repo", "artifacts", "app");
        var executable = tree.File("repo", "tools", "python-ml", "dist", "daxalgo-ml.exe");

        var launch = SidecarHostService.ResolveLaunchForPlatform(
            new SidecarOptions(), 8765, baseDir, null, isWindows: true);

        launch.Should().NotBeNull();
        launch!.Value.FileName.Should().Be(executable);
        launch.Value.Args.Should().Equal("--port", "8765");
    }

    [Fact]
    public void Sidecar_preserves_windows_repository_venv_lookup()
    {
        using var tree = new TempTree();
        var baseDir = tree.Directory("repo", "artifacts", "app");
        tree.File("repo", "tools", "python-ml", "daxalgo_ml", "app.py");
        var python = tree.File("repo", "tools", "python-ml", ".venv", "Scripts", "python.exe");

        var launch = SidecarHostService.ResolveLaunchForPlatform(
            new SidecarOptions(), 8765, baseDir, null, isWindows: true);

        launch.Should().NotBeNull();
        launch!.Value.FileName.Should().Be(python);
        launch.Value.Args.Should().Equal("-m", "daxalgo_ml.app", "--port", "8765");
    }

    private sealed class TempTree : IDisposable
    {
        public TempTree()
        {
            Root = Path.Combine(Path.GetTempPath(), $"daxalgo-runtime-resolver-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(Root);
        }

        private string Root { get; }

        public string Directory(params string[] segments)
        {
            var path = Combine(segments);
            System.IO.Directory.CreateDirectory(path);
            return path;
        }

        public string File(params string[] segments)
        {
            var path = Combine(segments);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, string.Empty);
            return path;
        }

        private string Combine(IEnumerable<string> segments) =>
            segments.Aggregate(Root, static (current, segment) => Path.Combine(current, segment));

        public void Dispose()
        {
            var resolvedRoot = Path.GetFullPath(Root);
            var resolvedTemp = Path.GetFullPath(Path.GetTempPath());
            if (resolvedRoot.StartsWith(resolvedTemp, StringComparison.OrdinalIgnoreCase) &&
                System.IO.Directory.Exists(resolvedRoot))
                System.IO.Directory.Delete(resolvedRoot, recursive: true);
        }
    }
}
