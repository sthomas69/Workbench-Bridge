using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using WorkbenchBridge.Service;

namespace WorkbenchBridge.Cli;

/// <summary>
/// `logs` (alias `monitor`): a docker-logs-style viewer over the service's
/// rolling Serilog file(s) in %ProgramData%\Workbench-Bridge\logs. It reads
/// the files directly, so it works whether the service runs as SYSTEM or
/// interactively, needs no elevation, and does not touch the IPC pipe.
///
/// Flags mirror `docker logs`:
///   -f, --follow           Stream new lines as they are written
///   -n, --tail &lt;N|all&gt;     Show the last N entries (default: all)
///       --since &lt;ts|rel&gt;   Only entries at/after an absolute time
///                            (2026-06-02T13:23:37Z) or relative (42m, 2h, 1d, 90s)
///       --details          Print the raw lines (full date, timezone, exceptions)
///   -c, --comport &lt;COMxx&gt;  Filter to one slot. Matches the user port, its
///                            paired internal port, and the slot's RFC2217 TCP
///                            port (resolved from appsettings.Local.json), so
///                            RFC2217 negotiation lines are caught too.
/// </summary>
public static class LogsCommand
{
    // 2026-06-02 10:03:39.699 +10:00 [INF] message
    private static readonly Regex EntryHead = new(
        @"^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2}) \[(?<lvl>\w+)\] (?<msg>.*)$",
        RegexOptions.Compiled);

    public static async Task<int> RunAsync(string[] args)
    {
        var opts = ParseArgs(args, out string? error);
        if (error is not null)
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine(
                "Usage: logs [-f|--follow] [-n|--tail <N|all>] [--since <ts|rel>] " +
                "[--details] [-c|--comport <COMxx>] [--path <file|dir>] [--clear] [--where]");
            return 1;
        }

        if (opts.Where)
            return WhereLogs(opts);

