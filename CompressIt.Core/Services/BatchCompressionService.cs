namespace CompressIt.Core.Services;

public static class BatchCompressionService
{
    public static List<string> EnumerateVideosInFolder(string folderPath, bool includeSubfolders = false)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return [];
        }

        var extSet = new HashSet<string>(ShellIntegrationService.VideoExtensions, StringComparer.OrdinalIgnoreCase);
        var option = includeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        return Directory.EnumerateFiles(folderPath, "*.*", option)
            .Where(path => extSet.Contains(Path.GetExtension(path)))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static async Task RunParallelAsync<T>(
        IReadOnlyList<T> items,
        int maxParallelism,
        Func<T, CancellationToken, Task> worker,
        CancellationToken ct = default)
    {
        if (items.Count == 0) return;

        int concurrency = Math.Clamp(maxParallelism, 1, 8);
        using var semaphore = new SemaphoreSlim(concurrency, concurrency);

        var tasks = items.Select(async (item, index) =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                // Stagger initial concurrent workers slightly to avoid GPU encoder init collisions
                if (index < concurrency && index > 0)
                {
                    await Task.Delay(index * 250, ct);
                }
                ct.ThrowIfCancellationRequested();
                await worker(item, ct);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
    }
}
