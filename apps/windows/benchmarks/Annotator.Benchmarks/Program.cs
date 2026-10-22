using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Annotator.Platform.Graphics;
using Microsoft.Win32;

namespace Annotator.Benchmarks;

/// <summary>Command line options (Mac Configuration, plus --only, --list and --timeout).</summary>
internal sealed record Configuration(int Samples, int Warmup, string Output, string? Worker, string[] Only, bool List, bool Help, TimeSpan WorkerTimeout)
{
    public const string Usage = """
        Usage: Annotator.Benchmarks [--samples 3..101] [--warmup 1..20] [--output PATH] [--only NAME[,NAME...]]
                                    [--timeout SECONDS] [--worker SCENARIO] [--list]

        Runs isolated Release workloads: the Mac suite's 34 (tools/macos/benchmark.sh) and the overlay presenter's frame work.
        Defaults: 11 samples, 2 warmups, build/performance/windows.json, every scenario.
        --only runs the scenarios whose names start with any of the given prefixes (for example presenter.stress).
        --worker writes one result to stdout; --output applies only to the suite. --timeout bounds each worker (600 s).
        Run from the repository root. See apps/windows/benchmarks/Annotator.Benchmarks/README.md.
        """;

    public static Configuration Parse(string[] args)
    {
        int samples = 11, warmup = 2;
        var output = Path.Combine("build", "performance", "windows.json");
        string? worker = null;
        string[] only = [];
        var list = false;
        var help = false;
        var timeout = TimeSpan.FromSeconds(600);
        for (var index = 0; index < args.Length; index++)
        {
            var flag = args[index];
            if (flag is "--help" or "-h")
            {
                help = true;
                continue;
            }
            if (flag == "--list")
            {
                list = true;
                continue;
            }
            BenchmarkFailure.Require(index + 1 < args.Length, $"Missing value for {flag}.");
            var value = args[++index];
            switch (flag)
            {
                case "--samples":
                    BenchmarkFailure.Require(int.TryParse(value, out samples) && samples is >= 3 and <= 101, "--samples must be in 3...101.");
                    break;
                case "--warmup":
                    BenchmarkFailure.Require(int.TryParse(value, out warmup) && warmup is >= 1 and <= 20, "--warmup must be in 1...20.");
                    break;
                case "--output":
                    output = value;
                    break;
                case "--worker":
                    BenchmarkFailure.Require(Workloads.IsKnown(value), $"Unknown scenario {value}.");
                    worker = value;
                    break;
                case "--only":
                    only = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    break;
                case "--timeout":
                    BenchmarkFailure.Require(int.TryParse(value, out var seconds) && seconds is >= 10 and <= 7_200, "--timeout must be in 10...7200 seconds.");
                    timeout = TimeSpan.FromSeconds(seconds);
                    break;
                default:
                    throw new BenchmarkFailure($"Unknown option {flag}.");
            }
        }
        return new Configuration(samples, warmup, output, worker, only, list, help, timeout);
    }

    public IEnumerable<string> Scenarios => Only.Length == 0
        ? Workloads.All
        : Workloads.All.Where(name => Only.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)));
}

internal static partial class Program
{
    public static int Main(string[] args)
    {
        try
        {
            var config = Configuration.Parse(args);
            if (config.Help)
            {
                Console.Out.WriteLine(Configuration.Usage);
                return 0;
            }
            if (config.List)
            {
                foreach (var name in config.Scenarios)
                {
                    Console.Out.WriteLine(name);
                }
                return 0;
            }
            if (config.Worker is { } scenario)
            {
                RunWorker(scenario, config);
                return 0;
            }
            RunSuite(config);
            return 0;
        }
        catch (BenchmarkFailure failure)
        {
            Console.Error.WriteLine($"Benchmark failed: {failure.Message}");
            return 1;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Benchmark failed: {error}");
            return 1;
        }
    }

    private static string BuildKind =>
#if DEBUG
        "Debug, " + (RuntimeFeature.IsDynamicCodeSupported ? "JIT" : "Native AOT");
#else
        "Release, " + (RuntimeFeature.IsDynamicCodeSupported ? "JIT" : "Native AOT");
#endif

    private static void RunWorker(string scenario, Configuration config)
    {
        var result = Workloads.Run(scenario, config.Samples, config.Warmup);
        using var stdout = Console.OpenStandardOutput();
        using var writer = new Utf8JsonWriter(stdout);
        result.Write(writer);
    }

