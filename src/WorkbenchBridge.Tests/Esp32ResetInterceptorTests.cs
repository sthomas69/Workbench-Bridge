using WorkbenchBridge.Rfc2217;
using Xunit;

namespace WorkbenchBridge.Tests;

/// <summary>
/// Tests for <see cref="Esp32ResetInterceptor"/> — the state machine that decides,
/// on a native USB-Serial-JTAG slot, whether the host's classic DTR/RTS activity
/// is a download-mode entry reset, an end-of-flash hard reset, or unrelated.
///
/// Line convention (esptool/reset.py): DTR drives IO0/BOOT, RTS drives EN; asserted
/// (true) pulls the line low. Samples below are written as (dtr, rts).
/// </summary>
public class Esp32ResetInterceptorTests
{
    /// <summary>Feed a sequence of (dtr, rts) samples 5ms apart and return every action.</summary>
    private static List<ResetInterceptAction> Run(
        Esp32ResetInterceptor sut, params (bool dtr, bool rts)[] samples)
    {
        var actions = new List<ResetInterceptAction>();
        long t = 0;
        foreach (var (dtr, rts) in samples)
        {
            actions.Add(sut.Observe(dtr, rts, t));
            t += 5;
        }
        return actions;
    }

    [Fact]
    public void FirstSample_IsBaseline_PassThrough()
    {
        var sut = new Esp32ResetInterceptor();
        Assert.Equal(ResetInterceptAction.PassThrough, sut.Observe(false, false, 0));
    }

    [Fact]
    public void ClassicReset_AssertsBoot_SynthesizesDownloadReset()
    {
        // esptool ClassicReset: D0 R1 | W0.1 | D1 R0 | W0.05 | D0
        var sut = new Esp32ResetInterceptor();
        var actions = Run(sut,
            (false, false),  // baseline (idle)
            (false, true),   // RTS asserted -> burst start
            (false, true),   // hold (EN low ~100ms)
            (true,  true),   // DTR asserted (IO0 low) just before EN release
            (true,  false),  // RTS released -> resolve: BOOT was low => download
            (false, false)); // trailing DTR clear

        Assert.Equal(ResetInterceptAction.PassThrough, actions[0]);
        Assert.Equal(ResetInterceptAction.Suppress, actions[1]);
        Assert.Equal(ResetInterceptAction.Suppress, actions[2]);
        Assert.Equal(ResetInterceptAction.Suppress, actions[3]);
        Assert.Equal(ResetInterceptAction.SynthesizeDownloadReset, actions[4]);
        // After resolving, the burst is over; trailing edges pass through.
        Assert.Equal(ResetInterceptAction.PassThrough, actions[5]);
        Assert.False(sut.InBurst);
    }

    [Fact]
    public void ClassicReset_BootAndEnReleasedTogether_StillDownload()
    {
        // If the poll catches DTR=1 and RTS=0 in the SAME sample (esptool sets them
        // microseconds apart), it must still classify as a download reset.
        var sut = new Esp32ResetInterceptor();
        var actions = Run(sut,
            (false, false),
            (false, true),   // burst start
            (true,  false)); // BOOT low + EN released in one sample

        Assert.Equal(ResetInterceptAction.SynthesizeDownloadReset, actions[2]);
    }

    [Fact]
    public void HardReset_NeverAssertsBoot_SynthesizesHardReset()
    {
        // esptool HardReset: R1 | W0.1 | R0  (DTR/IO0 stays high throughout)
        var sut = new Esp32ResetInterceptor();
        var actions = Run(sut,
            (false, false),  // baseline
            (false, true),   // RTS asserted -> burst start
            (false, true),   // hold
            (false, false)); // RTS released, BOOT never low => hard reset

        Assert.Equal(ResetInterceptAction.Suppress, actions[1]);
        Assert.Equal(ResetInterceptAction.Suppress, actions[2]);
        Assert.Equal(ResetInterceptAction.SynthesizeHardReset, actions[3]);
    }

