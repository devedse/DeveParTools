using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeveParTools.Updates;

namespace DeveParTools.Cli.Tests;

[TestClass]
public sealed class UpdateCommandTests
{
    private const string LatestUrl = "https://api.github.com/repos/devedse/DeveParTools/releases/latest";
    private string _dir = null!;
    private Fake _github = null!;
    private string _exe = null!;

    /// <summary>A stand-in for GitHub's API and downloads.</summary>
    private sealed class Fake : HttpMessageHandler
    {
        public Dictionary<string, Func<HttpResponseMessage>> Routes { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Routes.TryGetValue(request.RequestUri!.ToString(), out var r) ? r() : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    [TestInitialize]
    public void Init()
    {
        _dir = Path.Combine(Path.GetTempPath(), "devepar-cli-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _exe = Path.Combine(_dir, OperatingSystem.IsWindows() ? "devepar.exe" : "devepar");
        File.WriteAllText(_exe, "old program");
        _github = new Fake();
        CliApp.NewUpdater = () => new Updater("devepar", new Version(1, 0, 40), _github) { RuntimeId = "linux-x64" };
        CliApp.ProcessPath = () => _exe;
        CliApp.Cache = () => new UpdateCheckCache(Path.Combine(_dir, "cache.txt"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        CliApp.NewUpdater = () => new Updater("devepar", CliApp.Version);
        CliApp.ProcessPath = () => Environment.ProcessPath;
        CliApp.Cache = () => new UpdateCheckCache(UpdateCheckCache.DefaultPath);
        Directory.Delete(_dir, true);
    }

    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int code = CliApp.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    private void Release(string tag, byte[]? archive = null, string asset = "devepar-linux-x64.tar.gz")
    {
        var assets = new List<object>();
        if (archive != null)
        {
            string url = $"https://github.com/devedse/DeveParTools/releases/download/{tag}/{asset}";
            assets.Add(new Dictionary<string, object?> { ["name"] = asset, ["browser_download_url"] = url, ["size"] = archive.Length, ["digest"] = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(archive)) });
            _github.Routes[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) };
        }

        var json = JsonSerializer.Serialize(new Dictionary<string, object?> { ["tag_name"] = tag, ["html_url"] = $"https://github.com/devedse/DeveParTools/releases/tag/{tag}", ["assets"] = assets });
        _github.Routes[LatestUrl] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
    }

    private static byte[] TarGz(string name, string content)
    {
        using var ms = new MemoryStream();
        using (var gzip = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)) });
        }