    private static void RunSuite(Configuration config)
    {
#if DEBUG
        throw new BenchmarkFailure("Benchmarks require a Release build (dotnet publish -c Release, see the README).");
#else
        var root = RepositoryRoot();
        var scenarios = config.Scenarios.ToArray();
        BenchmarkFailure.Require(scenarios.Length > 0, $"No scenario starts with {string.Join(", ", config.Only)}.");
        var executable = System.Environment.ProcessPath ?? throw new BenchmarkFailure("The benchmark executable's path is unknown.");
        var started = DateTimeOffset.UtcNow;
        var fingerprint = SourceFingerprint(root);
        var environment = EnvironmentRecord(root, executable, fingerprint);
        var results = new List<(string Scenario, JsonDocument Document)>();
        try
        {
            foreach (var scenario in scenarios)
            {
                var document = RunScenario(executable, root, scenario, config);
                results.Add((scenario, document));
                var result = document.RootElement;
                var peak = result.GetProperty("samples").EnumerateArray().Max(sample => sample.GetProperty("after").GetProperty("processPeakRSSBytes").GetUInt64());
                Console.Error.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"{scenario,-44} p50 {result.GetProperty("p50MS").GetDouble(),9:F3} ms  p95 {result.GetProperty("p95MS").GetDouble(),9:F3} ms  peak RSS {peak / 1_048_576.0,7:F1} MiB"));
            }
            BenchmarkFailure.Require(SourceFingerprint(root) == fingerprint, "Measured sources changed during the run; discard this baseline.");
            var output = Path.GetFullPath(Path.IsPathRooted(config.Output) ? config.Output : Path.Combine(root, config.Output));
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var temporary = output + ".partial";
            using (var stream = File.Create(temporary))
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("completedAt", DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture));
                writer.WriteStartObject("environment");
                foreach (var (key, value) in environment)
                {
                    writer.WriteString(key, value);
                }
                writer.WriteEndObject();
                writer.WriteStartObject("measurementContract");
                foreach (var (key, value) in Contract(config))
                {
                    writer.WriteString(key, value);
                }
                writer.WriteEndObject();
                writer.WriteStartArray("results");
                foreach (var (_, document) in results)
                {
                    document.RootElement.WriteTo(writer);
                }
                writer.WriteEndArray();
                writer.WriteNumber("schemaVersion", 1);
                writer.WriteString("startedAt", started.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture));
                writer.WriteStartArray("unmeasured");
                foreach (var line in Unmeasured)
                {
                    writer.WriteStringValue(line);
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            File.Move(temporary, output, overwrite: true);
            Console.Error.WriteLine($"Saved {output}");
        }
        finally
        {
            foreach (var (_, document) in results)
            {
                document.Dispose();
            }
        }
