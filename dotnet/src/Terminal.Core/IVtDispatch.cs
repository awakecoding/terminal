using System.Text;

namespace Microsoft.Terminal.Core;

public interface IVtDispatch
{
    void Print(Rune rune);
    void ExecuteC0(byte control);
    void EscDispatch(char final, byte intermediate);
    void CsiDispatch(char final, ReadOnlySpan<int> parameters, byte intermediate, bool privateMarker);
    void CsiDispatch(char final, ReadOnlySpan<int> parameters, byte intermediate, byte privateMarker) =>
        CsiDispatch(final, parameters, intermediate, privateMarker != 0);
    void OscDispatch(int command, ReadOnlySpan<char> data);
}
