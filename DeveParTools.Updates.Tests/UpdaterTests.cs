using System.Net;
using System.Runtime.InteropServices;

namespace DeveParTools.Updates.Tests;

[TestClass]
public sealed class UpdaterTests
{
    private const string Rid = "linux-x64";
    private string _dir = null!;

    [TestInitialize]
    public void Init()
    {
        _dir = Path.Combine(Path.GetTempPath(), "devepar-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (!OperatingSystem.IsWindows())
        {
            foreach (var d in Directory.EnumerateDirectories(_dir, "*", SearchOption.AllDirectories).Prepend(_dir))
            {
                File.SetUnixFileMode(d, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        Directory.Delete(_dir, true);
    }

    private static Updater Make(FakeGitHub github, string product = "devepar", string version = "1.0.40", string? rid = Rid)
        => new(product, Version.Parse(version), github) { RuntimeId = rid };

    private string Program(string name = "devepar", string content = "old program")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private sealed class Recorder : IProgress<UpdateProgress>
    {
        public List<UpdateProgress> Reports { get; } = [];

        public void Report(UpdateProgress value) => Reports.Add(value);
    }

    // ---------------------------------------------------------------- systems and names

    [TestMethod]
    public void RuntimeIdOf_NamesTheSupportedSystems()
    {
        Assert.AreEqual("win-x64", Updater.RuntimeIdOf(true, false, false, Architecture.X64));
        Assert.AreEqual("linux-arm64", Updater.RuntimeIdOf(false, true, false, Architecture.Arm64));
        Assert.AreEqual("osx-arm64", Updater.RuntimeIdOf(false, false, true, Architecture.Arm64));
        Assert.IsNull(Updater.RuntimeIdOf(false, false, false, Architecture.X64), "another operating system");
        Assert.IsNull(Updater.RuntimeIdOf(true, false, false, Architecture.X86), "32-bit");
        Assert.IsNotNull(new Updater("devepar", new Version(1, 0, 40)).RuntimeId, "the test machines are all supported systems");
    }

    [TestMethod]
    public void AssetName_ZipOnWindows_TarGzElsewhere()
    {
        using var u = Make(new FakeGitHub(), "deveparui");
        Assert.AreEqual("deveparui-win-x64.zip", u.AssetName("win-x64"));
        Assert.AreEqual("deveparui-osx-arm64.tar.gz", u.AssetName("osx-arm64"));
        Assert.AreEqual(new Version(1, 0, 40), u.Current, "1.0.40.0 counts as 1.0.40");
        Assert.AreEqual(new Version(1, 2, 0), Updater.Normalize(new Version(1, 2)));
        Assert.AreEqual("https://github.com/devedse/DeveParTools/releases", Updater.ReleasesPage.ToString());
        Assert.AreEqual("https://github.com/devedse/DeveParTools/releases/tag/1.0.41", Updater.ReleasePage(new Version(1, 0, 41, 0)).ToString());
    }

    [TestMethod]
    public void FindTarget_OnlyReleasedSingleFilePrograms()
    {
        var released = new Version(1, 0, 40);
        StringAssert.Contains(Updater.FindTarget(null, released, "devepar").Reason, "through dotnet");
        StringAssert.Contains(Updater.FindTarget("/usr/share/dotnet/dotnet", released, "devepar").Reason, "through dotnet");
        StringAssert.Contains(Updater.FindTarget(Program(), new Version(1, 0, 0, 0), "devepar").Reason, "development build");

        var exe = Program();
        File.WriteAllText(Path.Combine(_dir, "devepar.dll"), "a build folder");
        StringAssert.Contains(Updater.FindTarget(exe, released, "devepar").Reason, "build folder");
        File.Delete(Path.Combine(_dir, "devepar.dll"));

        var dotted = Program("DeveParTools.App.Tests");
        File.WriteAllText(Path.Combine(_dir, "DeveParTools.App.Tests.dll"), "a test host");
        StringAssert.Contains(Updater.FindTarget(dotted, released, "devepar").Reason, "build folder", "a name with dots (Linux, no .exe)");
        var windowsStyle = Program("deveparui.exe");
        File.WriteAllText(Path.Combine(_dir, "deveparui.dll"), "a build folder");
        StringAssert.Contains(Updater.FindTarget(windowsStyle, released, "deveparui").Reason, "build folder", "deveparui.exe next to deveparui.dll");

        var target = Updater.FindTarget(exe, released, "devepar");
        Assert.AreEqual(exe, target.Executable);
        Assert.IsNull(target.Reason);
    }

    [TestMethod]
    public void TopLevelName_KeepsOnlyFilesAtTheTop()
    {
        Assert.AreEqual("devepar", Updater.TopLevelName("devepar"));
        Assert.AreEqual("devepar", Updater.TopLevelName("./devepar"));
        Assert.AreEqual("devepar.exe", Updater.TopLevelName(@".\devepar.exe"));
        Assert.IsNull(Updater.TopLevelName("./"));
        Assert.IsNull(Updater.TopLevelName("sub/devepar"));
        Assert.IsNull(Updater.TopLevelName("../devepar"));
        Assert.IsNull(Updater.TopLevelName(".."));
        Assert.IsNull(Updater.TopLevelName("."));
    }

    [TestMethod]
    public void Models_ReportAvailabilityAndFractions()
    {
        var release = new UpdateRelease(new Version(1, 0, 41), "1.0.41", Updater.ReleasesPage, []);
        Assert.IsTrue(new UpdateCheck(new Version(1, 0, 40), release, null).UpdateAvailable);
        Assert.IsFalse(new UpdateCheck(new Version(1, 0, 41), release, null).UpdateAvailable);
        Assert.AreEqual(0, new UpdateProgress(UpdateStage.Downloading, 5, 0).Fraction, "unknown total");
        Assert.AreEqual(0.25, new UpdateProgress(UpdateStage.Downloading, 25, 100).Fraction);
        Assert.AreEqual(1, new UpdateProgress(UpdateStage.Extracting, 120, 100).Fraction);
        var inner = new IOException("x");
        Assert.AreSame(inner, new UpdateException("m", inner).InnerException);
        Assert.AreEqual("m", new UpdateException("m").Message);
    }

    // ---------------------------------------------------------------- checking

    [TestMethod]
    public async Task Check_FindsTheLatestReleaseAndThisSystemsFile()
    {
        var github = new FakeGitHub();
        byte[] data = [1, 2, 3];
        github.Json(FakeGitHub.LatestUrl, FakeGitHub.ReleaseJson("1.0.41", ("devepar-linux-x64.tar.gz", data, FakeGitHub.Sha256Digest(data)), ("devepar-win-x64.zip", data, null), ("deveparui-linux-x64.tar.gz", data, "md5:abc")));
        using var u = Make(github);
        var cache = new UpdateCheckCache(Path.Combine(_dir, "cache.txt"));

        var check = await u.CheckAsync(cache);

        Assert.IsTrue(check.UpdateAvailable);
        Assert.AreEqual(new Version(1, 0, 41), check.Latest.Version);
        Assert.AreEqual("1.0.41", check.Latest.Tag);
        Assert.AreEqual("https://github.com/devedse/DeveParTools/releases/tag/1.0.41", check.Latest.Page.ToString());
        Assert.AreEqual("devepar-linux-x64.tar.gz", check.Asset!.Name);
        Assert.AreEqual(3, check.Asset.Size);
        Assert.AreEqual(FakeGitHub.Sha256Digest(data)[7..], check.Asset.Sha256);
        Assert.IsNull(check.Latest.Assets[1].Sha256, "no digest");
        Assert.IsNull(check.Latest.Assets[2].Sha256, "not a SHA-256");
        Assert.AreEqual(new Version(1, 0, 41), cache.Read()!.Value.Latest, "a check is remembered");

        var request = github.Seen.Single();
        StringAssert.StartsWith(request.Headers.UserAgent.ToString(), "DeveParTools/1.0.40");
        StringAssert.Contains(request.Headers.Accept.ToString(), "application/vnd.github+json");
    }

    [TestMethod]
    public async Task Check_NoFileForThisSystem()
    {
        var github = new FakeGitHub();
        github.Json(FakeGitHub.LatestUrl, FakeGitHub.ReleaseJson("v1.0.39", ("devepar-win-x64.zip", [1], null)));
        using var other = Make(github);
        var check = await other.CheckAsync();
        Assert.IsNull(check.Asset, "no linux-x64 file");
        Assert.IsFalse(check.UpdateAvailable, "v1.0.39 is older");

        using var unsupported = Make(github, rid: null);
        Assert.IsNull((await unsupported.CheckAsync()).Asset);
    }

    [TestMethod]
    public async Task Check_UsesTheGivenApiAddress()
    {
        var github = new FakeGitHub();
        github.Json("https://example.test/api/repos/devedse/DeveParTools/releases/latest", FakeGitHub.ReleaseJson("1.0.50"));
        using var u = new Updater("devepar", new Version(1, 0, 40), github, new Uri("https://example.test/api/"));
        Assert.AreEqual(new Version(1, 0, 50), (await u.GetLatestAsync()).Version);
    }

    [TestMethod]
    public async Task Check_TheApiCanBePointedElsewhereForTesting()
    {
        var github = new FakeGitHub();
        github.Json("http://localhost:8765/repos/devedse/DeveParTools/releases/latest", FakeGitHub.ReleaseJson("1.0.60"));
        try
        {
            Environment.SetEnvironmentVariable("DEVEPARTOOLS_UPDATE_API", "http://localhost:8765");
            Assert.AreEqual("http://localhost:8765/", Updater.DefaultApi.ToString());
            using var u = Make(github);
            Assert.AreEqual(new Version(1, 0, 60), (await u.GetLatestAsync()).Version);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEVEPARTOOLS_UPDATE_API", null);
        }

        Assert.AreEqual("https://api.github.com/", Updater.DefaultApi.ToString());
    }

    [TestMethod]
    public async Task Check_GitHubErrorsBecomeReadableMessages()
    {
        async Task<string> MessageFor(Action<FakeGitHub> setup)
        {
            var github = new FakeGitHub();
            setup(github);
            using var u = Make(github);
            return (await Assert.ThrowsExactlyAsync<UpdateException>(() => u.CheckAsync())).Message;
        }

        StringAssert.Contains(await MessageFor(g => g.Status(FakeGitHub.LatestUrl, HttpStatusCode.Forbidden)), "Try again in an hour");
        StringAssert.Contains(await MessageFor(g => g.Status(FakeGitHub.LatestUrl, HttpStatusCode.TooManyRequests)), "Try again in an hour");
        StringAssert.Contains(await MessageFor(g => g.Status(FakeGitHub.LatestUrl, HttpStatusCode.InternalServerError)), "GitHub answered 500");
        StringAssert.Contains(await MessageFor(g => g.Fail(FakeGitHub.LatestUrl, new HttpRequestException("No such host is known."))), "Could not reach GitHub to check for updates: No such host");
        StringAssert.Contains(await MessageFor(g => g.Fail(FakeGitHub.LatestUrl, new TaskCanceledException("timed out"))), "did not answer in time");
        StringAssert.Contains(await MessageFor(g => g.Json(FakeGitHub.LatestUrl, "<html>not json")), "could not be read");
        StringAssert.Contains(await MessageFor(g => g.Json(FakeGitHub.LatestUrl, "{\"html_url\":\"https://x\",\"assets\":[]}")), "could not be read");
        StringAssert.Contains(await MessageFor(g => g.Json(FakeGitHub.LatestUrl, "{\"tag_name\":\"1.0.41\",\"html_url\":\"not a url\",\"assets\":[]}")), "could not be read");
        StringAssert.Contains(await MessageFor(g => g.Json(FakeGitHub.LatestUrl, "{\"tag_name\":\"1.0.41\",\"html_url\":null,\"assets\":[]}")), "could not be read");
        StringAssert.Contains(await MessageFor(g => g.Json(FakeGitHub.LatestUrl, "{\"tag_name\":\"1.0.41\",\"html_url\":\"https://x\",\"assets\":{}}")), "could not be read");
        StringAssert.Contains(await MessageFor(g => g.Json(FakeGitHub.LatestUrl, "{\"tag_name\":\"nightly\",\"html_url\":\"https://x\",\"assets\":[]}")), "not a version: nightly");
        StringAssert.Contains(await MessageFor(g => g.Json(FakeGitHub.LatestUrl, "{\"tag_name\":null,\"html_url\":\"https://x\",\"assets\":[{\"name\":null,\"browser_download_url\":\"https://x/a\",\"size\":1}]}")), "not a version");
    }

    [TestMethod]
    public async Task Check_CancellationIsNotATimeout()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var u = Make(new FakeGitHub());
        await Assert.ThrowsAsync<OperationCanceledException>(() => u.CheckAsync(token: cts.Token));
    }

    // ---------------------------------------------------------------- automatic checks

    [TestMethod]
    public async Task AutomaticCheck_AsksGitHubAtMostOnceADay()
    {
        var github = new FakeGitHub();
        github.Json(FakeGitHub.LatestUrl, FakeGitHub.ReleaseJson("1.0.41"));
        using var u = Make(github);
        var cache = new UpdateCheckCache(Path.Combine(_dir, "sub", "cache.txt"));
        var now = new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

        Assert.AreEqual(new Version(1, 0, 41), await u.LatestForAutomaticCheckAsync(cache, now));
        Assert.AreEqual(1, github.Requests);
        Assert.AreEqual(new Version(1, 0, 41), await u.LatestForAutomaticCheckAsync(cache, now.AddHours(23)), "from the cache");
        Assert.AreEqual(1, github.Requests);

        github.Json(FakeGitHub.LatestUrl, FakeGitHub.ReleaseJson("1.0.42"));
        Assert.AreEqual(new Version(1, 0, 42), await u.LatestForAutomaticCheckAsync(cache, now.AddHours(25)), "a day later: asked again");
        Assert.AreEqual(2, github.Requests);
        Assert.AreEqual(new Version(1, 0, 42), await u.LatestForAutomaticCheckAsync(cache, now.AddHours(-1)), "a check in the future (clock changed): asked again");
        Assert.AreEqual(3, github.Requests);
    }

    [TestMethod]
    public async Task AutomaticCheck_FailuresAreRememberedAndNeverThrow()
    {
        var github = new FakeGitHub();
        github.Fail(FakeGitHub.LatestUrl, new HttpRequestException("offline"));
        using var u = Make(github);
        var cache = new UpdateCheckCache(Path.Combine(_dir, "cache.txt"));
        var now = new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

        Assert.IsNull(await u.LatestForAutomaticCheckAsync(cache, now), "offline, nothing known");
        Assert.AreEqual(new Version(1, 0, 40), cache.Read()!.Value.Latest, "the attempt is remembered (with the running version)");
        Assert.AreEqual(u.Current, await u.LatestForAutomaticCheckAsync(cache, now.AddHours(1)), "not asked again within a day: no update known");
        Assert.AreEqual(1, github.Requests);

        cache.Write(now.AddDays(-2), new Version(1, 0, 45));
        Assert.AreEqual(new Version(1, 0, 45), await u.LatestForAutomaticCheckAsync(cache, now), "offline: the previous answer");

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        cache.Write(now.AddDays(-2), new Version(1, 0, 46));
        Assert.AreEqual(new Version(1, 0, 46), await u.LatestForAutomaticCheckAsync(cache, now, cts.Token), "cancelled (the command finished first)");
    }

    // ---------------------------------------------------------------- installing

    [TestMethod]
    public async Task Install_TarGz_ReplacesTheProgramAndKeepsItsPermissions()
    {
        var exe = Program();
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        }

        File.WriteAllText(Path.Combine(_dir, "LICENSE"), "old license");
        var archive = FakeGitHub.TarGz(("./devepar", "new program"), ("./LICENSE", "new license"), ("./THIRD_PARTY_NOTICES.md", "notices"));
        var asset = new UpdateAsset("devepar-linux-x64.tar.gz", new Uri(FakeGitHub.DownloadUrl("1.0.41", "devepar-linux-x64.tar.gz")), archive.Length, FakeGitHub.Sha256Digest(archive)[7..]);
        var github = new FakeGitHub();
        github.Bytes(asset.Url.ToString(), archive);
        using var u = Make(github);
        var progress = new Recorder();

        await u.InstallAsync(asset, exe, progress);

        Assert.AreEqual("new program", File.ReadAllText(exe));
        Assert.AreEqual("new license", File.ReadAllText(Path.Combine(_dir, "LICENSE")), "files the user kept are updated");
        Assert.IsFalse(File.Exists(Path.Combine(_dir, "THIRD_PARTY_NOTICES.md")), "no new files next to the program");
        CollectionAssert.AreEquivalent(new[] { "devepar", "LICENSE" }, Directory.GetFileSystemEntries(_dir).Select(Path.GetFileName).ToArray(), "no leftovers");
        if (!OperatingSystem.IsWindows())
        {
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute, File.GetUnixFileMode(exe));
        }

        var stages = progress.Reports.Select(r => r.Stage).Distinct().ToList();
        CollectionAssert.AreEqual(new[] { UpdateStage.Downloading, UpdateStage.Extracting, UpdateStage.Installing }, stages);
        Assert.AreEqual(1, progress.Reports.Last(r => r.Stage == UpdateStage.Downloading).Fraction);
        Assert.AreEqual(archive.Length, progress.Reports.First(r => r.Stage == UpdateStage.Downloading).Total);
        Assert.AreEqual(1, progress.Reports.Last().Fraction);
    }

    [TestMethod]
    public async Task Install_Zip_WithoutDigestOrLength_IgnoresFoldersAndNestedFiles()
    {
        var exe = Program("deveparui-renamed.exe");
        var archive = FakeGitHub.Zip(("deveparui.exe", "new ui"), ("docs/", string.Empty), ("docs/readme.txt", "nested"), ("../escape.txt", "outside"));
        var asset = new UpdateAsset("deveparui-win-x64.zip", new Uri(FakeGitHub.DownloadUrl("1.0.41", "deveparui-win-x64.zip")), 0, null);
        var github = new FakeGitHub();
        github.Bytes(asset.Url.ToString(), archive, withLength: false);
        using var u = Make(github, "deveparui");
        var progress = new Recorder();

        await u.InstallAsync(asset, exe, progress);

        Assert.AreEqual("new ui", File.ReadAllText(exe), "the program keeps the name the user gave it");
        Assert.IsFalse(File.Exists(Path.Combine(Path.GetDirectoryName(_dir)!, "escape.txt")));
        CollectionAssert.AreEquivalent(new[] { "deveparui-renamed.exe" }, Directory.GetFileSystemEntries(_dir).Select(Path.GetFileName).ToArray());
        Assert.AreEqual(0, progress.Reports.First().Total, "size unknown");
    }

    [TestMethod]
    public async Task Install_BadDownloads_LeaveTheProgramAlone()
    {
        var exe = Program();
        var good = FakeGitHub.TarGz(("./devepar", "new program"));
        var url = new Uri(FakeGitHub.DownloadUrl("1.0.41", "devepar-linux-x64.tar.gz"));

        async Task<string> MessageFor(UpdateAsset asset, Action<FakeGitHub> setup)
        {
            var github = new FakeGitHub();
            setup(github);
            using var u = Make(github);
            var message = (await Assert.ThrowsExactlyAsync<UpdateException>(() => u.InstallAsync(asset, exe))).Message;
            Assert.AreEqual("old program", File.ReadAllText(exe));
            CollectionAssert.AreEquivalent(new[] { "devepar" }, Directory.GetFileSystemEntries(_dir).Select(Path.GetFileName).ToArray(), "cleaned up");
            return message;
        }

        var asset = new UpdateAsset("devepar-linux-x64.tar.gz", url, good.Length, FakeGitHub.Sha256Digest(good)[7..]);
        StringAssert.Contains(await MessageFor(asset with { Sha256 = new string('0', 64) }, g => g.Bytes(url.ToString(), good)), "damaged");
        StringAssert.Contains(await MessageFor(asset with { Size = good.Length + 10 }, g => g.Bytes(url.ToString(), good)), "stopped early");
        StringAssert.Contains(await MessageFor(asset, g => g.Status(url.ToString(), HttpStatusCode.NotFound)), "GitHub answered 404");
        StringAssert.Contains(await MessageFor(asset, g => g.Fail(url.ToString(), new HttpRequestException("reset"))), "Could not reach GitHub to download devepar-linux-x64.tar.gz");

        var other = FakeGitHub.TarGz(("./something-else", "x"));
        StringAssert.Contains(await MessageFor(asset with { Size = other.Length, Sha256 = null }, g => g.Bytes(url.ToString(), other)), "does not contain devepar");
    }

    [TestMethod]
    public async Task Install_ReadOnlyFolder_SaysToUseAdministratorRights()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // A read-only folder attribute does not stop new files on Windows; Program Files needs elevation, not testable here.
        }

