using System.CommandLine;
using DeveParTools;
using DeveParTools.Cli;
using DeveParTools.Core.Operations;
using DeveParTools.Par3;

return CliApp.RunProgram(args);

namespace DeveParTools.Cli
{
    /// <summary>Exit codes, identical to par2cmdline and par3cmdline.</summary>
    public static class ExitCodes
    {
        public const int Success = 0;
        public const int RepairPossible = 1;
        public const int RepairNotPossible = 2;
        public const int InvalidArguments = 3;
        public const int InsufficientCriticalData = 4;
        public const int RepairFailed = 5;
        public const int FileIoError = 6;
        public const int LogicError = 7;
        public const int MemoryError = 8;
    }

    public static partial class CliApp
    {
        public static int Run(string[] args, TextWriter? output = null, TextWriter? error = null)
        {
            output ??= Console.Out;
            error ??= Console.Error;
            var root = Build(output, error);
            var config = new InvocationConfiguration { Output = output, Error = error };
            return root.Parse(args).Invoke(config);
        }

        internal static RootCommand Build(TextWriter output, TextWriter error)
        {
            var json = new Option<bool>("--json") { Description = "Write machine-readable NDJSON events instead of text." };
            var map = new Option<bool>("--map") { Description = "Show a block map (found / moved / missing / repaired blocks)." };
            var quiet = new Option<bool>("--quiet", "-q") { Description = "Only print errors and the final result." };
            var threads = new Option<int?>("--threads", "-t") { Description = "Number of threads (default: all cores)." };
            var memory = new Option<int?>("--memory", "-m") { Description = "Memory budget for coding buffers in MiB." };

            var root = new RootCommand("devepar: create, verify and repair PAR1 / PAR2 / PAR3 recovery sets.");
            foreach (var o in new Option[] { json, map, quiet, threads, memory })
            {
                o.Recursive = true;
                root.Options.Add(o);
            }

            Common Ctx(ParseResult p) => new(p.GetValue(json), p.GetValue(map), p.GetValue(quiet), p.GetValue(threads), p.GetValue(memory), output, error);

            // ---------------------------------------------------------------- create
            var create = new Command("create", "Create a recovery set. The output extension (.par2, .par3, .par) selects the format.");
            create.Aliases.Add("c");
            var outArg = new Argument<string>("output") { Description = "Index file to write, e.g. set.par2 or set.par3." };
            var inputsArg = new Argument<string[]>("inputs") { Description = "Files and directories to protect.", Arity = ArgumentArity.OneOrMore };
            var redundancy = new Option<double?>("--redundancy", "-r") { Description = "Recovery data as a percentage of the input (default 10)." };
            var recoveryCount = new Option<int?>("--recovery-count", "-c") { Description = "Exact number of recovery blocks." };
            var blockCount = new Option<int?>("--block-count", "-b") { Description = "Target number of input blocks (default 2000)." };
            var blockSize = new Option<long?>("--block-size", "-s") { Description = "Block size in bytes (multiple of 4 for PAR2, 8 for PAR3)." };
            var first = new Option<int>("--first") { Description = "First recovery block number (exponent)." };
            var volumes = new Option<int?>("--volumes", "-n") { Description = "Number of recovery files." };
            var uniform = new Option<bool>("--uniform", "-u") { Description = "Same number of recovery blocks in every recovery file (PAR2, PAR3)." };
            var limit = new Option<bool>("--limit", "-l") { Description = "No recovery file larger than the largest input file (PAR2, PAR3)." };
            var comment = new Option<string?>("--comment") { Description = "Comment stored in the set." };
            var basePath = new Option<string?>("--base", "-B") { Description = "Base directory that stored names are relative to." };
            var noRecurse = new Option<bool>("--no-recurse") { Description = "Do not descend into sub-directories." };
            var format = new Option<string?>("--format", "-f") { Description = "Force the format: par1, par2 or par3." };
            var code = new Option<string>("--code") { Description = "PAR3 code: auto, cauchy or fft.", DefaultValueFactory = _ => "auto" };
            var dedup = new Option<bool>("--dedup") { Description = "PAR3: store identical blocks once." };
            var noPack = new Option<bool>("--no-tail-packing") { Description = "PAR3: do not pack file tails together." };
            var storeData = new Option<bool>("--data", "-D") { Description = "PAR3, rarely needed: also store a full copy of the files in the set (.part files), so it can recreate them without the originals. Only for archives and files you send; it doubles the size." };
            var unixPerms = new Option<bool>("--unix-permissions") { Description = "PAR3: store mtime and mode." };
            var fatPerms = new Option<bool>("--fat-permissions") { Description = "PAR3: store FAT times and attributes." };
            var reproducible = new Option<bool>("--reproducible") { Description = "PAR3: identical inputs give identical output." };
            var interleave = new Option<int>("--interleave", "-i") { Description = "PAR3 FFT: number of extra cohorts (0 = automatic)." };
            var maxRecovery = new Option<int?>("--max-recovery") { Description = "PAR3 FFT: most recovery blocks the set may ever have." };
            var specCauchy = new Option<bool>("--spec-cauchy") { Description = "PAR3: Cauchy x = I+1 (spec text) instead of par3cmdline's x = I." };
            foreach (var o in new Option[] { redundancy, recoveryCount, blockCount, blockSize, first, volumes, uniform, limit, comment, basePath, noRecurse, format, code, dedup, noPack, storeData, unixPerms, fatPerms, reproducible, interleave, maxRecovery, specCauchy })
            {
                create.Options.Add(o);
            }

            create.Arguments.Add(outArg);
            create.Arguments.Add(inputsArg);
            create.SetAction(p => Guard(Ctx(p), c =>
            {
                ParFormat? fmt = p.GetValue(format)?.ToLowerInvariant() switch
                {
                    null => null,
                    "par1" => ParFormat.Par1,
                    "par2" => ParFormat.Par2,
                    "par3" => ParFormat.Par3,
                    var f => throw new ArgumentException($"Unknown format '{f}'."),
                };
                var request = new ParCreateRequest
                {
                    Inputs = p.GetValue(inputsArg)!,
                    OutputPath = p.GetValue(outArg)!,
                    Format = fmt,
                    BaseDirectory = p.GetValue(basePath),
                    RedundancyPercent = p.GetValue(redundancy) ?? 10,
                    RecoveryBlockCount = p.GetValue(recoveryCount),
                    BlockCount = p.GetValue(blockCount),
                    BlockSize = p.GetValue(blockSize),
                    FirstRecoveryBlock = p.GetValue(first),
                    VolumeCount = p.GetValue(volumes),
                    UniformVolumes = p.GetValue(uniform),
                    LimitVolumeSize = p.GetValue(limit),
                    Comment = p.GetValue(comment),
                    Recursive = !p.GetValue(noRecurse),
                    Par3Code = p.GetValue(code)!.ToLowerInvariant() switch
                    {
                        "auto" => Par3Code.Auto,
                        "cauchy" => Par3Code.Cauchy,
                        "fft" => Par3Code.Fft,
                        var x => throw new ArgumentException($"Unknown code '{x}'."),
                    },
                    Par3Dedup = p.GetValue(dedup) ? Par3Dedup.Blocks : Par3Dedup.None,
                    Par3PackTails = !p.GetValue(noPack),
                    Par3StoreData = p.GetValue(storeData),
                    Par3UnixPermissions = p.GetValue(unixPerms),
                    Par3FatPermissions = p.GetValue(fatPerms),
                    Par3Reproducible = p.GetValue(reproducible),
                    Par3Interleave = p.GetValue(interleave),
                    Par3MaxRecoveryBlocks = p.GetValue(maxRecovery),
                    Par3CauchyConvention = p.GetValue(specCauchy) ? Par3CauchyConvention.Specification : Par3CauchyConvention.Par3cmdline,
                };
                var report = ParClient.Create(request, c.Context);
                c.Result("created", new Dictionary<string, object?>
                {
                    ["format"] = report.Format.ToString(),
                    ["blockSize"] = report.BlockSize,
                    ["inputBlocks"] = report.InputBlocks,
                    ["recoveryBlocks"] = report.RecoveryBlocks,
                    ["code"] = report.Description,
                    ["files"] = report.WrittenFiles,
                }, $"Created {report.Format} set: {report.InputBlocks} input block(s) of {report.BlockSize} bytes, {report.RecoveryBlocks} recovery block(s), {report.Description}.\n" +
                   string.Join('\n', report.WrittenFiles.Select(f => "  " + f)));
                return ExitCodes.Success;
            }));

            // ---------------------------------------------------------------- verify / repair
            var parArg = new Argument<string>("parfile") { Description = "A .par, .par2 or .par3 file of the set." };
            var extraArg = new Argument<string[]>("extra") { Description = "Extra files to search for data (renamed or partial files).", Arity = ArgumentArity.ZeroOrMore };
            var verifyBase = new Option<string?>("--base", "-B") { Description = "Base directory of the protected files." };

            var verify = new Command("verify", "Verify the files of a recovery set.");
            verify.Aliases.Add("v");
            verify.Arguments.Add(parArg);
            verify.Arguments.Add(extraArg);
            verify.Options.Add(verifyBase);
            verify.SetAction(p => Guard(Ctx(p), c =>
            {
                var report = ParClient.Verify(p.GetValue(parArg)!, new ParVerifyRequest { ExtraFiles = p.GetValue(extraArg) ?? [], BaseDirectory = p.GetValue(verifyBase) }, c.Context);
                c.PrintReport(report);
                return report.AllFilesComplete ? ExitCodes.Success : report.CriticalDataMissing ? ExitCodes.InsufficientCriticalData : report.RepairPossible ? ExitCodes.RepairPossible : ExitCodes.RepairNotPossible;
            }));

            var purge = new Option<bool>("--purge", "-p") { Description = "Delete damaged originals instead of keeping backups." };
            var restorePerms = new Option<bool>("--restore-permissions") { Description = "PAR3: apply stored permissions and times." };
            var restoreLinks = new Option<bool>("--restore-links") { Description = "PAR3: recreate stored links (inside the base directory only)." };
            var repair = new Command("repair", "Verify and repair the files of a recovery set.");
            repair.Aliases.Add("r");
            repair.Arguments.Add(parArg);
            repair.Arguments.Add(extraArg);
            foreach (var o in new Option[] { verifyBase, purge, restorePerms, restoreLinks })
            {
                repair.Options.Add(o);
            }

            repair.SetAction(p => Guard(Ctx(p), c =>
            {
                var report = ParClient.Verify(p.GetValue(parArg)!, new ParVerifyRequest { ExtraFiles = p.GetValue(extraArg) ?? [], BaseDirectory = p.GetValue(verifyBase) }, c.Context);
                c.PrintReport(report);
                if (report.AllFilesComplete)
                {
                    return ExitCodes.Success;
                }

                if (report.CriticalDataMissing)
                {
                    return ExitCodes.InsufficientCriticalData;
                }

                if (!report.RepairPossible)
                {
                    return ExitCodes.RepairNotPossible;
                }

                var result = ParClient.Repair(report, new ParRepairRequest { PurgeBackups = p.GetValue(purge), RestorePermissions = p.GetValue(restorePerms), RestoreLinks = p.GetValue(restoreLinks) }, c.Context);
                c.ShowMap();
                c.Result("repaired", new Dictionary<string, object?> { ["success"] = result.Success, ["message"] = result.Message, ["repaired"] = result.RepairedFiles, ["failed"] = result.FailedFiles }, result.Message);
                return result.Success ? ExitCodes.Success : ExitCodes.RepairFailed;
            }));

            // ---------------------------------------------------------------- extend
            var extendCount = new Option<int?>("--recovery-count", "-c") { Description = "Number of new recovery blocks." };
            var extendRedundancy = new Option<double?>("--redundancy", "-r") { Description = "New recovery data as a percentage of the input (default 10)." };
            var extendFirst = new Option<long?>("--first") { Description = "Number of the first new recovery block (default: after the existing ones)." };
            var extendVolumes = new Option<int?>("--volumes", "-n") { Description = "Number of new recovery files." };
            var extendOutput = new Option<string?>("--output", "-o") { Description = "Base name for the new recovery files (default: the set's index file)." };
            var extend = new Command("extend", "Add recovery blocks to an existing set (all input files must be present).");
            extend.Aliases.Add("e");
            extend.Arguments.Add(parArg);
            extend.Arguments.Add(extraArg);
            foreach (var o in new Option[] { verifyBase, extendCount, extendRedundancy, extendFirst, extendVolumes, extendOutput })
            {
                extend.Options.Add(o);
            }

            extend.SetAction(p => Guard(Ctx(p), c =>
            {
                var report = ParClient.Extend(new ParExtendRequest
                {
                    ParFile = p.GetValue(parArg)!,
                    ExtraFiles = p.GetValue(extraArg) ?? [],
                    BaseDirectory = p.GetValue(verifyBase),
                    RecoveryBlockCount = p.GetValue(extendCount),
                    RedundancyPercent = p.GetValue(extendRedundancy) ?? 10,
                    FirstRecoveryBlock = p.GetValue(extendFirst),
                    VolumeCount = p.GetValue(extendVolumes),
                    OutputPath = p.GetValue(extendOutput),
                }, c.Context);
                c.Result("extended", new Dictionary<string, object?>
                {
                    ["format"] = report.Format.ToString(),
                    ["recoveryBlocks"] = report.RecoveryBlocks,
                    ["code"] = report.Description,
                    ["files"] = report.WrittenFiles,
                }, $"Added {report.RecoveryBlocks} recovery block(s) ({report.Description}).\n" + string.Join('\n', report.WrittenFiles.Select(f => "  " + f)));
                return ExitCodes.Success;
            }));

            // ---------------------------------------------------------------- PAR inside archives
            var archiveArg = new Argument<string>("archive") { Description = "A .zip, .7z or .rar file." };
            var insertRedundancy = new Option<double?>("--redundancy", "-r") { Description = "Recovery data as a percentage (default 1)." };
            var insertCount = new Option<int?>("--recovery-count", "-c") { Description = "Exact number of recovery blocks." };
            var insertBlockSize = new Option<long?>("--block-size", "-s") { Description = "Block size in bytes (default: chosen automatically)." };
            var insert = new Command("insert", "Put PAR3 recovery data inside a .zip/.7z/.rar so it can repair itself (verify/repair it like a PAR file).");
            insert.Aliases.Add("i");
            insert.Arguments.Add(archiveArg);
            foreach (var o in new Option[] { insertRedundancy, insertCount, insertBlockSize })
            {
                insert.Options.Add(o);
            }

            insert.SetAction(p => Guard(Ctx(p), c =>
            {
                var r = ParClient.Insert(new ParInsertRequest
                {
                    ArchivePath = p.GetValue(archiveArg)!,
                    RedundancyPercent = p.GetValue(insertRedundancy) ?? 1,
                    RecoveryBlockCount = p.GetValue(insertCount),
                    BlockSize = p.GetValue(insertBlockSize),
                }, c.Context);
                c.Result("inserted", new Dictionary<string, object?>
                {
                    ["format"] = r.ArchiveFormat,
                    ["originalLength"] = r.OriginalLength,
                    ["newLength"] = r.NewLength,
                    ["blockSize"] = r.BlockSize,
                    ["inputBlocks"] = r.InputBlocks,
                    ["recoveryBlocks"] = r.RecoveryBlocks,
                }, $"Added {r.RecoveryBlocks} recovery block(s) of {r.BlockSize} bytes inside the {r.ArchiveFormat} archive ({r.OriginalLength:N0} → {r.NewLength:N0} bytes).");
                return ExitCodes.Success;
            }));

            var delete = new Command("delete", "Remove PAR3 data from inside an archive, restoring the original bytes (repair first if damaged).");
            delete.Aliases.Add("d");
            delete.Arguments.Add(archiveArg);
            delete.SetAction(p => Guard(Ctx(p), c =>
            {
                long length = ParClient.RemoveInside(p.GetValue(archiveArg)!, c.Context);
                c.Result("deleted", new Dictionary<string, object?> { ["length"] = length }, $"Removed the PAR3 data; the archive is back to {length:N0} bytes.");
                return ExitCodes.Success;
            }));

            // ---------------------------------------------------------------- list
            var list = new Command("list", "List the files of a recovery set (and their state).");
            list.Aliases.Add("l");
            list.Arguments.Add(parArg);
            list.SetAction(p => Guard(Ctx(p), c =>
            {
                var report = ParClient.Verify(p.GetValue(parArg)!, new ParVerifyRequest(), c.Context);
                c.PrintReport(report, listOnly: true);
                return ExitCodes.Success;
            }));

            // ---------------------------------------------------------------- bench / info
            var size = new Option<int>("--size") { Description = "Buffer size in KiB.", DefaultValueFactory = _ => 256 };
            var bench = new Command("bench", "Measure the speed of the SIMD kernels, hashes and the FFT code on this machine.");
            var time = new Option<int>("--time") { Description = "Milliseconds per measurement (the first third warms up).", DefaultValueFactory = _ => 450 };
            bench.Options.Add(size);
            bench.Options.Add(time);
            bench.SetAction(p => Guard(Ctx(p), c =>
            {
                Bench.Run(c, p.GetValue(size) * 1024, TimeSpan.FromMilliseconds(Math.Max(3, p.GetValue(time))));
                return ExitCodes.Success;
            }));

            var info = new Command("info", "Show CPU features and the kernels that will be used.");
            info.SetAction(p => Guard(Ctx(p), c =>
            {
                c.Result("info", new Dictionary<string, object?>
                {
                    ["cpu"] = DeveParTools.Core.Galois.SimdSupport.Describe(),
                    ["kernel"] = DeveParTools.Core.Galois.SimdSupport.Best.ToString(),
                    ["supported"] = DeveParTools.Core.Galois.SimdSupport.SupportedLevels.Select(l => l.ToString()).ToList(),
                    ["cores"] = Environment.ProcessorCount,
                }, $"CPU features : {DeveParTools.Core.Galois.SimdSupport.Describe()}\nKernel       : {DeveParTools.Core.Galois.SimdSupport.Best}\nAvailable    : {string.Join(", ", DeveParTools.Core.Galois.SimdSupport.SupportedLevels)}\nCores        : {Environment.ProcessorCount}");
                return ExitCodes.Success;
            }));

            var update = BuildUpdate(Ctx);
            foreach (var cmd in new[] { create, verify, repair, extend, insert, delete, list, bench, info, update })
            {
                root.Subcommands.Add(cmd);
            }

            return root;
        }