        return ms.ToArray();
    }

    [TestMethod]
    public void Check_SaysWhetherANewerVersionExists()
    {
        Release("1.0.41");
        var newer = Run("update", "--check");
        Assert.AreEqual(ExitCodes.Success, newer.Code, newer.Err);
        StringAssert.Contains(newer.Out, "devepar 1.0.41 is available (you have 1.0.40). Run \"devepar update\" to install it.");
        StringAssert.Contains(newer.Out, "What's new: https://github.com/devedse/DeveParTools/releases/tag/1.0.41");
        Assert.AreEqual("old program", File.ReadAllText(_exe), "only checked");
        StringAssert.Contains(File.ReadAllText(Path.Combine(_dir, "cache.txt")), "latest=1.0.41", "remembered for the notice");

        Release("1.0.40");
        var same = Run("update", "--check");
        StringAssert.Contains(same.Out, "devepar 1.0.40 is the latest version.");
        var installSame = Run("update");
        StringAssert.Contains(installSame.Out, "devepar 1.0.40 is the latest version.");
        Assert.AreEqual("old program", File.ReadAllText(_exe));

        var json = Run("update", "--check", "--json");
        StringAssert.Contains(json.Out, "\"type\":\"update\",\"current\":\"1.0.40\",\"latest\":\"1.0.40\",\"updateAvailable\":false");
    }

    [TestMethod]
    public void Update_ReplacesTheProgram()
    {
        Release("1.0.41", TarGz("./devepar", "new program"));
        var r = Run("update");
        Assert.AreEqual(ExitCodes.Success, r.Code, r.Err);
        StringAssert.Contains(r.Out, "Downloading devepar-linux-x64.tar.gz (0.0 MB)...");
        StringAssert.Contains(r.Out, "Extracting...");
        StringAssert.Contains(r.Out, "Installing...");
        StringAssert.Contains(r.Out, $"Updated {_exe} from 1.0.40 to 1.0.41.");
        Assert.AreEqual("new program", File.ReadAllText(_exe));

        Release("1.0.42", TarGz("./devepar", "newer program"));
        var json = Run("update", "--json");
        StringAssert.Contains(json.Out, "\"type\":\"stage\",\"name\":\"Extracting\"");
        StringAssert.Contains(json.Out, "\"type\":\"updated\"");
        Assert.AreEqual("newer program", File.ReadAllText(_exe));
    }

    [TestMethod]
    public void Update_ExplainsWhyItCannot()
    {
        CliApp.ProcessPath = () => null;
        var dotnet = Run("update");
        Assert.AreEqual(ExitCodes.LogicError, dotnet.Code);
        StringAssert.Contains(dotnet.Err, "runs through dotnet");

        CliApp.ProcessPath = () => _exe;
        Release("1.0.41", TarGz("./devepar", "x"), asset: "devepar-win-x64.zip");
        var noFile = Run("update");
        Assert.AreEqual(ExitCodes.LogicError, noFile.Code);
        StringAssert.Contains(noFile.Err, "Release 1.0.41 has no devepar for this system (linux-x64)");

        _github.Routes[LatestUrl] = () => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var down = Run("update", "--check");
        Assert.AreEqual(ExitCodes.LogicError, down.Code);
        StringAssert.Contains(down.Err, "GitHub answered 500");
        Assert.AreEqual("old program", File.ReadAllText(_exe));
    }

    [TestMethod]
    public void Notice_OnlyForPeopleAndNotForUpdateItself()
    {
        var root = CliApp.Build(TextWriter.Null, TextWriter.Null);
        string? None(string _) => null;
        Assert.IsTrue(CliApp.WantsUpdateNotice(root.Parse(["info"]), false, None));
        Assert.IsFalse(CliApp.WantsUpdateNotice(root.Parse(["info"]), true, None), "standard error redirected");
        Assert.IsFalse(CliApp.WantsUpdateNotice(root.Parse(["info", "--json"]), false, None));
        Assert.IsFalse(CliApp.WantsUpdateNotice(root.Parse(["info", "-q"]), false, None));
        Assert.IsFalse(CliApp.WantsUpdateNotice(root.Parse(["update"]), false, None));
        Assert.IsFalse(CliApp.WantsUpdateNotice(root.Parse(["verify"]), false, None), "a command line with errors");
        Assert.IsFalse(CliApp.WantsUpdateNotice(root.Parse(["info"]), false, n => n == "DEVEPAR_NO_UPDATE_CHECK" ? "1" : null));
        Assert.IsFalse(CliApp.WantsUpdateNotice(root.Parse(["info"]), false, n => n == "CI" ? "true" : null));
        Assert.IsTrue(CliApp.WantsUpdateNotice(root.Parse(["info"]), false, n => n == "DEVEPAR_NO_UPDATE_CHECK" ? "0" : n == "CI" ? "false" : null));

        Assert.AreEqual("devepar 1.0.41 is available (you have 1.0.40). Run \"devepar update\" to install it.", CliApp.Notice(new Version(1, 0, 40), new Version(1, 0, 41)));
        Assert.AreEqual("28.5 MB", CliApp.Megabytes(28_470_065));
    }

    [TestMethod]
    public void WaitQuietly_FalseForSlowOrFailedChecks()
    {
        Assert.IsTrue(CliApp.WaitQuietly(Task.CompletedTask, TimeSpan.Zero));
        Assert.IsFalse(CliApp.WaitQuietly(Task.Delay(Timeout.Infinite), TimeSpan.FromMilliseconds(1)));
        Assert.IsFalse(CliApp.WaitQuietly(Task.FromException(new IOException()), TimeSpan.Zero));
    }

    [TestMethod]
    public void RunProgram_RemovesWhatAnEarlierUpdateLeft()
    {
        File.WriteAllText(_exe + ".old", "the previous version");
        var version = CliApp.Version;
        try
        {
            CliApp.Version = new Version(1, 0, 40); // a released build (local builds are 1.0.0)
            Assert.AreEqual(ExitCodes.Success, CliApp.RunProgram(["info", "--json"]));
        }
        finally
        {
            CliApp.Version = version;
        }

        Assert.IsFalse(File.Exists(_exe + ".old"));
    }
}
