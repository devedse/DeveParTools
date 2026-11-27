using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace DeveParTools.Updates;

/// <summary>
/// Updates devepar or deveparui to the latest GitHub release: <see cref="CheckAsync"/> asks GitHub what the latest
/// release is, <see cref="InstallAsync"/> downloads this system's archive, checks it, and puts the new program in place
/// of the running one.
/// </summary>
/// <remarks>
/// <para><b>Finding the release.</b> One request without an account:
/// <c>GET https://api.github.com/repos/devedse/DeveParTools/releases/latest</c>. The answer has the tag (1.0.41) and every
/// file of the release with its download address, size and SHA-256. The files are named
/// <c>{program}-{system}.zip</c> on Windows and <c>{program}-{system}.tar.gz</c> elsewhere, e.g.
/// deveparui-win-x64.zip, devepar-linux-arm64.tar.gz (see <see cref="AssetName"/>).</para>
/// <para><b>Installing.</b> Everything happens in the program's own folder, so the last step is a rename or copy on
/// the same disk. Steps 1 and 2 (<see cref="PrepareAsync"/>):</para>
/// <code>
/// 1. download   → .deveparui-update.download   (SHA-256 checked against GitHub's while it arrives)
/// 2. extract    → .deveparui-update\deveparui.exe   (with the running program's permissions on Linux/macOS)
/// </code>
/// <para>Then there are two ways to finish, because a single-file .NET program reads parts of itself from its file
/// whenever it first needs them (it opens the file by its path). Once the new version is at that path, the old process
/// cannot load anything it has not loaded yet.</para>
/// <code>
/// devepar (exits right after)        3. swap: devepar → devepar.old, .devepar-update/devepar → devepar  (InstallAsync)
///                                    4. print the result and exit; the next start deletes devepar.old
///
/// deveparui (restarts itself)        3. the old app starts .deveparui-update/deveparui --finish-update &lt;path&gt; &lt;pid&gt;
///                                       and exits, without touching its own file
///                                    4. the new copy waits for that process to end, renames deveparui → .old,
///                                       copies itself to deveparui (CompleteStaged) and starts it
///                                    5. the started app deletes .old and the folder (RemoveLeftovers)
/// </code>
/// <para>Renaming is allowed while a program runs (Windows does not allow overwriting or deleting it), and if putting the
/// new file in place fails, the rename is undone, so there is always a working program at the original path.</para>
/// <para>A folder the user cannot write to (Program Files, /usr/local/bin) fails at step 1, before anything changes,
/// with a message that says to run the update with administrator rights.</para>
/// </remarks>
public sealed class Updater : IDisposable
{
    /// <summary>The GitHub repository whose releases hold the programs.</summary>
    public const string Repository = "devedse/DeveParTools";

    /// <summary>How long an automatic check's answer is used before GitHub is asked again.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

    private readonly HttpClient _http;
    private readonly Uri _api;

    /// <param name="product">The program: "devepar" or "deveparui" (the start of its release file names and the file in them).</param>
    /// <param name="current">The running version.</param>
    /// <param name="handler">The HTTP handler (tests pass a fake one); default: the normal one.</param>
    /// <param name="api">The GitHub API address (default: <see cref="DefaultApi"/>).</param>
    public Updater(string product, Version current, HttpMessageHandler? handler = null, Uri? api = null)
    {
        Product = product;
        Current = Normalize(current);
        _api = api ?? DefaultApi;
        _http = handler == null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(30);

        // GitHub's API requires a User-Agent; this one names the program, nothing about the user.
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DeveParTools", Current.ToString()));
    }

    /// <summary>
    /// https://api.github.com/, or the address in DEVEPARTOOLS_UPDATE_API (for trying updates against a local copy of the
    /// API, which serves repos/devedse/DeveParTools/releases/latest and the files it lists).
    /// </summary>
    public static Uri DefaultApi => Environment.GetEnvironmentVariable("DEVEPARTOOLS_UPDATE_API") is { Length: > 0 } api
        ? new Uri(api.EndsWith('/') ? api : api + "/")
        : new Uri("https://api.github.com/");

