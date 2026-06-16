namespace WorkbenchBridge.Rfc2217;

/// <summary>
/// What the <see cref="SerialBridge"/> signal loop should do with the current
/// DTR/RTS sample on a native USB-Serial-JTAG slot.
/// </summary>
public enum ResetInterceptAction
{
    /// <summary>Not a reset burst — let the normal verbatim DTR/RTS passthrough run.</summary>
    PassThrough,

    /// <summary>Inside a reset burst — swallow the raw lines; the real sequence is synthesised.</summary>
    Suppress,

    /// <summary>A download-mode entry reset completed (DTR/IO0 was asserted) — emit USBJTAGSerialReset.</summary>
    SynthesizeDownloadReset,

    /// <summary>A plain reset completed (DTR/IO0 never asserted) — emit a USB hard reset (run firmware).</summary>
    SynthesizeHardReset,
}

/// <summary>
/// Detects a host-driven <b>classic</b> DTR/RTS auto-reset on a native
/// USB-Serial-JTAG slot and tells the bridge to re-issue the correct USB reset
/// instead.
///
/// IDEs (Arduino, PlatformIO) always drive esptool's <c>ClassicReset</c> and
/// expose no <c>--before</c> override, so over the bridge a native-USB board
/// would receive the wrong sequence. This interceptor watches the line activity
/// and classifies each reset "burst" by what it does to the BOOT pin:
/// <list type="bullet">
///   <item>DTR (IO0/BOOT) asserted during the burst → download-mode entry
///         (esptool <c>ClassicReset</c>: <c>D0 R1 W0.1 D1 R0 W0.05 D0</c>).</item>
///   <item>DTR never asserted → plain reset (esptool <c>HardReset</c>: <c>R1 W0.1 R0</c>),
///         which the end-of-flash <c>--after hard-reset</c> drives to run firmware.</item>
/// </list>
/// so the bridge can synthesise <c>USBJTAGSerialReset</c> or a USB hard reset
/// respectively.
///
/// Decisions key off the <b>RTS-release edge</b> (EN→HIGH), not on quiescence
/// timing, so the result is independent of esptool's inter-line sleeps and the
/// bridge's poll cadence. Pure and deterministic (timestamps are passed in) so
/// it is fully unit-testable.
///
/// <para><b>Monitors are not resets.</b> A serial monitor (PuTTY, the Arduino
/// IDE) opens by asserting DTR+RTS and <i>holding</i> them for the whole
/// session. On a USB-Serial-JTAG board a held assertion would otherwise be
/// read as DTR=BOOT + RTS=EN and drop the chip into download mode. esptool's
/// real resets, by contrast, <i>pulse</i> RTS and release it within a few
/// hundred milliseconds. So a burst that releases inside
/// <see cref="MaxBurstMs"/> is a genuine reset and is synthesised; a burst that
/// stays asserted past it is a monitor holding the lines and is a <b>no-op</b>
/// (and its eventual release, on monitor close, is also a no-op). The bridge
/// never raw-forwards DTR/RTS on a usb-jtag slot — this interceptor is the only
/// thing that ever drives the device's reset lines.</para>
///
/// Mapping convention (matches esptool/reset.py and the auto-reset circuit):
/// DTR drives IO0/BOOT, RTS drives EN/CHIP_PU; asserted (True) pulls the line LOW.
/// </summary>
public sealed class Esp32ResetInterceptor
{
    /// <summary>
    /// Burst window separating a real esptool reset pulse from a serial monitor
    /// holding the lines. An RTS assertion that releases within this window is a
    /// genuine reset (esptool's longest is ~150&#160;ms classic / ~400&#160;ms
    /// USB hard); one still asserted past it is a monitor connection and is
    /// abandoned without synthesising any reset.
    /// </summary>
    private const long MaxBurstMs = 1000;

    private bool _initialised;
    private bool _lastDtr;
    private bool _lastRts;

    private bool _inBurst;
    private bool _sawDtrAssertedInBurst;
    private long _burstStartMs;

    /// <summary>True while a reset burst is being absorbed (raw lines suppressed).</summary>
    public bool InBurst => _inBurst;

    /// <summary>
    /// Feed one DTR/RTS sample. Call on every signal-poll tick (not only on change)
    /// so the <see cref="MaxBurstMs"/> backstop can fire. <paramref name="nowMs"/> is
    /// a monotonic millisecond clock (e.g. <c>Environment.TickCount64</c>); tests pass
    /// synthetic values.
    /// </summary>
    public ResetInterceptAction Observe(bool dtr, bool rts, long nowMs)
    {
        if (!_initialised)
        {
            _initialised = true;
            _lastDtr = dtr;
            _lastRts = rts;
            return ResetInterceptAction.PassThrough;
        }

        if (!_inBurst)
        {
            // Every reset — classic or hard — begins by pulling EN low (RTS→True).
            if (rts && !_lastRts)
            {
                _inBurst = true;
                _sawDtrAssertedInBurst = dtr;   // ClassicReset may assert IO0 before EN
                _burstStartMs = nowMs;
                _lastDtr = dtr;
                _lastRts = rts;
                return ResetInterceptAction.Suppress;
            }

            _lastDtr = dtr;
            _lastRts = rts;
            return ResetInterceptAction.PassThrough;
        }

        // In a burst: remember if IO0/BOOT was ever pulled low (⇒ download mode).
        if (dtr)
            _sawDtrAssertedInBurst = true;

        bool rtsReleased = !rts && _lastRts;                 // EN→HIGH: reset pulse released
        bool tooLong = (nowMs - _burstStartMs) >= MaxBurstMs;

        _lastDtr = dtr;
        _lastRts = rts;

        if (rtsReleased)
        {
            // RTS released. Within the burst window it is esptool's real reset
            // pulse — synthesise the matching USB reset. Released only after a
            // long hold it is a serial monitor disconnecting, not a reset.
            bool download = _sawDtrAssertedInBurst;
            _inBurst = false;
            _sawDtrAssertedInBurst = false;
            if ((nowMs - _burstStartMs) >= MaxBurstMs)
                return ResetInterceptAction.PassThrough;     // monitor close: no-op
            return download
                ? ResetInterceptAction.SynthesizeDownloadReset
                : ResetInterceptAction.SynthesizeHardReset;
        }

        if (tooLong)
        {
            // RTS asserted and HELD past the burst window: a serial monitor
            // holding the lines, not an esptool reset. Abandon the burst
            // silently and never reset the device. RTS is still asserted, so no
            // new burst starts until it is released and re-asserted.
            _inBurst = false;
            _sawDtrAssertedInBurst = false;
            return ResetInterceptAction.Suppress;
        }

        return ResetInterceptAction.Suppress;
    }

    /// <summary>
    /// Abandon any in-progress burst without emitting — e.g. after an RFC 2217
    /// reconnect, where the observed line baseline is no longer trustworthy.
    /// </summary>
    public void Reset()
    {
        _inBurst = false;
        _sawDtrAssertedInBurst = false;
        _initialised = false;
    }
}
