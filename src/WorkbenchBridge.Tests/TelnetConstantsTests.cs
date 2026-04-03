using WorkbenchBridge.Rfc2217;
using Xunit;

namespace WorkbenchBridge.Tests;

/// <summary>
/// Verify RFC 2217 constants match the specification.
/// </summary>
public class TelnetConstantsTests
{
    [Fact]
    public void IAC_Is_0xFF()
    {
        Assert.Equal(255, TelnetConstants.IAC);
    }

    [Fact]
    public void ComPortOption_Is_44()
    {
        Assert.Equal(44, TelnetConstants.OPT_COM_PORT);
    }

    [Fact]
    public void ServerResponseOffset_Is_100()
    {
        Assert.Equal(100, TelnetConstants.CPO_SERVER_OFFSET);
    }

    [Fact]
    public void ServerSetBaudRate_Is_ClientPlusOffset()
    {
        Assert.Equal(
            TelnetConstants.CPO_SET_BAUDRATE + TelnetConstants.CPO_SERVER_OFFSET,
            TelnetConstants.CPO_SERVER_SET_BAUDRATE);
    }

    [Fact]
    public void ServerSetControl_Is_ClientPlusOffset()
    {
        Assert.Equal(
            TelnetConstants.CPO_SET_CONTROL + TelnetConstants.CPO_SERVER_OFFSET,
            TelnetConstants.CPO_SERVER_SET_CONTROL);
    }

    [Fact]
    public void DTR_Control_Values_Are_Correct()
    {
        Assert.Equal(8, TelnetConstants.CONTROL_DTR_ON);
        Assert.Equal(9, TelnetConstants.CONTROL_DTR_OFF);
    }

    [Fact]
    public void RTS_Control_Values_Are_Correct()
    {
        Assert.Equal(11, TelnetConstants.CONTROL_RTS_ON);
        Assert.Equal(12, TelnetConstants.CONTROL_RTS_OFF);
    }

    [Fact]
    public void ModemState_CTS_DSR_Bits_Correct()
    {
        Assert.Equal(0x10, TelnetConstants.MODEM_CTS);
        Assert.Equal(0x20, TelnetConstants.MODEM_DSR);
    }
}