        var exe = Program();
        File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        using var u = Make(new FakeGitHub());
        var e = await Assert.ThrowsExactlyAsync<UpdateException>(() => u.InstallAsync(new UpdateAsset("devepar-linux-x64.tar.gz", new Uri("https://x/a"), 1, null), exe));
        StringAssert.Contains(e.Message, "administrator rights");
        Assert.AreEqual("old program", File.ReadAllText(exe));
    }

    [TestMethod]
    public async Task PrepareThenCompleteStaged_TheNewCopyPutsItselfInPlace()
    {
        var exe = Program();
        var archive = FakeGitHub.TarGz(("./devepar", "new program"));
        var asset = new UpdateAsset("devepar-linux-x64.tar.gz", new Uri(FakeGitHub.DownloadUrl("1.0.41", "devepar-linux-x64.tar.gz")), archive.Length, null);
        var github = new FakeGitHub();
        github.Bytes(asset.Url.ToString(), archive);
        using var u = Make(github);
        var progress = new Recorder();

        var staged = await u.PrepareAsync(asset, exe, progress);

        Assert.AreEqual(Updater.StagingPath(exe, "devepar"), Path.GetDirectoryName(staged));
        Assert.AreEqual("new program", File.ReadAllText(staged));
        Assert.AreEqual("old program", File.ReadAllText(exe), "preparing changes nothing yet");
        Assert.IsFalse(File.Exists(Path.Combine(_dir, ".devepar-update.download")), "the download is gone");
        CollectionAssert.AreEqual(new[] { UpdateStage.Downloading, UpdateStage.Extracting }, progress.Reports.Select(r => r.Stage).Distinct().ToArray());
        if (!OperatingSystem.IsWindows())
        {
            Assert.IsTrue(File.GetUnixFileMode(staged).HasFlag(UnixFileMode.UserExecute), "ready to run");
        }

        Updater.CompleteStaged(staged, exe, waitForProcessId: int.MaxValue, TimeSpan.FromSeconds(1)); // no such process: no wait
        Assert.AreEqual("new program", File.ReadAllText(exe));
        Assert.IsTrue(File.Exists(staged), "copied, not moved: the staged copy is the running program");
        Assert.IsFalse(File.Exists(Updater.OldPath(exe)));

        Updater.RemoveLeftovers(exe, "devepar");
        CollectionAssert.AreEquivalent(new[] { "devepar" }, Directory.GetFileSystemEntries(_dir).Select(Path.GetFileName).ToArray());
    }

    [TestMethod]
    public void CompleteStaged_PutsTheOldProgramBackWhenTheCopyFails()
    {
        var exe = Program();
        Assert.ThrowsExactly<FileNotFoundException>(() => Updater.CompleteStaged(Path.Combine(_dir, "missing"), exe, null, TimeSpan.Zero));
        Assert.AreEqual("old program", File.ReadAllText(exe));
        Assert.IsFalse(File.Exists(Updater.OldPath(exe)));
    }

    [TestMethod]
    public void WaitForExit_WaitsForARunningProcessAndIgnoresAMissingOne()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Updater.WaitForExit(Environment.ProcessId, TimeSpan.FromMilliseconds(50)); // this process keeps running: waits the whole time
        Assert.IsTrue(clock.ElapsedMilliseconds >= 40);
        Updater.WaitForExit(int.MaxValue, TimeSpan.FromMinutes(1)); // not running: returns at once
        Assert.IsTrue(clock.Elapsed < TimeSpan.FromSeconds(30));
    }

    [TestMethod]
    public void Replace_UndoesTheRenameWhenTheNewProgramCannotBeMovedIn()
    {
        var exe = Program();
        var folder = Path.Combine(_dir, "a folder, not a program");
        Directory.CreateDirectory(folder);
        Assert.ThrowsExactly<FileNotFoundException>(() => Updater.Replace(exe, folder));
        Assert.AreEqual("old program", File.ReadAllText(exe));
        Assert.IsFalse(File.Exists(Updater.OldPath(exe)));
    }

    [TestMethod]
    public void Replace_RemovesAnEarlierOldFile()
    {
        var exe = Program();
        File.WriteAllText(Updater.OldPath(exe), "from an earlier update");
        var replacement = Path.Combine(_dir, "new");
        File.WriteAllText(replacement, "new program");
        Updater.Replace(exe, replacement);
        Assert.AreEqual("new program", File.ReadAllText(exe));
        Assert.IsFalse(File.Exists(Updater.OldPath(exe)));
    }

    [TestMethod]
    public void RemoveLeftovers_DeletesWhatAnUpdateLeftBehind()
    {
        var exe = Program();
        File.WriteAllText(exe + ".old", "old");
        File.WriteAllText(Path.Combine(_dir, ".devepar-update.download"), "partial");
        Directory.CreateDirectory(Path.Combine(_dir, ".devepar-update"));
        File.WriteAllText(Path.Combine(_dir, ".devepar-update", "devepar"), "extracted");

        Updater.RemoveLeftovers(exe, "devepar");

        CollectionAssert.AreEquivalent(new[] { "devepar" }, Directory.GetFileSystemEntries(_dir).Select(Path.GetFileName).ToArray());
        Updater.RemoveLeftovers(exe, "devepar"); // nothing left: no error
    }

    [TestMethod]
    public void TryDelete_IgnoresFilesThatCannotBeDeleted()
    {
        var file = Program("busy");
        if (OperatingSystem.IsWindows())
        {
            using var open = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
            Updater.TryDelete(file); // in use, like the running program
        }
        else
        {
            File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            Updater.TryDelete(file); // no permission
            File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.IsTrue(File.Exists(file));
    }

    // ---------------------------------------------------------------- cache

    [TestMethod]
    public void Cache_RoundTripsAndToleratesBadFiles()
    {
        var cache = new UpdateCheckCache(Path.Combine(_dir, "a", "cache.txt"));
        Assert.IsNull(cache.Read(), "no file");
        var at = new DateTimeOffset(2026, 11, 27, 14, 5, 0, TimeSpan.FromHours(1));
        cache.Write(at, new Version(1, 0, 41));
        Assert.AreEqual((at, new Version(1, 0, 41)), cache.Read());

        File.WriteAllText(cache.FilePath, "garbage\nchecked=yesterday\nlatest=1.0.41\nother=1\n");
        Assert.IsNull(cache.Read(), "no valid time");
        File.WriteAllText(cache.FilePath, $"checked={at:O}\nlatest=new\n");
        Assert.IsNull(cache.Read(), "no valid version");

        var unreadable = new UpdateCheckCache(_dir); // a folder
        Assert.IsNull(unreadable.Read());
        File.WriteAllText(Path.Combine(_dir, "file"), "x");
        new UpdateCheckCache(Path.Combine(_dir, "file", "cache.txt")).Write(at, new Version(1, 0, 41)); // the folder is a file: ignored

        StringAssert.EndsWith(UpdateCheckCache.DefaultPath, Path.Combine("DeveParTools", "update-check.txt"));
    }

    [TestMethod]
    public void ReleaseJson_ExtraFieldsAreIgnored()
    {
        var release = Updater.ParseRelease(FakeGitHub.ReleaseJson("1.0.41", ("a.zip", [1], null)));
        Assert.AreEqual("a.zip", release.Assets.Single().Name);
    }
}