        /// <summary>Runs a command, mapping exceptions to the par2cmdline exit codes.</summary>
        internal static int Guard(Common c, Func<Common, int> body)
        {
            try
            {
                return body(c);
            }
            catch (Exception e) when (e is ArgumentException or FormatException or NotSupportedException)
            {
                c.Error("invalid-arguments", e.Message);
                return ExitCodes.InvalidArguments;
            }
            catch (DeveParTools.Updates.UpdateException e)
            {
                c.Error("update-failed", e.Message);
                return ExitCodes.LogicError;
            }
            catch (InvalidDataException e)
            {
                c.Error("insufficient-data", e.Message);
                return ExitCodes.InsufficientCriticalData;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                c.Error("io-error", e.Message);
                return ExitCodes.FileIoError;
            }
            catch (OutOfMemoryException e)
            {
                c.Error("out-of-memory", e.Message);
                return ExitCodes.MemoryError;
            }
            catch (InvalidOperationException e)
            {
                // An operation that cannot run in the current state (e.g. extending a set with missing input).
                c.Error("not-possible", e.Message);
                return ExitCodes.LogicError;
            }
            catch (Exception e)
            {
                c.Error("error", e.ToString());
                return ExitCodes.LogicError;
            }
            finally
            {
                c.Dispose();
            }
        }
    }
}
