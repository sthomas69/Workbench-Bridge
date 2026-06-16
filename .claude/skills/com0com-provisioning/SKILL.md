---
name: com0com-provisioning
description: >-
  Create, rename, diagnose and repair com0com virtual COM-port pairs so they
  appear as real, usable "Ports (COM & LPT)" devices that any IDE/esptool can
  open. Covers the Ports-class vs CNCPorts distinction, the PortName=COM# +
  setupc-change rename dance, applying a rename WITHOUT a reboot, the serenum /
  "Microsoft Serial Mouse" desktop-hijack and its fix, ComDB reservations, and
  read-only verification against device reality (not tool output). Use when a
  com0com port is missing, won't show in Device Manager / IDE port lists, has
  the wrong COM number, "needs a reboot", or causes the mouse to jump during
  install.
---

# com0com provisioning & repair

Hard-won operational knowledge for making com0com pairs into real, flashable
Windows COM ports. com0com is an unsigned virtual null-modem driver; getting a
pair into the standard **Ports (COM & LPT)** class — not com0com's own raw class
— is the entire game. This skill is the playbook the Workbench-Bridge service
automates in `Provisioner.cs` / `Com0comRegistry.cs`, written so it can be
applied (or debugged) by hand.

## The mental model

A pair is two sides: `CNCA{n}` (the "user" port your IDE opens) and `CNCB{n}`
(the "internal" port the bridge opens), same `{n}`. Each side lives in **two**
registry locations that must agree:

| Location | What it holds | Authoritative for |
|---|---|---|
| `HKLM\SYSTEM\CurrentControlSet\Services\com0com\Parameters\CNCx{n}` | `PortName` (`COM#`=auto), `RealPortName`, `EmuBR`, `EmuOverrun` | the *desired* config (staged) |
| `HKLM\SYSTEM\CurrentControlSet\Enum\COM0COM\PORT\CNCx{n}` | `ClassGUID`, `FriendlyName`, `UpperFilters`, `Device Parameters\PortName` | what Windows *actually exposes* (live) |

## The one rule: verify the device CLASS, not the name

`SerialPort.GetPortNames()`, `Win32_SerialPort`, the ComDB, and even
`Parameters\RealPortName` will all happily report a COM name for a port that is
**not actually usable**. The single source of truth is the device's `ClassGUID`
in the **Enum** branch:

