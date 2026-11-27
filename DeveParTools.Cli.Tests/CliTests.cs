using System.Text.Json;
using DeveParTools.Core.Operations;

namespace DeveParTools.Cli.Tests;

[TestClass]
public sealed class CliTests
{
    private string _dir = null!;

    [TestInitialize]
    public void Init()
    {
        _dir = Path.Combine(Path.GetTempPath(), "devepar-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private (int Code, string Out, string Err) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int code = CliApp.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    private string MakeFile(string name, int size, int seed = 1)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static List<JsonElement> Events(string ndjson) =>
        ndjson.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();

    [TestMethod]
    public void CreateVerifyRepair_Par2_ExitCodesFollowPar2cmdline()
    {
        var a = MakeFile("a.bin", 60_000);
        var b = MakeFile("b.bin", 25_000, 2);
        var par2 = Path.Combine(_dir, "set.par2");

        var create = Run("create", par2, a, b, "-r", "20", "-b", "50", "--comment", "cli test");
        Assert.AreEqual(ExitCodes.Success, create.Code, create.Err);
        StringAssert.Contains(create.Out, "Created Par2 set");

        var ok = Run("verify", par2);
        Assert.AreEqual(ExitCodes.Success, ok.Code, ok.Err);
        StringAssert.Contains(ok.Out, "All files are correct");
        StringAssert.Contains(ok.Out, "Comment: cli test");

        var bytes = File.ReadAllBytes(a);
        Array.Clear(bytes, 5000, 4000);
        File.WriteAllBytes(a, bytes);
        var damaged = Run("v", par2);
        Assert.AreEqual(ExitCodes.RepairPossible, damaged.Code);
        StringAssert.Contains(damaged.Out, "Repair is possible");

        var list = Run("list", par2);
        Assert.AreEqual(ExitCodes.Success, list.Code);
        StringAssert.Contains(list.Out, "a.bin");

        var repaired = Run("repair", par2, "--purge", "--map");
        Assert.AreEqual(ExitCodes.Success, repaired.Code, repaired.Err);
        Assert.AreEqual(ExitCodes.Success, Run("verify", par2, "-q").Code);
        Assert.IsFalse(File.Exists(a + ".1"), "--purge leaves no backup");

        // Repair of a complete set does nothing and succeeds.
        Assert.AreEqual(ExitCodes.Success, Run("r", par2).Code);
    }

    [TestMethod]
    public void Verify_TooMuchDamage_RepairNotPossible()
    {
        var a = MakeFile("x.bin", 40_000);
        var par2 = Path.Combine(_dir, "x.par2");
        Assert.AreEqual(0, Run("create", par2, a, "-c", "1", "-b", "40").Code);
        File.Delete(a);
        var verify = Run("verify", par2);
        Assert.AreEqual(ExitCodes.RepairNotPossible, verify.Code);
        StringAssert.Contains(verify.Out, "Repair is NOT possible");
        Assert.AreEqual(ExitCodes.RepairNotPossible, Run("repair", par2).Code);
    }

    [TestMethod]
    public void Par3_FolderWithPar3Options_AndExtraFiles()
    {
        MakeFile("tree/one.bin", 30_000);
        MakeFile("tree/sub/two.bin", 12_345, 3);
        var par3 = Path.Combine(_dir, "tree.par3");
        var create = Run("create", par3, Path.Combine(_dir, "tree"), "-B", _dir, "-r", "25", "-b", "40", "--code", "cauchy", "--dedup", "--reproducible", "--unix-permissions", "-n", "2", "--uniform");
        Assert.AreEqual(0, create.Code, create.Err);

        // Move a file away: it is found again when passed as an extra file.
        var two = Path.Combine(_dir, "tree", "sub", "two.bin");
        var moved = Path.Combine(_dir, "elsewhere.bin");
        File.Move(two, moved);
        var verify = Run("verify", par3, moved, "-B", _dir, "--json");
        var events = Events(verify.Out);
        var result = events.Single(e => e.GetProperty("type").GetString() == "verified");
        Assert.AreEqual("Par3", result.GetProperty("format").GetString());
        Assert.IsTrue(result.GetProperty("repairPossible").GetBoolean());
        Assert.IsTrue(result.GetProperty("files").EnumerateArray().Any(f => f.GetProperty("name").GetString()!.EndsWith("two.bin", StringComparison.Ordinal)));
        Assert.IsTrue(events.Any(e => e.GetProperty("type").GetString() == "stage"));

        var repair = Run("repair", par3, moved, "-B", _dir, "--restore-permissions", "--restore-links", "--json", "--map");
        Assert.AreEqual(0, repair.Code, repair.Out + repair.Err);
        var repairEvents = Events(repair.Out);
        Assert.IsTrue(repairEvents.Single(e => e.GetProperty("type").GetString() == "repaired").GetProperty("success").GetBoolean());
        Assert.IsTrue(repairEvents.Any(e => e.GetProperty("type").GetString() == "blockmap"));
        Assert.IsTrue(File.Exists(two));
    }

    [TestMethod]
    [DataRow("par1")]
    [DataRow("PAR2")]
    [DataRow("par3")]
    public void Create_FormatOption_OverridesExtension(string format)
    {
        var f = MakeFile("in.bin", 5000);
        var output = Path.Combine(_dir, "out.recovery");
        var create = Run("create", output, f, "--format", format, "--json", "-t", "2", "-m", "64");
        Assert.AreEqual(0, create.Code, create.Out + create.Err);
        var result = Events(create.Out).Single(e => e.GetProperty("type").GetString() == "created");
        Assert.AreEqual(format, result.GetProperty("format").GetString(), ignoreCase: true);
        Assert.IsTrue(result.GetProperty("files").GetArrayLength() >= 1);
    }

    [TestMethod]
    public void Create_Par3Fft_WithInterleaveAndSpecCauchy()
    {
        var f = MakeFile("big.bin", 200_000);
        var create = Run("create", Path.Combine(_dir, "fft.par3"), f, "--code", "fft", "-b", "100", "-c", "10", "-i", "1", "--max-recovery", "20", "--no-tail-packing", "--data", "--fat-permissions", "--no-recurse", "--first", "0", "-s", "2048");
        Assert.AreEqual(0, create.Code, create.Err);
        StringAssert.Contains(create.Out, "FFT");
        var spec = Run("create", Path.Combine(_dir, "spec.par3"), f, "--code", "cauchy", "--spec-cauchy", "-c", "3");
        Assert.AreEqual(0, spec.Code, spec.Err);
        Assert.AreEqual(0, Run("verify", Path.Combine(_dir, "spec.par3")).Code);
    }

    [TestMethod]
    [DataRow("--format", "par9")]
    [DataRow("--code", "turbo")]
    public void Create_UnknownChoice_IsInvalidArguments(string option, string value)
    {
        var f = MakeFile("i.bin", 100);
        var r = Run("create", Path.Combine(_dir, "o.par3"), f, option, value);
        Assert.AreEqual(ExitCodes.InvalidArguments, r.Code);
        StringAssert.Contains(r.Err, "Unknown");
    }

    [TestMethod]
    public void Errors_MapToExitCodes()
    {
        // A file that is not a PAR file: insufficient critical data.
        var junk = MakeFile("junk.par2", 100);
        var notPar = Run("verify", junk, "--json");
        Assert.AreEqual(ExitCodes.InsufficientCriticalData, notPar.Code);
        Assert.AreEqual("error", Events(notPar.Out).Last().GetProperty("type").GetString());

        // Input file that does not exist: I/O error.
        var missingInput = Run("create", Path.Combine(_dir, "m.par2"), Path.Combine(_dir, "nope.bin"));
        Assert.AreEqual(ExitCodes.FileIoError, missingInput.Code, missingInput.Out + missingInput.Err);
        StringAssert.Contains(missingInput.Err, "Error:");

        // Parse errors from System.CommandLine.
        Assert.AreNotEqual(0, Run("verify").Code);
        Assert.AreNotEqual(0, Run("frobnicate").Code);
    }

    [TestMethod]
    public void Guard_MapsEveryExceptionKind()
    {
        Assert.AreEqual(ExitCodes.InvalidArguments, GuardThrowing(new ArgumentException("a")));
        Assert.AreEqual(ExitCodes.InvalidArguments, GuardThrowing(new FormatException("f")));
        Assert.AreEqual(ExitCodes.InsufficientCriticalData, GuardThrowing(new InvalidDataException("d")));
        Assert.AreEqual(ExitCodes.FileIoError, GuardThrowing(new IOException("io")));
        Assert.AreEqual(ExitCodes.FileIoError, GuardThrowing(new UnauthorizedAccessException("ua")));
        Assert.AreEqual(ExitCodes.MemoryError, GuardThrowing(new OutOfMemoryException("oom")));
        Assert.AreEqual(ExitCodes.LogicError, GuardThrowing(new InvalidOperationException("bug")));

        static int GuardThrowing(Exception e)
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var common = new Common(false, false, false, null, null, output, error);
            int code = CliApp.Guard(common, _ => throw e);
            StringAssert.Contains(error.ToString(), "Error:");
            return code;
        }
    }

    [TestMethod]
    public void InfoAndBench()
    {
        var info = Run("info", "--json");
        Assert.AreEqual(0, info.Code);
        var json = Events(info.Out).Single();
        Assert.AreEqual("info", json.GetProperty("type").GetString());
        Assert.IsTrue(json.GetProperty("cores").GetInt32() > 0);

        var text = Run("info");
        StringAssert.Contains(text.Out, "Kernel");

        var bench = Run("bench", "--size", "4", "--time", "3");
        Assert.AreEqual(0, bench.Code, bench.Err);
        StringAssert.Contains(bench.Out, "BLAKE3");
        StringAssert.Contains(bench.Out, "GB/s");

        var benchJson = Run("bench", "--size", "4", "--time", "3", "--json");
        var result = Events(benchJson.Out).Single(e => e.GetProperty("type").GetString() == "bench");
        Assert.IsTrue(result.GetProperty("blake3").GetDouble() > 0);
    }

    [TestMethod]
    public void Common_TextAndJsonOutput()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using (var text = new Common(false, false, false, 3, 128, output, error))
        {
            Assert.AreEqual(3, text.Context.Threads);
            Assert.AreEqual(128L << 20, text.Context.MemoryBudget);
            text.Stage("Scanning");
            text.Progress(0.5, "half");
            text.Log(ParLogLevel.Info, "info line");
            text.Log(ParLogLevel.Warning, "careful");
        }

        StringAssert.Contains(output.ToString(), "Scanning...");
        StringAssert.Contains(output.ToString(), "info line");
        StringAssert.Contains(error.ToString(), "Warning: careful");

        using var quietOut = new StringWriter();
        using var quietErr = new StringWriter();
        using (var quiet = new Common(false, false, true, null, null, quietOut, quietErr))
        {
            quiet.Stage("hidden");
            quiet.Log(ParLogLevel.Info, "hidden too");
            quiet.Log(ParLogLevel.Error, "shown");
        }

        Assert.AreEqual(string.Empty, quietOut.ToString());
        StringAssert.Contains(quietErr.ToString(), "Error: shown");

        using var jsonOut = new StringWriter();
        using (var json = new Common(true, true, false, null, null, jsonOut, TextWriter.Null))
        {
            json.Stage("S");
            json.Progress(1, "done");
            json.Progress(0.1, null);
            json.Log(ParLogLevel.Debug, "dbg");
            json.Result("custom", new Dictionary<string, object?> { ["n"] = 1, ["s"] = "x", ["b"] = true, ["l"] = new List<string> { "a" }, ["nil"] = null, ["d"] = 1.5 }, "ignored");
        }

        var events = Events(jsonOut.ToString());
        CollectionAssert.AreEqual(new[] { "stage", "progress", "log", "custom" }, events.Select(e => e.GetProperty("type").GetString()).ToList());
        var custom = events[^1];
        Assert.AreEqual(JsonValueKind.Null, custom.GetProperty("nil").ValueKind);
        Assert.AreEqual("a", custom.GetProperty("l")[0].GetString());
    }