    /// <summary>The releases page, for people who download by hand.</summary>
    public static Uri ReleasesPage { get; } = new($"https://github.com/{Repository}/releases");

    /// <summary>The page of one release (the tags are the bare versions: 1.0.41).</summary>
    public static Uri ReleasePage(Version version) => new($"{ReleasesPage}/tag/{Normalize(version)}");

    public string Product { get; }

    /// <summary>The running version, as major.minor.build.</summary>
    public Version Current { get; }

    /// <summary>This system as a .NET runtime identifier (win-x64, linux-arm64, osx-arm64 …), or null when it is none of those.</summary>
    public string? RuntimeId { get; init; } = RuntimeIdOf(OperatingSystem.IsWindows(), OperatingSystem.IsLinux(), OperatingSystem.IsMacOS(), RuntimeInformation.ProcessArchitecture);

    /// <summary>The runtime identifier for an operating system and processor, or null for other systems (32-bit, FreeBSD …).</summary>
    public static string? RuntimeIdOf(bool windows, bool linux, bool macOs, Architecture architecture)
    {
        string? os = windows ? "win" : linux ? "linux" : macOs ? "osx" : null;
        string? cpu = architecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => null,
        };
        return os == null || cpu == null ? null : $"{os}-{cpu}";
    }

    /// <summary>The release file for a system: deveparui-win-x64.zip, devepar-linux-x64.tar.gz.</summary>
    public string AssetName(string runtimeId) => $"{Product}-{runtimeId}{(runtimeId.StartsWith("win-", StringComparison.Ordinal) ? ".zip" : ".tar.gz")}";

    /// <summary>1.0.41.0 and 1.0.41 are the same version: keep major.minor.build.</summary>
    public static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build));

    /// <summary>
    /// The program an update replaces: the running one, unless this is not a released build. Development builds
    /// (version 1.0.0), programs started through "dotnet" and builds with their .dll next to them are not updated:
    /// replacing them would mix a release into a build folder.
    /// </summary>
    /// <param name="processPath">The running program (Environment.ProcessPath).</param>
    public static UpdateTarget FindTarget(string? processPath, Version current, string product)
    {
        string download = $"Download a release from {ReleasesPage}.";
        if (processPath == null || Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return new(null, $"{product} runs through dotnet here, so it cannot replace itself. {download}");
        }

        if (Normalize(current) == new Version(1, 0, 0))
        {
            return new(null, $"This is a development build (version 1.0.0), which does not update itself. {download}");
        }

        // The program's .dll next to it: deveparui.exe → deveparui.dll, and on Linux (no extension, maybe dots in the
        // name) My.App → My.App.dll.
        string withoutExe = processPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? processPath[..^4] : processPath;
        if (File.Exists(withoutExe + ".dll"))
        {
            return new(null, $"This {product} is a build folder, not the released single program, so it does not update itself. {download}");
        }

        return new(processPath, null);
    }

    /// <summary>Asks GitHub for the latest release.</summary>
    /// <exception cref="UpdateException">GitHub could not be reached, refused, or answered something unreadable.</exception>
    public async Task<UpdateRelease> GetLatestAsync(CancellationToken token = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_api, $"repos/{Repository}/releases/latest"));
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Send(() => _http.SendAsync(request, token), "check for updates", token);
        if (!response.IsSuccessStatusCode)
        {
            throw new UpdateException(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
                ? "GitHub limits how often it can be asked without an account. Try again in an hour."
                : $"GitHub answered {(int)response.StatusCode} ({response.ReasonPhrase}) when asked for the latest release.");
        }

        return ParseRelease(await response.Content.ReadAsStringAsync(token));
    }

    /// <summary>GitHub's answer (JSON) as a release.</summary>
    internal static UpdateRelease ParseRelease(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return ParseRelease(document.RootElement);
        }
        catch (JsonException e)
        {
            throw new UpdateException("GitHub's answer about the latest release could not be read.", e);
        }
    }

    /// <summary>The fields of GitHub's release object that matter here: tag_name, html_url, assets[name, browser_download_url, size, digest].</summary>
    internal static UpdateRelease ParseRelease(JsonElement root)
    {
        string tag;
        Uri page;
        List<UpdateAsset> assets;
        try
        {
            tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
            page = new Uri(root.GetProperty("html_url").GetString()!);
            assets = root.GetProperty("assets").EnumerateArray().Select(a => new UpdateAsset(
                a.GetProperty("name").GetString() ?? string.Empty,
                new Uri(a.GetProperty("browser_download_url").GetString()!),
                a.GetProperty("size").GetInt64(),
                a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String && d.GetString()!.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                    ? d.GetString()![7..].ToLowerInvariant()
                    : null)).ToList();
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException or ArgumentNullException)
        {
            throw new UpdateException("GitHub's answer about the latest release could not be read.", e);
        }

        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version))
        {
            throw new UpdateException($"The latest release has a name that is not a version: {tag}.");
        }

        return new UpdateRelease(Normalize(version), tag, page, assets);
    }

    /// <summary>
    /// Asks GitHub for the latest release and picks this system's file from it. With a cache, the answer is remembered
    /// (see <see cref="LatestForAutomaticCheckAsync"/>).
    /// </summary>
    /// <exception cref="UpdateException">GitHub could not be asked.</exception>
    public async Task<UpdateCheck> CheckAsync(UpdateCheckCache? cache = null, CancellationToken token = default)
    {
        var latest = await GetLatestAsync(token);
        cache?.Write(DateTimeOffset.UtcNow, latest.Version);
        var asset = RuntimeId == null ? null : latest.Assets.FirstOrDefault(a => a.Name == AssetName(RuntimeId));
        return new UpdateCheck(Current, latest, asset);
    }

    /// <summary>
    /// The latest version for an automatic check: the cached answer when it is less than <see cref="CheckInterval"/>
    /// old, otherwise GitHub's (then cached). Never throws: when GitHub cannot be asked, the attempt is cached too (so an
    /// offline computer does not try on every start) and the previous answer is returned, or null without one.
    /// </summary>
    public async Task<Version?> LatestForAutomaticCheckAsync(UpdateCheckCache cache, DateTimeOffset now, CancellationToken token = default)
    {
        var cached = cache.Read();
        if (cached is { } c && c.Checked <= now && now - c.Checked < CheckInterval)
        {
            return c.Latest;
        }

        try
        {
            var latest = await GetLatestAsync(token);
            cache.Write(now, latest.Version);
            return latest.Version;
        }
        catch (Exception e) when (e is UpdateException or OperationCanceledException)
        {
            cache.Write(now, cached?.Latest ?? Current);
            return cached?.Latest;
        }
    }

    /// <summary>
    /// Downloads <paramref name="asset"/>, checks it, and replaces <paramref name="executable"/> with the program in it
    /// right away (see the class remarks). For a program that exits right after, like devepar: the running process must
    /// not need any part of itself it has not loaded yet, because those are read from its file, which is then the new
    /// version. A program that restarts itself uses <see cref="PrepareAsync"/> and <see cref="CompleteStaged"/> instead.
    /// </summary>
    /// <exception cref="UpdateException">The download failed, was damaged, did not contain the program, or the folder is not writable.</exception>
    public async Task InstallAsync(UpdateAsset asset, string executable, IProgress<UpdateProgress>? progress = null, CancellationToken token = default)
    {
        string program = await PrepareAsync(asset, executable, progress, token);
        try
        {
            progress?.Report(new UpdateProgress(UpdateStage.Installing, 0, 1));
            Replace(Path.GetFullPath(executable), program);
            progress?.Report(new UpdateProgress(UpdateStage.Installing, 1, 1));
        }
        finally
        {
            TryDelete(StagingPath(executable, Product));
        }
    }

    /// <summary>
    /// Downloads <paramref name="asset"/>, checks it, and extracts it into a folder next to
    /// <paramref name="executable"/> (.deveparui-update); returns the new program there, ready to run, with the
    /// running one's permissions. Nothing else is changed except the release's other files (LICENSE …) that the user
    /// kept next to the program.
    /// </summary>
    /// <exception cref="UpdateException">The download failed, was damaged, did not contain the program, or the folder is not writable.</exception>
    public async Task<string> PrepareAsync(UpdateAsset asset, string executable, IProgress<UpdateProgress>? progress = null, CancellationToken token = default)
    {
        executable = Path.GetFullPath(executable);
        string folder = Path.GetDirectoryName(executable)!;
        string download = DownloadPath(executable, Product);
        string staging = StagingPath(executable, Product);
        try
        {
            FileStream file;
            try
            {
                file = new FileStream(download, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.Asynchronous);
            }
            catch (UnauthorizedAccessException e)
            {
                throw new UpdateException($"No permission to change files in {folder}. Run the update with administrator rights (sudo on Linux and macOS), or download the new version from {ReleasesPage}.", e);
            }

            await using (file)
            {
                await DownloadAsync(asset, file, progress, token);
            }

            string program = await Task.Run(() => Extract(download, asset.Name, staging, progress, token), token);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(program, File.GetUnixFileMode(executable) | UnixFileMode.UserExecute);
            }

            // The release's other files (LICENSE, THIRD_PARTY_NOTICES.md) are updated where the user kept them.
            foreach (var extra in Directory.EnumerateFiles(staging).Where(f => f != program))
            {
                string target = Path.Combine(folder, Path.GetFileName(extra));
                if (File.Exists(target))
                {
                    File.Copy(extra, target, overwrite: true);
                }
            }

            return program;
        }
        catch
        {
            TryDelete(staging);
            throw;
        }
        finally
        {
            TryDelete(download);
        }
    }

    /// <summary>
    /// The second half of an update for a program that restarts (deveparui), run by the new program from the folder
    /// <see cref="PrepareAsync"/> extracted it to: waits until the old program has exited, then copies itself over
    /// <paramref name="executable"/>. The old file is kept as .old until the copy is complete and put back if it fails.
    /// The staged program is copied, not moved: it is running, and reads parts of itself from its file as it needs them.
    /// </summary>
    /// <param name="staged">The new program (the running one).</param>
    /// <param name="waitForProcessId">The old program's process, or null when it has already exited.</param>
    public static void CompleteStaged(string staged, string executable, int? waitForProcessId, TimeSpan wait)
    {
        if (waitForProcessId is int pid)
        {
            WaitForExit(pid, wait);
        }

        string old = OldPath(executable);
        TryDelete(old);
        File.Move(executable, old, overwrite: true);
        try
        {
            File.Copy(staged, executable);
        }
        catch
        {
            File.Move(old, executable, overwrite: true);
            throw;
        }

        TryDelete(old);
    }

    /// <summary>Waits for a process to exit (at most <paramref name="wait"/>); a process that is already gone is fine.</summary>
    internal static void WaitForExit(int processId, TimeSpan wait)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            process.WaitForExit(wait);
        }
        catch (ArgumentException)
        {
            // Not running (any more).
        }
    }

    private async Task DownloadAsync(UpdateAsset asset, Stream target, IProgress<UpdateProgress>? progress, CancellationToken token)
    {
        using var response = await Send(() => _http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, token), $"download {asset.Name}", token);
        if (!response.IsSuccessStatusCode)
        {
            throw new UpdateException($"Downloading {asset.Name} failed: GitHub answered {(int)response.StatusCode} ({response.ReasonPhrase}).");
        }

        long total = response.Content.Headers.ContentLength ?? asset.Size;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var source = await response.Content.ReadAsStreamAsync(token);
        var buffer = new byte[1 << 16];
        long done = 0;
        progress?.Report(new UpdateProgress(UpdateStage.Downloading, 0, total));
        for (int n; (n = await source.ReadAsync(buffer, token)) > 0;)
        {
            hash.AppendData(buffer, 0, n);
            await target.WriteAsync(buffer.AsMemory(0, n), token);
            done += n;
            progress?.Report(new UpdateProgress(UpdateStage.Downloading, done, total));
        }

        if (asset.Size > 0 && done != asset.Size)
        {
            throw new UpdateException($"The download of {asset.Name} stopped early ({done:N0} of {asset.Size:N0} bytes). Try again.");
        }

        if (asset.Sha256 != null && Convert.ToHexStringLower(hash.GetHashAndReset()) != asset.Sha256)
        {
            throw new UpdateException($"The download of {asset.Name} is damaged: its SHA-256 differs from the one GitHub published. Try again.");
        }
    }

    /// <summary>Runs a request, turning "no network" and time-outs into an <see cref="UpdateException"/>.</summary>
    private static async Task<HttpResponseMessage> Send(Func<Task<HttpResponseMessage>> send, string what, CancellationToken token)
    {
        try
        {
            return await send();
        }
        catch (HttpRequestException e)
        {
            throw new UpdateException($"Could not reach GitHub to {what}: {e.Message}", e);
        }
        catch (TaskCanceledException e) when (!token.IsCancellationRequested)
        {
            throw new UpdateException($"GitHub did not answer in time when trying to {what}.", e);
        }
    }

    /// <summary>
    /// Extracts the top-level files of the archive (zip or tar.gz) into <paramref name="staging"/> and returns the
    /// program among them. Anything in a sub-folder, or with ".." in its name, is ignored.
    /// </summary>
    internal string Extract(string archive, string assetName, string staging, IProgress<UpdateProgress>? progress, CancellationToken token)
    {
        TryDelete(staging);
        Directory.CreateDirectory(staging);
        if (assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(archive);
            long total = zip.Entries.Sum(e => e.CompressedLength), done = 0;
            foreach (var entry in zip.Entries)
            {
                token.ThrowIfCancellationRequested();
                if (TopLevelName(entry.FullName) is { } name)
                {
                    entry.ExtractToFile(Path.Combine(staging, name), overwrite: true);
                }

                done += entry.CompressedLength;
                progress?.Report(new UpdateProgress(UpdateStage.Extracting, done, total));
            }
        }
        else
        {
            using var file = File.OpenRead(archive);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var tar = new TarReader(gzip);
            while (tar.GetNextEntry() is { } entry)
            {
                token.ThrowIfCancellationRequested();
                if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile && TopLevelName(entry.Name) is { } name)
                {
                    entry.ExtractToFile(Path.Combine(staging, name), overwrite: true);
                }

                progress?.Report(new UpdateProgress(UpdateStage.Extracting, file.Position, file.Length));
            }
        }

        return new[] { Product + ".exe", Product }.Select(n => Path.Combine(staging, n)).FirstOrDefault(File.Exists)
            ?? throw new UpdateException($"{assetName} does not contain {Product}.");
    }

    /// <summary>"devepar" or "./devepar" → "devepar"; null for folders, files in folders, "." and "..".</summary>
    internal static string? TopLevelName(string entry)
    {
        string name = entry.Replace('\\', '/');
        if (name.StartsWith("./", StringComparison.Ordinal))
        {
            name = name[2..];
        }

        return name.Length == 0 || name.Contains('/') || name is "." or ".." ? null : name;
    }

    /// <summary>
    /// Puts <paramref name="replacement"/> at <paramref name="executable"/>'s path: the old file is renamed to .old first
    /// (allowed even while it runs), and renamed back if the new one cannot be moved in.
    /// </summary>
    internal static void Replace(string executable, string replacement)
    {
        string old = OldPath(executable);
        TryDelete(old);
        File.Move(executable, old, overwrite: true);
        try
        {
            File.Move(replacement, executable);
        }
        catch
        {
            File.Move(old, executable);
            throw;
        }

        TryDelete(old); // Windows: fails while the old program runs; RemoveLeftovers deletes it at the next start.
    }

    /// <summary>Deletes what an earlier update left next to <paramref name="executable"/>: the .old program, a download, an extract folder.</summary>
    public static void RemoveLeftovers(string executable, string product)
    {
        foreach (var path in new[] { OldPath(executable), DownloadPath(executable, product), StagingPath(executable, product) })
        {
            TryDelete(path);
        }
    }

    internal static string OldPath(string executable) => executable + ".old";

    private static string DownloadPath(string executable, string product) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(executable))!, $".{product}-update.download");

    /// <summary>The folder an update is extracted to: .deveparui-update next to the program.</summary>
    public static string StagingPath(string executable, string product) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(executable))!, $".{product}-update");

    /// <summary>Deletes a file or folder if it exists; failures (in use, no permission) are ignored.</summary>
    internal static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else
            {
                File.Delete(path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose() => _http.Dispose();
}