    [Fact]
    public void MonitorOpen_DtrToggleWithoutRtsAssert_PassesThrough()
    {
        // Opening a serial monitor toggles DTR but never pulls EN low; this is not
        // a reset and must be forwarded verbatim.
        var sut = new Esp32ResetInterceptor();
        var actions = Run(sut,
            (false, false),  // baseline
            (true,  false),  // DTR asserted, RTS idle
            (false, false)); // DTR cleared

        Assert.All(actions, a => Assert.Equal(ResetInterceptAction.PassThrough, a));
        Assert.False(sut.InBurst);
    }

    [Fact]
    public void MonitorHold_RtsAssertedPastWindow_NeverResets()
    {
        // A serial monitor (PuTTY / Arduino IDE) opens by asserting DTR+RTS and
        // HOLDING them. On a usb-jtag slot that must NOT be read as a reset — it
        // would otherwise drop the chip into download mode. The held burst is
        // abandoned silently once it outlives the burst window.
        var sut = new Esp32ResetInterceptor();
        Assert.Equal(ResetInterceptAction.PassThrough, sut.Observe(false, false, 0));
        Assert.Equal(ResetInterceptAction.Suppress, sut.Observe(true, true, 5)); // monitor asserts both
        Assert.Equal(ResetInterceptAction.Suppress, sut.Observe(true, true, 500));
        // >= 1000ms still held: a monitor, not a reset => no synthesis, just no-op.
        Assert.Equal(ResetInterceptAction.Suppress, sut.Observe(true, true, 1005));
        Assert.False(sut.InBurst);
        // Continued holding stays a no-op (no new burst, no reset).
        Assert.Equal(ResetInterceptAction.PassThrough, sut.Observe(true, true, 2000));
    }

    [Fact]
    public void MonitorOpenThenClose_HeldBothLines_NeverResets()
    {
        // Full monitor lifecycle: assert+hold on open, release on close. Neither
        // edge may synthesise a reset on a usb-jtag slot.
        var sut = new Esp32ResetInterceptor();
        var actions = Run(sut,
            (false, false),                  // baseline (idle)
            (true,  true),                   // monitor open: assert both
            (true,  true));                  // hold...
        // Long hold past the window, then the monitor closes (releases both).
        actions.Add(sut.Observe(true,  true,  1005)); // window exceeded -> abandon
        actions.Add(sut.Observe(false, false, 1010)); // monitor close: release

        Assert.DoesNotContain(ResetInterceptAction.SynthesizeDownloadReset, actions);
        Assert.DoesNotContain(ResetInterceptAction.SynthesizeHardReset, actions);
        Assert.False(sut.InBurst);
    }

    [Fact]
    public void TwoConsecutiveResets_EachResolveIndependently()
    {
        // A flash cycle drives a download reset, then (after writing) a hard reset.
        var sut = new Esp32ResetInterceptor();
        var actions = Run(sut,
            (false, false),  // baseline
            // download reset
            (false, true),
            (true,  true),
            (true,  false),
            (false, false),
            // hard reset
            (false, true),
            (false, true),
            (false, false));

        Assert.Equal(ResetInterceptAction.SynthesizeDownloadReset, actions[3]);
        Assert.Equal(ResetInterceptAction.SynthesizeHardReset, actions[7]);
    }

    [Fact]
    public void Reset_AbandonsInProgressBurst()
    {
        var sut = new Esp32ResetInterceptor();
        sut.Observe(false, false, 0);
        sut.Observe(false, true, 5);   // burst start
        Assert.True(sut.InBurst);

        sut.Reset();
        Assert.False(sut.InBurst);

        // After Reset() the next sample is a fresh baseline (PassThrough), not a burst.
        Assert.Equal(ResetInterceptAction.PassThrough, sut.Observe(false, true, 10));
    }
}