#endif
    }


    /// <summary>Runs one scenario in a fresh worker process within the worker timeout and checks its result contract.</summary>
    private static JsonDocument RunScenario(string executable, string root, string scenario, Configuration config)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { "--worker", scenario, "--samples", $"{config.Samples}", "--warmup", $"{config.Warmup}" })
        {
            start.ArgumentList.Add(argument);
        }
        using var worker = Process.Start(start) ?? throw new BenchmarkFailure($"Could not start the worker for {scenario}.");
        var output = worker.StandardOutput.ReadToEndAsync();
        if (!worker.WaitForExit(config.WorkerTimeout))
        {
            worker.Kill(entireProcessTree: true);
            worker.WaitForExit(TimeSpan.FromSeconds(10));
            throw new BenchmarkFailure($"The {scenario} worker did not finish within {config.WorkerTimeout.TotalSeconds:F0} s and was stopped.");
        }
        worker.WaitForExit();
        BenchmarkFailure.Require(output.Wait(TimeSpan.FromSeconds(30)), $"The {scenario} worker's output did not close.");
        BenchmarkFailure.Require(worker.ExitCode == 0, $"{executable} --worker {scenario} exited {worker.ExitCode}.");
        var document = JsonDocument.Parse(output.Result);
        var result = document.RootElement;
        var expected = Workloads.SampleCount(scenario, config.Samples);
        BenchmarkFailure.Require(result.GetProperty("scenario").GetString() == scenario && result.GetProperty("samples").GetArrayLength() == expected,
                                 "Worker returned an invalid result contract.");
        foreach (var sample in result.GetProperty("samples").EnumerateArray())
        {
            var duration = sample.GetProperty("durationMS").GetDouble();
            var after = sample.GetProperty("after");
            var current = after.GetProperty("currentRSSBytes").GetUInt64();
            BenchmarkFailure.Require(double.IsFinite(duration) && duration > 0 && current > 0 && after.GetProperty("processPeakRSSBytes").GetUInt64() >= current,
                                     "Worker returned invalid duration/RSS values.");
        }
        return document;
    }

    private static readonly string[] Unmeasured =
    [
        "Composition and present-to-display timing of the running app: PresentMon or Windows Performance Recorder traces.",
        "Input-to-visible time in the running app: Annotator.RenderHarness input (Windows.Graphics.Capture); physical input-to-photon needs instrumented input and a high-speed camera.",
        "FPS, live capture, receiver protection, hooks, timers, battery and power: dedicated native and physical acceptance.",
        "Spec 003 <=8 ms app-frame / <=33 ms input-visible gates: presenter.* report the overlay thread's frame work only; renderer.* are offscreen draws, not app frames.",
    ];

    private static IEnumerable<(string, string)> Contract(Configuration config) =>
    [
        ("clock", "Stopwatch (QueryPerformanceCounter, monotonic); milliseconds."),
        ("cpuTime", "GetProcessTimes user + kernel time in seconds; Windows charges it per scheduler tick, so short intervals read in steps of about 15.6 ms."),
        ("gpu", "renderer.* durations run until a Direct3D event query after EndDraw signals (GPU completion); cpuSubmitMicroseconds is the CPU part. presenter.* durations are the overlay thread's CPU submit (app frame work); gpuCompletionMicroseconds runs until the GPU finished."),
        ("idle", "Harness-only process CPU and working set after explicit dispose, garbage collection and a 250 ms sleep."),
        ("percentile", "Nearest rank: sorted[ceil(p*n)-1]; p95 with 11 samples is the maximum."),
        ("rssCurrent", "GetProcessMemoryInfo WorkingSetSize, bytes."),
        ("rssLimits", "Before/after working set is not operation-local peak or allocated bytes; retention is not leak proof."),
        ("rssPeak", "GetProcessMemoryInfo PeakWorkingSetSize, bytes; cumulative worker high-water including fixtures/warmup."),
        ("scope", "Synthetic app-owned fixtures: Annotator.Core model and files, Direct2D on the hardware device (renderer.*), WARP (export.*, as the app exports), the overlay presenter on composition virtual surfaces without a window (presenter.*); no desktop or app interaction."),
        ("warmup", $"{config.Warmup} discarded samples, {config.Samples} measured per scenario; presenter.* measure more frames (see each result's workload and warmupCount)."),
    ];

    /// <summary>The repository root: the current directory or a parent that holds apps/windows/Annotator.Windows.slnx.</summary>
    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(System.Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "apps", "windows", "Annotator.Windows.slnx")))
            {
                return directory.FullName;
            }
        }
        throw new BenchmarkFailure("Run from the annotation repository root: apps/windows/Annotator.Windows.slnx was not found.");
    }

    /// <summary>SHA-256 over the measured sources (path, a zero byte, then the bytes, in ordinal path order).</summary>
    private static string SourceFingerprint(string root)
    {
        var files = new List<string> { "apps/windows/Directory.Build.props", "tools/windows/validate.ps1" };
        string[] folders =
        [
            "apps/windows/src/Annotator.Core", "apps/windows/src/Annotator.Platform", "apps/windows/src/Annotator.Rendering",
            "apps/windows/src/Annotator.Workspace", "apps/windows/benchmarks/Annotator.Benchmarks",
        ];
        foreach (var folder in folders)
        {
            var full = Path.Combine(root, folder);
            BenchmarkFailure.Require(Directory.Exists(full), $"Run from the annotation repository root: missing {folder}.");
            foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                var parts = relative.Split('/');
                if (parts.Contains("bin") || parts.Contains("obj"))
                {
                    continue;
                }
                if (Path.GetExtension(file) is ".cs" or ".hlsl" or ".csproj" or ".targets" or ".txt" or ".json")
                {
                    files.Add(relative);
                }
            }
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files.Order(StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(file + "\0"));
            hash.AppendData(File.ReadAllBytes(Path.Combine(root, file)));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static SortedDictionary<string, string> EnvironmentRecord(string root, string executable, string fingerprint)
    {
        var processor = Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null) as string;
        var megahertz = Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "~MHz", null);
        var manufacturer = Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\BIOS", "SystemManufacturer", null) as string;
        var product = Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\BIOS", "SystemProductName", null) as string;
        const string CurrentVersion = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        var build = Registry.GetValue(CurrentVersion, "CurrentBuild", null) as string ?? "unknown";
        var revision = Registry.GetValue(CurrentVersion, "UBR", null);
        var displayVersion = Registry.GetValue(CurrentVersion, "DisplayVersion", null) as string ?? "unknown";
        var edition = Registry.GetValue(CurrentVersion, "EditionID", null) as string ?? "unknown";
        var windows = int.TryParse(build, out var number) && number >= 22_000 ? "Windows 11" : "Windows 10";
        string adapter, warp;
        using (var devices = GraphicsDevices.Create())
        {
            adapter = $"{devices.Adapter.Description}, feature level {devices.FeatureLevel}";
        }
        using (var devices = GraphicsDevices.Create(new GraphicsDeviceOptions { UseWarp = true }))
        {
            warp = devices.Adapter.Description;
        }
        var appVersion = AppVersion(root);
        var revisionText = Git(root, "rev-parse", "HEAD");
        return new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["appBuild"] = $"Annotator for Windows {appVersion} at {revisionText[..Math.Min(12, revisionText.Length)]} (development build, unpackaged; Annotator.App.csproj Version {appVersion})",
            ["architecture"] = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            ["build"] = BuildKind,
            ["cpu"] = processor?.Trim() ?? "unknown",
            ["cpuNominalMHz"] = $"{megahertz}",
            ["executable"] = executable,
            ["exportDevice"] = warp,
            ["fixtureSeed"] = $"{Fixtures.Seed}",
            ["gitDirty"] = Git(root, "status", "--porcelain").Length == 0 ? "false" : "true",
            ["gitRevision"] = revisionText,
            ["hardwareModel"] = $"{manufacturer?.Trim()} {product?.Trim()}".Trim(),
            ["logicalProcessors"] = $"{System.Environment.ProcessorCount}",
            ["machineIsolation"] = "Other processes, scheduling and thermal conditions uncontrolled.",
            ["measuredSourcesSHA256"] = fingerprint,
            ["os"] = $"{windows} {edition} {displayVersion} (build {build}.{revision})",
            ["osBuild"] = $"{build}.{revision}",
            ["physicalMemoryBytes"] = $"{PhysicalMemory()}",
            ["presenterDisplay"] = $"{PresenterWorkloads.PixelWidth} x {PresenterWorkloads.PixelHeight} pixels at {PresenterWorkloads.Scale * 100:F0}%, composition virtual surfaces, no window",
            ["processIsolation"] = "One new worker per scenario; setup and warmup share that worker.",
            ["rendererDevice"] = adapter,
            ["toolchain"] = $"{RuntimeInformation.FrameworkDescription}, {RuntimeInformation.RuntimeIdentifier}",
        };
    }

    /// <summary>The app project's Version (the shared version once the Store package sets it).</summary>
    private static string AppVersion(string root)
    {
        var project = File.ReadAllText(Path.Combine(root, "apps", "windows", "src", "Annotator.App", "Annotator.App.csproj"));
        var start = project.IndexOf("<Version>", StringComparison.Ordinal);
        var end = project.IndexOf("</Version>", StringComparison.Ordinal);
        return start >= 0 && end > start ? project[(start + "<Version>".Length)..end].Trim() : "unknown";
    }

    /// <summary>One git command in the repository, bounded to 30 seconds.</summary>
    private static string Git(string root, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, UseShellExecute = false };
        start.ArgumentList.Add("--no-pager");
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var git = Process.Start(start) ?? throw new BenchmarkFailure("git could not start.");
        var output = git.StandardOutput.ReadToEndAsync();
        if (!git.WaitForExit(TimeSpan.FromSeconds(30)))
        {
            git.Kill();
            throw new BenchmarkFailure($"git {string.Join(' ', arguments)} did not finish within 30 s.");
        }
        git.WaitForExit();
        BenchmarkFailure.Require(git.ExitCode == 0 && output.Wait(TimeSpan.FromSeconds(5)), $"git {string.Join(' ', arguments)} exited {git.ExitCode}.");
        return output.Result.Trim();
    }

    private static ulong PhysicalMemory()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status) ? status.TotalPhysical : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