    [TestMethod]
    public void TerminalBlockMap_RendersEveryState()
    {
        var states = Enum.GetValues<BlockState>();
        var map = new BlockStateMap(states.Length * 3, 2);
        for (int i = 0; i < map.InputBlockCount; i++)
        {
            map.Set(i, states[i % states.Length]);
        }

        var plain = TerminalBlockMap.Render(map, 40, useColor: false);
        var color = TerminalBlockMap.Render(map, 40, useColor: true);
        Assert.IsFalse(plain.Contains('\u001b', StringComparison.Ordinal));
        StringAssert.Contains(color, "\u001b[");
        foreach (var s in states)
        {
            Assert.IsTrue(plain.Contains(TerminalBlockMap.Style(s).Glyph, StringComparison.Ordinal), s.ToString());
        }

        // Many blocks are grouped; a group shows its worst state.
        var big = new BlockStateMap(100_000, 0);
        big.Set(12345, BlockState.Missing);
        var grouped = TerminalBlockMap.Render(big, 20, useColor: false);
        Assert.IsTrue(grouped.Contains(TerminalBlockMap.Style(BlockState.Missing).Glyph, StringComparison.Ordinal));
        Assert.AreEqual(string.Empty, TerminalBlockMap.Render(new BlockStateMap(0, 0), 20, false));
    }
}