        if (opts.Clear)
            return ClearLogs(opts);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        return await ExecuteAsync(opts, cts.Token);
    }

    /// <summary>
    /// Runs the dump-and-optionally-follow pipeline against a caller-supplied
    /// cancellation token. The standalone <c>logs</c> command wires Ctrl+C to it;
    /// the <c>debug</c> command supplies its own token so it can restore the
    /// service's logging levels after the stream stops. This method does NOT
    /// register its own Ctrl+C handler.
    /// </summary>
    internal static async Task<int> ExecuteAsync(Options opts, CancellationToken ct)
    {
        // Resolve where to read. --path overrides the default and may point at a
        // directory (use the rolling glob) or a single file (tail just that one).
        // Default: %ProgramData%\Workbench-Bridge\logs.
        Func<List<string>> listFiles;
        if (opts.Path is not null && File.Exists(opts.Path))
        {
            string single = opts.Path;
            listFiles = () => new List<string> { single };
        }
        else
        {
            string logDir = opts.Path ?? BridgeLogging.LogDirectory;
            if (!Directory.Exists(logDir))
            {
                Console.Error.WriteLine($"Log directory not found: {logDir}");
                Console.Error.WriteLine("The service has not written any logs yet (or pass --path).");
                return 1;
            }
            // Daily-rolling files: workbenchbridge<yyyyMMdd>.log. The filename date
            // sorts chronologically, so an ordinal sort is oldest -> newest.
            listFiles = () => Directory.GetFiles(logDir, BridgeLogging.FileSearchPattern)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        Regex? portFilter = BuildPortFilter(opts.ComPort);

        List<string> Files() => listFiles();

        var files = Files();
        if (files.Count == 0)
        {
            Console.Error.WriteLine("No matching log files found.");
            return 1;
        }

        // ---- initial dump: parse everything, then apply since / filter / tail ----
        var entries = new List<Entry>();
        foreach (var f in files)
            ReadEntries(f, entries);

        IEnumerable<Entry> filtered = entries;
        if (opts.Since is DateTimeOffset since)
            filtered = filtered.Where(e => e.Timestamp is null || e.Timestamp >= since);
        if (portFilter is not null)
            filtered = filtered.Where(e => portFilter.IsMatch(e.Text));

        var list = filtered.ToList();
        if (opts.Tail is int n && list.Count > n)
            list = list.Skip(list.Count - n).ToList();

        foreach (var e in list)
            PrintEntry(e, opts.Details);

        if (!opts.Follow)
            return 0;

        // ---- follow: start at the end of the newest file and stream appends ----
        Console.Error.WriteLine("-- following (Ctrl+C to stop) --");

        string current = files[^1];
        long pos;
        try { pos = new FileInfo(current).Length; } catch { pos = 0; }
        var buffer = new StringBuilder();
        bool lastMatched = portFilter is null;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Roll detection: a newer-dated file means the service crossed midnight.
                var newest = Files().LastOrDefault();
                if (newest is not null && !string.Equals(newest, current, StringComparison.OrdinalIgnoreCase))
                {
                    current = newest;
                    pos = 0;
                    buffer.Clear();
                }

                string chunk = "";
                try
                {
                    using var fs = new FileStream(
                        current, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    if (fs.Length < pos) { pos = 0; buffer.Clear(); } // truncated
                    fs.Seek(pos, SeekOrigin.Begin);
                    using var sr = new StreamReader(fs);
                    chunk = await sr.ReadToEndAsync(ct);
                    pos = fs.Position;
                }
                catch (IOException) { /* transient share violation; retry next tick */ }

                if (chunk.Length > 0)
                {
                    buffer.Append(chunk);
                    int nl;
                    while ((nl = IndexOfNewline(buffer)) >= 0)
                    {
                        string lineRaw = buffer.ToString(0, nl);
                        buffer.Remove(0, nl + 1);
                        string line = lineRaw.TrimEnd('\r');
                        StreamLine(line, portFilter, opts.Details, ref lastMatched);
                    }
                }

                try { await Task.Delay(400, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (OperationCanceledException) { /* Ctrl+C */ }

        return 0;
    }

    // -------------------------------------------------------------
    // Streaming helpers
    // -------------------------------------------------------------

    private static void StreamLine(string line, Regex? portFilter, bool details, ref bool lastMatched)
    {
        var m = EntryHead.Match(line);
        if (m.Success)
        {
            lastMatched = portFilter is null || portFilter.IsMatch(line);
            if (!lastMatched) return;

            if (details)
            {
                Console.WriteLine(line);
            }
            else
            {
                string lvl = m.Groups["lvl"].Value;
                string head = ParseTs(m.Groups["ts"].Value) is DateTimeOffset t
                    ? $"{t:HH:mm:ss} [{lvl}] {m.Groups["msg"].Value}"
                    : line;
                WriteColored(head, lvl);
            }
        }
        else
        {
            // Continuation (exception/stack-trace) line: inherit the head's decision.
            if (lastMatched)
                Console.WriteLine(details ? line : "    " + line);
        }
    }

    private static int IndexOfNewline(StringBuilder sb)
    {
        for (int i = 0; i < sb.Length; i++)
            if (sb[i] == '\n') return i;
        return -1;
    }

    // -------------------------------------------------------------
    // File parsing
    // -------------------------------------------------------------

    private sealed class Entry
    {
        public DateTimeOffset? Timestamp;
        public string Level = "";
        public string Message = "";
        public List<string> Raw = new();
        public string Text => string.Join("\n", Raw);
    }

    private static void ReadEntries(string file, List<Entry> into)
    {
        try
        {
            using var fs = new FileStream(
                file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            string? line;
            Entry? cur = null;
            while ((line = sr.ReadLine()) is not null)
            {
                var m = EntryHead.Match(line);
                if (m.Success)
                {
                    cur = new Entry
                    {
                        Level = m.Groups["lvl"].Value,
                        Message = m.Groups["msg"].Value,
                        Timestamp = ParseTs(m.Groups["ts"].Value)
                    };
                    cur.Raw.Add(line);
                    into.Add(cur);
                }
                else if (cur is not null)
                {
                    cur.Raw.Add(line);
                }
                else
                {
                    // Stray continuation before any header - keep it as its own entry.
                    var stray = new Entry { Message = line };
                    stray.Raw.Add(line);
                    into.Add(stray);
                }
            }
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"Could not read {file}: {ex.Message}");
        }
    }

    private static void PrintEntry(Entry e, bool details)
    {
        if (details)
        {
            foreach (var r in e.Raw)
                Console.WriteLine(r);
            return;
        }

        string head = e.Timestamp is DateTimeOffset t
            ? $"{t:HH:mm:ss} [{e.Level}] {e.Message}"
            : (e.Raw.Count > 0 ? e.Raw[0] : e.Message);
        WriteColored(head, e.Level);

        for (int i = 1; i < e.Raw.Count; i++)
            Console.WriteLine("    " + e.Raw[i]);
    }

    private static void WriteColored(string text, string level)
    {
        ConsoleColor? color = level.ToUpperInvariant() switch
        {
            "ERR" or "FTL" => ConsoleColor.Red,
            "WRN" => ConsoleColor.Yellow,
            "DBG" or "VRB" => ConsoleColor.DarkGray,
            _ => null
        };

        if (color is null || Console.IsOutputRedirected)
        {
            Console.WriteLine(text);
            return;
        }

        var prev = Console.ForegroundColor;
        Console.ForegroundColor = color.Value;
        Console.WriteLine(text);
        Console.ForegroundColor = prev;
    }

    private static DateTimeOffset? ParseTs(string ts) =>
        DateTimeOffset.TryParseExact(
            ts, "yyyy-MM-dd HH:mm:ss.fff zzz",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t
            : null;

    // -------------------------------------------------------------
    // -c / --comport filter
    // -------------------------------------------------------------

    /// <summary>
    /// Builds a whole-word matcher for a slot's identifiers. With the config we
    /// match user port + internal port + RFC2217 TCP port; without it we fall
    /// back to the given port plus the conventional internal pair (COM42 ->
    /// COM242). Word boundaries stop COM42 from matching COM420/COM242 etc.
    /// </summary>
    private static Regex? BuildPortFilter(string? comport)
    {
        if (string.IsNullOrWhiteSpace(comport))
            return null;

        string port = comport.Trim().ToUpperInvariant();
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { port };

        var config = TryLoadConfig();
        var map = config?.ComPortMapping.FirstOrDefault(m =>
            string.Equals(m.User.PortName, port, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(m.Internal.PortName, port, StringComparison.OrdinalIgnoreCase));

        if (map is not null)
        {
            tokens.Add(map.User.PortName);
            tokens.Add(map.Internal.PortName);
            tokens.Add(map.PiTcpPort.ToString());
        }
        else
        {
            string digits = new string(port.Where(char.IsDigit).ToArray());
            if (digits.Length > 0)
                tokens.Add("COM2" + digits);
        }

        string pattern = @"\b(" + string.Join("|", tokens.Select(Regex.Escape)) + @")\b";
        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    private static BridgeConfig? TryLoadConfig()
    {
        foreach (var path in CandidateConfigPaths())
        {
            if (!File.Exists(path)) continue;
            try
            {
                var conf = new ConfigurationBuilder()
                    .AddJsonFile(path, optional: false)
                    .Build();
                var bridge = new BridgeConfig();
                conf.GetSection("Bridge").Bind(bridge);
                if (bridge.ComPortMapping.Count > 0)
                    return bridge;
            }
            catch { /* try the next candidate */ }
        }
        return null;
    }

    private static IEnumerable<string> CandidateConfigPaths()
    {
        string cliDir = AppContext.BaseDirectory;

        // In-source-tree Local.json (gitignored, dev-canonical):
        // <repo>/src/WorkbenchBridge.Service/appsettings.Local.json
        var dir = new DirectoryInfo(cliDir);
        var srcDir = dir.Parent?.Parent?.Parent?.Parent?.Parent;
        if (srcDir is not null)
            yield return Path.Combine(
                srcDir.FullName, "WorkbenchBridge.Service", "appsettings.Local.json");

        // A copy next to the CLI exe.
        yield return Path.Combine(cliDir, "appsettings.Local.json");
    }

    // -------------------------------------------------------------
    // --clear: delete log files
    // -------------------------------------------------------------

    /// <summary>
    /// Removes the rolling log file(s). The current day's file is held open by
    /// the running service (Serilog grants only FileShare.Read, not Delete), so
    /// it cannot be removed while the service runs - we report that clearly and
    /// delete every rolled file we can. -c/--comport cannot scope this because
    /// all slots share one log file.
    /// </summary>
    private static int ClearLogs(Options opts)
    {
        if (opts.ComPort is not null)
            Console.Error.WriteLine(
                "Note: --clear removes whole log files; -c/--comport is ignored " +
                "(all slots share one log file).");

        List<string> targets;
        if (opts.Path is not null && File.Exists(opts.Path))
        {
            targets = new List<string> { opts.Path };
        }
        else
        {
            string logDir = opts.Path ?? BridgeLogging.LogDirectory;
            if (!Directory.Exists(logDir))
            {
                Console.Error.WriteLine($"Log directory not found: {logDir}");
                return 1;
            }
            Console.WriteLine($"Log directory: {logDir}");
            targets = Directory.GetFiles(logDir, BridgeLogging.FileSearchPattern)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (targets.Count == 0)
        {
            Console.WriteLine("No log files to clear.");
            return 0;
        }

        int removed = 0, locked = 0;
        foreach (var f in targets)
        {
            try
            {
                File.Delete(f);
                removed++;
                Console.WriteLine($"Removed {Path.GetFileName(f)}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                locked++;
                Console.Error.WriteLine($"Locked (in use): {Path.GetFileName(f)}");
            }
        }

        Console.WriteLine($"Cleared {removed} file(s)" + (locked > 0 ? $", {locked} locked." : "."));
        if (locked > 0)
        {
            Console.Error.WriteLine(
                "Today's log is held open by the running service (Serilog keeps it locked " +
                "for writing), so it cannot be deleted while the service runs.");
            Console.Error.WriteLine(
                "To get a clean slate, clear BEFORE starting the service, or stop it first:");
            Console.Error.WriteLine(
                "  - interactive (Service.exe in a terminal): Ctrl+C the service window, " +
                "re-run 'logs --clear', then restart it");
            Console.Error.WriteLine(
                "  - installed Windows service: Stop-Service WorkbenchBridge; logs --clear; " +
                "Start-Service WorkbenchBridge");
        }
        return locked > 0 ? 1 : 0;
    }

    // -------------------------------------------------------------
    // Argument parsing
    // -------------------------------------------------------------

    internal sealed class Options
    {
        public bool Follow;
        public bool Details;
        public int? Tail;          // null = all
        public DateTimeOffset? Since;
        public string? ComPort;
        public string? Path;
        public bool Clear;
        public bool Where;
    }

    /// <summary>
    /// `logs --where`: print the resolved log directory and the rolling files in it
    /// (with sizes/timestamps). Pure discovery — answers "where are the logs?"
    /// without needing the service or any other tool.
    /// </summary>
    private static int WhereLogs(Options opts)
    {
        string logDir = opts.Path ?? BridgeLogging.LogDirectory;
        Console.WriteLine($"Log directory: {logDir}");
        if (!Directory.Exists(logDir))
        {
            Console.WriteLine("  (does not exist yet - the service has not written any logs here)");
            return 0;
        }
        var files = Directory.GetFiles(logDir, BridgeLogging.FileSearchPattern)
            .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (files.Count == 0)
        {
            Console.WriteLine("  (no log files yet)");
            return 0;
        }
        foreach (var f in files)
        {
            var fi = new FileInfo(f);
            Console.WriteLine($"  {fi.Name,-30} {fi.Length,12:n0} bytes  {fi.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
        }
        return 0;
    }

    /// <summary>
    /// Builds the options the <c>debug</c> command uses to follow one slot: a
    /// short initial tail for context, then live follow filtered to the port.
    /// </summary>
    internal static Options FollowOptions(string comport, int tail) => new()
    {
        Follow = true,
        ComPort = comport,
        Tail = tail
    };

    private static Options ParseArgs(string[] args, out string? error)
    {
        error = null;
        var o = new Options();

        // args[0] is the command name ("logs"/"monitor"); skip it.
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            switch (a.ToLowerInvariant())
            {
                case "-f":
                case "--follow":
                    o.Follow = true;
                    break;
                case "--details":
                    o.Details = true;
                    break;
                case "-n":
                case "--tail":
                    if (++i >= args.Length) { error = $"{a} requires a value (N or 'all')."; return o; }
                    if (!args[i].Equals("all", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!int.TryParse(args[i], out int n) || n < 0)
                        {
                            error = $"Invalid --tail value '{args[i]}' (expected a non-negative number or 'all').";
                            return o;
                        }
                        o.Tail = n;
                    }
                    break;
                case "--since":
                    if (++i >= args.Length) { error = "--since requires a value."; return o; }
                    var ts = ParseSince(args[i]);
                    if (ts is null)
                    {
                        error = $"Invalid --since value '{args[i]}' (expected ISO-8601 or relative like 42m, 2h, 1d, 90s).";
                        return o;
                    }
                    o.Since = ts;
                    break;
                case "-c":
                case "--comport":
                    if (++i >= args.Length) { error = $"{a} requires a COM port value."; return o; }
                    o.ComPort = args[i];
                    break;
                case "--path":
                    if (++i >= args.Length) { error = "--path requires a file or directory."; return o; }
                    o.Path = args[i];
                    break;
                case "--clear":
                    o.Clear = true;
                    break;
                case "--where":
                case "--path-only":
                    o.Where = true;
                    break;
                default:
                    error = $"Unknown logs option: {a}";
                    return o;
            }
        }

        return o;
    }

    private static DateTimeOffset? ParseSince(string value)
    {
        value = value.Trim();

        var rel = Regex.Match(value, @"^(\d+)([smhd])$", RegexOptions.IgnoreCase);
        if (rel.Success)
        {
            int q = int.Parse(rel.Groups[1].Value);
            var now = DateTimeOffset.Now;
            return rel.Groups[2].Value.ToLowerInvariant() switch
            {
                "s" => now.AddSeconds(-q),
                "m" => now.AddMinutes(-q),
                "h" => now.AddHours(-q),
                "d" => now.AddDays(-q),
                _ => null
            };
        }

        if (DateTimeOffset.TryParse(
                value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var abs))
            return abs;

        return null;
    }
}