- `{4d36e978-e325-11ce-bfc1-08002be10318}` = **Ports (COM & LPT)** → real, usable port. ✅
- `{df799e12-3c56-421b-b298-b6d3642bc878}` = **CNCPorts** (com0com's own class) → seeded but its Ports-class migration never finished → **NOT a usable port**, even though it has a name. ❌

If you "confirm success" from `GetPortNames`/registry alone you will be wrong.
Always read `ClassGUID` (and the live `Device Parameters\PortName`).

## Creating / renaming a pair (the correct sequence)

setupc.exe resolves `com0com.inf` relative to its working directory — **always
run it from the com0com install dir** (`C:\Program Files (x86)\com0com`).

```
# 1. Install with the COM# PLACEHOLDER — this is what triggers Microsoft's
#    Ports-class co-installer. Installing PortName=COM48 DIRECTLY skips the
#    co-installer and lands the device in the raw CNCPorts class (unusable).
setupc.exe install <n> PortName=COM#,EmuBR=yes,EmuOverrun=no PortName=COM#,EmuBR=yes,EmuOverrun=no

# 2. Rename to the desired names. Keeps the device in the Ports class.
setupc.exe change CNCA<n> RealPortName=COM48
setupc.exe change CNCB<n> RealPortName=COM248
```

The install auto-assigns a name (e.g. COM17). Step 2 renames it to what you want.

## Applying a rename WITHOUT a reboot

Renaming forces the com0com bus to rebuild the child devnode. Windows won't rip
out a possibly-referenced node live, so SetupAPI sets `DI_NEEDREBOOT`, **stages**
the new name in `Parameters\RealPortName`, and leaves the live
`Enum\…\Device Parameters\PortName` on the old/auto name. Hence "Reboot required".

You do **not** need to reboot:

- A `setupc change CNCA<n> RealPortName=COMxx` issued **immediately after** a
  reboot-required install **defers** (the co-installer waits). The live name does
  not change.
- Re-run the **same** `setupc change` once the devnode is **live & stable**
  (Status OK). It applies in-session: ComDB logs the new COM, releases the old,
  restarts the devnode. Verify the live `Device Parameters\PortName` afterwards.
- `pnputil /restart-device "COM0COM\PORT\CNCx<n>"` alone does **not** re-read
  `RealPortName` — it restarts the node but the co-installer rename doesn't run.
  Use `setupc change`, not a bare restart, to apply a name.

So the robust automation is: install (may defer) → on a later pass, once the
device is stable, re-issue the rename and confirm the **live** name changed.

## The "mouse jumps around" desktop hijack (serenum / sermouse)

com0com.inf attaches `serenum` (the serial enumerator) as an UpperFilter on every
port. During install/re-enum churn serenum probes the new port, mis-detects a
**"Microsoft Serial Mouse"**, and — with the `sermouse` service enabled — that
phantom injects real cursor movement. This is the cause of the cursor jumping
during provisioning (NOT crash dialogs, NOT a killed DrvInst).

Fix (living off the land, reversible, no driver signing):

```powershell
# Belt: disable the serial-mouse driver globally (safe if you have no RS-232 mouse;
# USB/HID mice use mouhid/HID, not sermouse). Reverse with Start=3.
Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\sermouse' Start 4 -Type DWord

# Braces: strip serenum from each com0com port's UpperFilters. Takes effect on the
# devnode's next restart (the rename restart picks it up). Read/write unaffected.
$p='HKLM:\SYSTEM\CurrentControlSet\Enum\COM0COM\PORT\CNCA<n>'
Set-ItemProperty $p UpperFilters ([string[]]@()) -Type MultiString
```

Do this **before** any re-enumeration so the rebuild can't trigger the probe.

## ComDB

`HKLM\SYSTEM\CurrentControlSet\Control\COM Name Arbiter\ComDB` is a bitmap of
COM numbers Windows considers "in use" (bit N-1 = COMn). A stale reservation
makes an install/rename hit "name already in use". `setupc change` releases the
old name and claims the new one for you; if doing it by hand, release the target
name first.

## Read-only diagnosis (paste-ready)

```powershell
# Class + live name + filters for every com0com port (the ground truth)
Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Enum\COM0COM\PORT' | ForEach-Object {
  $k=$_; $p=Get-ItemProperty $k.PSPath
  $dp=Get-ItemProperty "$($k.PSPath)\Device Parameters" -EA SilentlyContinue
  [pscustomobject]@{ Sub=$k.PSChildName; Class=$p.Class; Live=$dp.PortName;
    Friendly=$p.FriendlyName; UpperFilters=($p.UpperFilters -join ',') }
} | Format-Table -Auto

# Staged vs live for one pair
Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\com0com\Parameters\CNCA<n>' |
  Select PortName,RealPortName,EmuBR,EmuOverrun

# Does Windows actually list it?  (necessary but NOT sufficient — also check Class above)
[System.IO.Ports.SerialPort]::GetPortNames()
```

## Failure → fix quick reference

| Symptom | Likely cause | Fix |
|---|---|---|
| Port in registry/`GetPortNames` but IDE can't open it | device in **CNCPorts** class, migration unfinished | `setupc remove <n>` then `install <n> PortName=COM#` (placeholder triggers co-installer), then `change` to rename |
| Wrong COM number (e.g. COM17 not COM48); "duplicate" in Device Manager | install auto-named it; rename deferred after reboot-required | re-issue `setupc change CNCA<n> RealPortName=COM48` once the device is stable; confirm live name |
| "Reboot required" | rename staged, co-installer deferred | re-run `setupc change` on the now-stable device — applies in-session, no reboot |
| Mouse jumps / phantom "Microsoft Serial Mouse" during install | serenum mis-detection + sermouse | disable `sermouse` (Start=4), strip `serenum` UpperFilter; then re-enumerate |
| `change`/`install` seems to hang | unsigned-driver `WerFault` crash dialog (session 0) | kill `WerFault`/`WerFaultSecure` only — **never** kill `DrvInst` (it's the real Ports-class installer; killing it aborts the migration → CNCPorts) |
| Garbled serial after flashing a board | crystal mismatch (e.g. TTGO 26 MHz vs 40 MHz firmware) | not a com0com issue — rebuild firmware for the correct board target |

## EmuBR / EmuOverrun

Normalise to `EmuBR` **on** (`0xFFFFFFFF`), `EmuOverrun` **off** (`0`). EmuOverrun
on makes com0com emulate a UART receive overrun and silently **discard** bytes
when the reader lags, corrupting esptool sync/flash; off makes it buffer/flow-
control so no data is lost.

## See also

- `src/WorkbenchBridge.Service/Provisioner.cs` / `Com0comRegistry.cs` — the code
  that automates all of the above (plan → install → strip serenum → rename →
  verify-live → self-heal).
