using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace WorkbenchBridge.Service;

/// <summary>
/// Applies a <see cref="BridgeConfig"/> to the machine: seeds/repairs com0com
/// pairs (setupc.exe install), renames them to the desired COM names via the
/// registry + pnputil, releases stale ComDB reservations, and normalises
/// EmuBR/EmuOverrun.
///
/// This is the "heavy lifting" that used to live in the CLI's ConfigureCommand
/// and required the user to run elevated. It now runs INSIDE the WorkbenchBridge
/// service, which already runs as LocalSystem and therefore has the HKLM-write,
/// process-spawn and PnP privileges these steps need. The CLI just sends a
/// Configure request over the named pipe (see BridgeWorker.HandleConfigureAsync).
///
/// The provisioner deliberately does NOT touch the WorkbenchBridge service
/// itself (it cannot stop the process it runs in). Releasing the Internal COM
/// port handles is the caller's job: BridgeWorker stops its in-process bridges
/// before calling Apply and restarts them afterwards. The com0com pairs are
/// reloaded per-device with pnputil /restart-device, so the com0com *service*
/// never needs a bounce either.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Provisioner
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;

    public Provisioner(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger("provisioner");
    }

    /// <summary>
    /// Computes the plan and (unless <paramref name="dryRun"/>) applies it.
    /// Never throws for expected failures - the result carries Success plus a
    /// human-readable log that the CLI prints verbatim.
    /// </summary>
    public ProvisionResult Apply(BridgeConfig config, bool dryRun)
    {
        var log = new List<string>();
        void Line(string s)
        {
            log.Add(s);
            _logger.LogInformation("{Line}", s);
        }

        if (config.ComPortMapping.Count == 0)
            return new ProvisionResult { Success = false, Log = { "No Bridge.ComPortMapping entries to apply." } };

        Line($"com0com params key:  HKLM\\{config.Registry.Com0comParametersKey}");
        Line($"com0com install:     {config.Tools.Com0comPath}");

        var registry = new Com0comRegistry(
            config.Registry.Com0comParametersKey,
            _loggerFactory.CreateLogger<Com0comRegistry>());
        var comDb = new ComPortDatabase(_loggerFactory.CreateLogger<ComPortDatabase>());

        HashSet<int> existingIdx;
        try { existingIdx = registry.ListExistingPairIndices(); }
        catch (UnauthorizedAccessException)
        {
            return new ProvisionResult { Success = false, Log = { "Registry read denied (service not running as SYSTEM?)." } };
        }

        var visibleNames = System.IO.Ports.SerialPort.GetPortNames()
            .Select(n => n.TrimEnd(':'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Line($"Visible COM ports now ({visibleNames.Count}): {string.Join(", ", visibleNames.OrderBy(n => n))}");

        string setupcPath = Path.Combine(config.Tools.Com0comPath, "setupc.exe");
        if (!File.Exists(setupcPath))
            return new ProvisionResult { Success = false, Log = { $"setupc.exe not found at {setupcPath}." } };

        // Build the per-mapping plan: ok / rename / reinstall. The authoritative
        // signal is whether Windows currently exposes the desired COM name on the
        // right CNCAn/CNCBn PnP device (the Enum branch), cross-checked against
        // the live SerialPort.GetPortNames() set.
        var actions = new List<(ComPortMappingConfig m, string action, string aExposed, string bExposed)>();
        foreach (var m in config.ComPortMapping)
        {
            int idx = m.GetPairIndex();
            string aExposed = registry.ReadExposedPortNameFromEnum($"CNCA{idx}") ?? "";
            string bExposed = registry.ReadExposedPortNameFromEnum($"CNCB{idx}") ?? "";

            // Is each side a REAL device in the Windows Ports class? A pair seeded
            // but left in the raw CNCPorts class still has its PortName in the
            // registry (and may show in GetPortNames/ComDB), so the class check is
            // the only reliable signal of "actually a usable port" (SLOT8, 2026-06-14).
            bool aInPorts = registry.IsExposedInPortsClass($"CNCA{idx}");
            bool bInPorts = registry.IsExposedInPortsClass($"CNCB{idx}");

            // "ok": already a real Ports-class device with the desired name.
            bool aOk = aInPorts
                       && string.Equals(aExposed, m.User.PortName, StringComparison.OrdinalIgnoreCase)
                       && visibleNames.Contains(m.User.PortName);
            bool bOk = bInPorts
                       && string.Equals(bExposed, m.Internal.PortName, StringComparison.OrdinalIgnoreCase)
                       && visibleNames.Contains(m.Internal.PortName);

            string action;
            if (aOk && bOk)
                action = "ok";
            else if (aInPorts && bInPorts)
                // Both are real Ports-class devices, just with the wrong name ->
                // rename in place (setupc change RealPortName).
                action = "rename";
            else
                // At least one side is NOT in the Ports class (stuck in CNCPorts,
                // or missing) -> remove + reinstall so the Ports-class co-installer
                // (DrvInst) runs and migrates it. A plain rename can't do this.
                action = "reinstall";

            actions.Add((m, action, aExposed, bExposed));
        }

        Line("Plan:");
        foreach (var (m, action, aExp, bExp) in actions)
            Line($"  {m.SlotLabel,-6} pair {m.GetPairIndex()} -> {action,-9} (currently CNCA={aExp,-6} CNCB={bExp,-6})");

        if (dryRun)
        {
            Line("[dry-run] No changes applied.");
            return new ProvisionResult { Success = true, Log = log };
        }

        // Remove half-finished pairs left by a prior interrupted install.
        int cleaned = CleanupHalfPairs(registry, setupcPath, config.Tools.Com0comPath, Line);
        if (cleaned > 0)
        {
            Line($"Cleaned up {cleaned} half-finished com0com pair(s).");
            try { existingIdx = registry.ListExistingPairIndices(); }
            catch (UnauthorizedAccessException) { /* keep prior snapshot */ }
        }

        foreach (var (m, action, _, _) in actions)
        {
            if (action == "ok") continue;
            int idx = m.GetPairIndex();
            string user = m.User.PortName;
            string internalP = m.Internal.PortName;

            if (action == "reinstall")
            {
                if (existingIdx.Contains(idx))
                {
                    Line($"Pair {idx} ({m.SlotLabel}): remove old registration.");
                    bool removed = RunSetupc(setupcPath, config.Tools.Com0comPath, $"remove {idx}", Line);
                    if (!removed)
                    {
                        Line($"setupc remove {idx} failed; deleting orphan registry subkeys.");
                        try
                        {
                            registry.DeleteSide($"CNCA{idx}");
                            registry.DeleteSide($"CNCB{idx}");
                        }
                        catch (Exception ex)
                        {
                            return Fail(log, $"Failed to delete orphan registry: {ex.Message}");
                        }
                    }
                }

                comDb.Release(user);
                comDb.Release(internalP);

                if (!RunSetupc(setupcPath, config.Tools.Com0comPath,
                        $"install {idx} PortName=COM#,EmuBR=yes,EmuOverrun=no " +
                        $"PortName=COM#,EmuBR=yes,EmuOverrun=no",
                        Line))
                {
                    return Fail(log, $"setupc install {idx} failed; aborting.");
                }
            }

            // Strip serenum from both sides BEFORE the rename. The 'change' below
            // restarts the devnode, so the stripped UpperFilter is picked up in the
            // SAME restart (no extra re-enumeration needed). serenum is the serial
            // enumerator that mis-detects our com0com ports as a "Microsoft Serial
            // Mouse" and, with sermouse enabled, injects phantom cursor movement -
            // the desktop hijack seen during pair-7 install churn (2026-06-15).
            registry.StripSerenumUpperFilter($"CNCA{idx}");
            registry.StripSerenumUpperFilter($"CNCB{idx}");

            // Rename via setupc 'change ... RealPortName=' — the documented method
            // that renames the port while KEEPING it in the
            // "Ports (COM & LPT)" class. The previous approach (direct registry
            // RealPortName write + pnputil /restart-device) did NOT trigger
            // Microsoft's Ports-class co-installer for a freshly-installed pair:
            // the restart ran before the device was live and failed with "device
            // instance does not exist", leaving the pair stuck in the raw
            // 'CNCPorts' class with no usable COM name (SLOT8, 2026-06-14).
            // Release the target names from the ComDB first so the rename can't
            // collide with a stale reservation. setupc runs inside the service,
            // which guards the unsigned driver's crash dialogs (KillCrashDialogs).
            comDb.Release(user);
            comDb.Release(internalP);

            if (!RunSetupc(setupcPath, config.Tools.Com0comPath,
                    $"change CNCA{idx} RealPortName={user}", Line))
                return Fail(log, $"setupc change CNCA{idx} RealPortName={user} failed.");
            if (!RunSetupc(setupcPath, config.Tools.Com0comPath,
                    $"change CNCB{idx} RealPortName={internalP}", Line))
                return Fail(log, $"setupc change CNCB{idx} RealPortName={internalP} failed.");

            // Verify the rename reached the LIVE devnode, not just the Parameters
            // key. A 'change' issued immediately after a 'Reboot required' install
            // defers the Ports-class co-installer, so the live Enum PortName can
            // still read the old/auto-assigned value (e.g. SLOT8 auto-grabbed COM17
            // and the COM48 rename stayed staged, 2026-06-15). We deliberately do
            // NOT fail: the device IS in the Ports class, and a later self-heal pass
            // renames it cleanly once it has settled - no reboot required. The
            // self-heal's healthy check compares the live name, so it will retry.
            string aLive = registry.ReadExposedPortNameFromEnum($"CNCA{idx}") ?? "";
            string bLive = registry.ReadExposedPortNameFromEnum($"CNCB{idx}") ?? "";
            if (!string.Equals(aLive, user, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(bLive, internalP, StringComparison.OrdinalIgnoreCase))
            {
                Line($"  NOTE: pair {idx} rename deferred (live CNCA={aLive}->{user}, " +
                     $"CNCB={bLive}->{internalP}); a subsequent self-heal pass will " +
                     "apply it once the devnode is stable (no reboot needed).");
            }
        }

        // Belt-and-braces normalisation of EmuBR/EmuOverrun to REG_DWORD.
        try
        {
            int sides = 0;
            foreach (var m in config.ComPortMapping)
            {
                registry.EnsureEmuFlags(m.User.RegistryKey);
                registry.EnsureEmuFlags(m.Internal.RegistryKey);
                sides += 2;
            }
            Line($"Normalised EmuBR/EmuOverrun on all {sides} sides.");

            // Read the values straight back so the log reflects ground truth, not
            // just "we issued the write". These are the same Parameters subkeys
            // `setupc list` reads, so this is the authoritative answer to "did it
            // actually take?". Want: EmuBR=on, EmuOverrun=off on every side.
            var after = registry.ListSides();
            var bad = new List<string>();
            foreach (var m in config.ComPortMapping)
            {
                foreach (var key in new[] { m.User.RegistryKey, m.Internal.RegistryKey })
                {
                    if (!after.TryGetValue(key, out var s))
                        bad.Add($"{key} (subkey missing)");
                    else if (!s.EmuBR)
                        bad.Add($"{key} (EmuBR off)");
                    else if (s.EmuOverrun)
                        bad.Add($"{key} (EmuOverrun ON)");
                }
            }
            if (bad.Count == 0)
                Line($"Verified from registry: EmuBR=on, EmuOverrun=off on all {sides} sides.");
            else
                Line("WARNING: EmuBR/EmuOverrun not as expected after write on: " +
                     string.Join(", ", bad) + ".");

            // Pairs renamed/reinstalled this run were restarted via pnputil above,
            // so the live driver re-read these flags. A pair that was already 'ok'
            // is NOT restarted, so the running driver keeps the previous flags
            // until the next device restart or reboot; the registry (and a future
            // start) is correct regardless.
        }
        catch (Exception ex)
        {
            return Fail(log, $"Failed to normalise EmuBR/EmuOverrun: {ex.Message}");
        }

        Line("Provisioning applied successfully.");
        return new ProvisionResult { Success = true, Log = log };
    }

    private static ProvisionResult Fail(List<string> log, string message)
    {
        log.Add(message);
        return new ProvisionResult { Success = false, Log = log };
    }

    /// <summary>
    /// Removes "half-finished" com0com pairs where only one of CNCA{n}/CNCB{n}
    /// exists - the residue of an install interrupted before both sides were
    /// created. Tries `setupc remove {n}` first, falling back to deleting the
    /// orphan registry subkey directly. Returns the number of pairs removed.
    /// </summary>
    private int CleanupHalfPairs(
        Com0comRegistry registry, string setupcPath, string com0comDir, Action<string> line)
    {
        Dictionary<string, Com0comSideStatus> sides;
        try { sides = registry.ListSides(); }
        catch (Exception ex)
        {
            line($"Skipping half-pair cleanup (registry read failed: {ex.Message}).");
            return 0;
        }

        var present = new Dictionary<int, (bool A, bool B)>();
        foreach (var name in sides.Keys)
        {
            if (!Com0comRegistry.TryParseSide(name, out var side, out int idx)) continue;
            present.TryGetValue(idx, out var flags);
            if (side == "CNCA") flags.A = true; else flags.B = true;
            present[idx] = flags;
        }

        int removed = 0;
        foreach (var (idx, flags) in present)
        {
            if (flags.A && flags.B) continue; // complete pair - leave it alone
            line($"Pair {idx}: half-finished (CNCA={flags.A}, CNCB={flags.B}); removing.");
            bool ok = RunSetupc(setupcPath, com0comDir, $"remove {idx}", line);
            if (!ok)
            {
                try
                {
                    registry.DeleteSide($"CNCA{idx}");
                    registry.DeleteSide($"CNCB{idx}");
                }
                catch (Exception ex)
                {
                    line($"Could not delete orphan subkeys for pair {idx}: {ex.Message}");
                    continue;
                }
            }
            removed++;
        }
        return removed;
    }

    /// <summary>
    /// Runs pnputil.exe with the given arguments to issue /restart-device against
    /// a com0com PnP instance ID so the driver re-reads RealPortName and re-runs
    /// the Ports class co-installer without setupc.exe's ComDB call.
    /// </summary>
    private bool RunPnputil(string arguments, Action<string> line)
    {
        string pnputil = Path.Combine(Environment.SystemDirectory, "pnputil.exe");
        line($"pnputil {arguments}");
        var psi = new ProcessStartInfo
        {
            FileName = pnputil,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        try
        {
            using var p = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null.");
            p.StandardInput.Close();
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit((int)TimeSpan.FromSeconds(30).TotalMilliseconds))
            {
                line("  pnputil timed out; killing.");
                try { p.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return false;
            }
            foreach (var l in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                line($"  | {l.TrimEnd()}");
            if (p.ExitCode != 0)
            {
                line($"  pnputil exited {p.ExitCode}");
                if (!string.IsNullOrWhiteSpace(stderr))
                    line($"  stderr: {stderr.Trim()}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            line($"  Failed to run pnputil: {ex.Message}");
            return false;
        }
    }

    private bool RunSetupc(string setupcPath, string com0comDir, string arguments, Action<string> line)
    {
        line($"setupc {arguments}");

        // setupc.exe resolves com0com.inf relative to its working directory; it
        // must be the com0com install folder or the PnP install fails and pops a
        // blocking GUI dialog.
        var psi = new ProcessStartInfo
        {
            FileName = setupcPath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = com0comDir
        };

        // The unsigned com0com 3.0.0 driver can spawn a WerFault crash-reporter
        // window during install that blocks setupc behind a modal dialog. Kill
        // only that in the background so setupc can progress unattended. We do
        // NOT touch DrvInst - it is the real installer doing the Ports-class
        // migration (see KillCrashDialogs).
        using var watcherCts = new CancellationTokenSource();
        var watcher = Task.Run(() => KillCrashDialogsLoop(watcherCts.Token));

        try
        {
            using var p = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null.");
            p.StandardInput.Close();
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit((int)TimeSpan.FromSeconds(120).TotalMilliseconds))
            {
                line("  setupc timed out after 120s; killing.");
                try { p.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return false;
            }

            foreach (var l in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                line($"  | {l.TrimEnd()}");

            if (p.ExitCode != 0)
            {
                line($"  setupc exited {p.ExitCode}");
                if (!string.IsNullOrWhiteSpace(stderr))
                    line($"  stderr: {stderr.Trim()}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            line($"  Failed to run setupc: {ex.Message}");
            return false;
        }
        finally
        {
            watcherCts.Cancel();
            try { watcher.Wait(TimeSpan.FromSeconds(2)); } catch { /* best effort */ }
            KillCrashDialogs();
        }
    }

    private void KillCrashDialogsLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            KillCrashDialogs();
            try { Task.Delay(500, ct).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void KillCrashDialogs()
    {
        // Kill ONLY the crash-reporter UI (WerFault). Do NOT kill DrvInst: it is
        // the legitimate Windows device installer that performs the Ports-class
        // migration for a freshly-seeded com0com pair. Killing it (as this code
        // used to) aborts the migration and leaves the port stuck in com0com's
        // raw 'CNCPorts' class with no usable COM name (SLOT8 regression,
        // confirmed in the service log 2026-06-14: "Killing DrvInst ... blocking
        // setupc.exe" 3.3s into install). A genuinely hung install is bounded by
        // setupc's 120s timeout instead.
        foreach (var name in new[] { "WerFault", "WerFaultSecure" })
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        _logger.LogWarning("Killing {Name} (PID {Pid}) blocking setupc.exe", name, p.Id);
                        p.Kill(entireProcessTree: true);
                    }
                    catch { /* ignore per-process failures */ }
                    finally { p.Dispose(); }
                }
            }
            catch { /* ignore enumeration failures */ }
        }
    }
}

/// <summary>
/// Outcome of a <see cref="Provisioner.Apply"/> call: overall success plus the
/// step-by-step log the CLI prints to the user.
/// </summary>
public sealed class ProvisionResult
{
    public bool Success { get; init; }
    public List<string> Log { get; init; } = new();
}
