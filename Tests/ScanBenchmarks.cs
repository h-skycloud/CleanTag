using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using CleanTag.Core;
using Microsoft.Win32.SafeHandles;

internal static class ScanBenchmarks
{
    public static async Task RunAsync()
    {
        string parent = Path.Combine(AppContext.BaseDirectory, "benchmark-data");
        string fixture = Path.Combine(parent, "run-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(fixture, "files"); Directory.CreateDirectory(root);
        try
        {
            for (int directory = 0; directory < 40; directory++)
            {
                string child = Path.Combine(root, $"project-{directory:D2}"); Directory.CreateDirectory(child);
                for (int file = 0; file < 100; file++) File.WriteAllText(Path.Combine(child, $"file-{file:D3}.txt"), "benchmark fixture");
            }
            // Alternate warmed runs of the original scanner approach and the optimized scanner.
            var legacyTimes = new List<double>(); var fastTimes = new List<double>();
            List<FileEntry> before = []; ScanResult? after = null;
            for (int run = 0; run < 4; run++)
            {
                var watch = Stopwatch.StartNew(); before = await Task.Run(() => LegacyScan(root));
                if (run > 0) legacyTimes.Add(watch.Elapsed.TotalMilliseconds);
                watch.Restart(); after = await FileScanner.ScanAsync(root, null, default);
                if (run > 0) fastTimes.Add(watch.Elapsed.TotalMilliseconds);
            }
            if (!before.Select(f => f.Identity).Order().SequenceEqual(after!.Files.Select(f => f.Identity).Order())) throw new Exception("Benchmark inventories differ");
            var store = new CatalogStore(Path.Combine(fixture, "data", "catalog.sqlite")); await store.InitializeAsync();
            var timer = Stopwatch.StartNew(); await store.IndexScanAsync(root, after); double initialIndexMs = timer.Elapsed.TotalMilliseconds;
            timer.Restart(); await store.IndexScanAsync(root, await FileScanner.ScanAsync(root, null, default)); double unchangedRefreshMs = timer.Elapsed.TotalMilliseconds;
            timer.Restart(); await store.AddSourceAsync(root); await store.AddFolderAsync(root); double directFolderMs = timer.Elapsed.TotalMilliseconds;
            static double Median(List<double> times) => times.Order().ElementAt(times.Count / 2);
            var result = new { at = DateTime.UtcNow, files = 4000, folders = 40, runs = 3, warmCache = true,
                legacyScanMs = Median(legacyTimes), optimizedScanMs = Median(fastTimes), speedup = Median(legacyTimes) / Median(fastTimes),
                initialIndexMs, unchangedRefreshMs, directFolderMs, identicalInventory = true };
            string report = Path.GetFullPath(Path.Combine("work", "scan-benchmark.json")); Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(await File.ReadAllTextAsync(report)); Console.WriteLine(report);
        }
        finally { if (FileSafety.IsWithin(parent, fixture) && Path.GetFileName(fixture).StartsWith("run-")) Directory.Delete(fixture, true); }
    }

    private static List<FileEntry> LegacyScan(string root)
    {
        var files = new List<FileEntry>(); var stack = new Stack<string>(); stack.Push(root);
        while (stack.Count > 0)
        {
            foreach (var info in new DirectoryInfo(stack.Pop()).EnumerateFileSystemInfos())
            {
                if ((info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.System | FileAttributes.Offline)) != 0) continue;
                if (info is DirectoryInfo directory) stack.Push(directory.FullName);
                else if (info is FileInfo file)
                {
                    using var handle = File.OpenHandle(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    string? native = GetFileInformationByHandleEx(handle, 18, out var id, 24) ? $"{id.Volume:X16}:{id.High:X16}{id.Low:X16}" : null;
                    files.Add(new(file.FullName, file.Length, file.LastWriteTimeUtc.Ticks) { NativeId = native });
                }
            }
        }
        return files;
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileIdInfo { public ulong Volume, Low, High; }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out FileIdInfo info, uint size);
}
