namespace WorkbenchBridge.Rfc2217;

/// <summary>
/// Telnet protocol constants per RFC 854 and RFC 2217.
/// </summary>
public static class TelnetConstants
{
    // Telnet commands (RFC 854)
    public const byte IAC  = 255; // Interpret As Command
    public const byte DONT = 254;
    public const byte DO   = 253;
    public const byte WONT = 252;
    public const byte WILL = 251;
    public const byte SB   = 250; // Subnegotiation Begin
    public const byte GA   = 249; // Go Ahead
    public const byte EL   = 248; // Erase Line
    public const byte EC   = 247; // Erase Character
    public const byte AYT  = 246; // Are You There
    public const byte AO   = 245; // Abort Output
    public const byte IP   = 244; // Interrupt Process
    public const byte BRK  = 243; // Break
    public const byte SE   = 240; // Subnegotiation End

    // Telnet options
    public const byte OPT_BINARY    = 0;   // RFC 856 Binary Transmission
    public const byte OPT_ECHO      = 1;   // RFC 857
    public const byte OPT_SGA       = 3;   // RFC 858 Suppress Go Ahead
    public const byte OPT_COM_PORT  = 44;  // RFC 2217 COM Port Control

    // RFC 2217 COM Port subnegotiation commands (client to server)
    public const byte CPO_SET_BAUDRATE     = 1;
    public const byte CPO_SET_DATASIZE     = 2;
    public const byte CPO_SET_PARITY       = 3;
    public const byte CPO_SET_STOPSIZE     = 4;
    public const byte CPO_SET_CONTROL      = 5;
    public const byte CPO_NOTIFY_LINESTATE = 6;
    public const byte CPO_NOTIFY_MODEMSTATE = 7;
    public const byte CPO_FLOWCONTROL_SUSPEND  = 8;
    public const byte CPO_FLOWCONTROL_RESUME   = 9;
    public const byte CPO_SET_LINESTATE_MASK   = 10;
    public const byte CPO_SET_MODEMSTATE_MASK  = 11;
    public const byte CPO_PURGE_DATA           = 12;

    // RFC 2217 COM Port subnegotiation responses (server to client, offset by 100)
    public const byte CPO_SERVER_OFFSET = 100;
    public const byte CPO_SERVER_SET_BAUDRATE      = 101;
    public const byte CPO_SERVER_SET_DATASIZE       = 102;
    public const byte CPO_SERVER_SET_PARITY         = 103;
    public const byte CPO_SERVER_SET_STOPSIZE        = 104;
    public const byte CPO_SERVER_SET_CONTROL         = 105;
    public const byte CPO_SERVER_NOTIFY_LINESTATE    = 106;
    public const byte CPO_SERVER_NOTIFY_MODEMSTATE   = 107;
    public const byte CPO_SERVER_FLOWCONTROL_SUSPEND = 108;
    public const byte CPO_SERVER_FLOWCONTROL_RESUME  = 109;
    public const byte CPO_SERVER_SET_LINESTATE_MASK  = 110;
    public const byte CPO_SERVER_SET_MODEMSTATE_MASK = 111;
    public const byte CPO_SERVER_PURGE_DATA          = 112;

    // SET_CONTROL values (RFC 2217 section 3)
    public const byte CONTROL_REQ_FLOW        = 0;
    public const byte CONTROL_FLOW_NONE       = 1;
    public const byte CONTROL_FLOW_XONXOFF    = 2;
    public const byte CONTROL_FLOW_HARDWARE   = 3;
    public const byte CONTROL_REQ_BREAK       = 4;
    public const byte CONTROL_BREAK_ON        = 5;
    public const byte CONTROL_BREAK_OFF       = 6;
    public const byte CONTROL_REQ_DTR         = 7;
    public const byte CONTROL_DTR_ON          = 8;
    public const byte CONTROL_DTR_OFF         = 9;
    public const byte CONTROL_REQ_RTS         = 10;
    public const byte CONTROL_RTS_ON          = 11;
    public const byte CONTROL_RTS_OFF         = 12;
    public const byte CONTROL_REQ_FLOW_IN     = 13;
    public const byte CONTROL_FLOW_IN_NONE    = 14;
    public const byte CONTROL_FLOW_IN_XONXOFF = 15;
    public const byte CONTROL_FLOW_IN_HARDWARE = 16;

    // Parity values
    public const byte PARITY_REQ  = 0;
    public const byte PARITY_NONE = 1;
    public const byte PARITY_ODD  = 2;
    public const byte PARITY_EVEN = 3;
    public const byte PARITY_MARK = 4;
    public const byte PARITY_SPACE = 5;

    // Stop bit values
    public const byte STOPBITS_REQ = 0;
    public const byte STOPBITS_1   = 1;
    public const byte STOPBITS_2   = 2;
    public const byte STOPBITS_1_5 = 3;

    // Data size values
    public const byte DATASIZE_REQ = 0;
    public const byte DATASIZE_5   = 5;
    public const byte DATASIZE_6   = 6;
    public const byte DATASIZE_7   = 7;
    public const byte DATASIZE_8   = 8;

    // Purge values
    public const byte PURGE_RX = 1;
    public const byte PURGE_TX = 2;
    public const byte PURGE_BOTH = 3;

    // Modem state bits (for NOTIFY_MODEMSTATE)
    public const byte MODEM_DCTS = 0x01; // Delta CTS
    public const byte MODEM_DDSR = 0x02; // Delta DSR
    public const byte MODEM_TERI = 0x04; // Trailing Edge RI
    public const byte MODEM_DDCD = 0x08; // Delta DCD
    public const byte MODEM_CTS  = 0x10;
    public const byte MODEM_DSR  = 0x20;
    public const byte MODEM_RI   = 0x40;
    public const byte MODEM_DCD  = 0x80;

    // Line state bits (for NOTIFY_LINESTATE)
    public const byte LINE_DR    = 0x01; // Data Ready
    public const byte LINE_OE    = 0x02; // Overrun Error
    public const byte LINE_PE    = 0x04; // Parity Error
    public const byte LINE_FE    = 0x08; // Framing Error
    public const byte LINE_BI    = 0x10; // Break Interrupt
    public const byte LINE_THRE  = 0x20; // TX Holding Register Empty
    public const byte LINE_TEMT  = 0x40; // TX Empty
    public const byte LINE_FIFO  = 0x80; // FIFO Error
}
