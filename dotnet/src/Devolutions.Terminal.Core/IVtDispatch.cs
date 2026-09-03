using System.Text;

namespace Devolutions.Terminal.Core;

public interface IVtDispatch
{
    void Print(Rune rune);
    void ExecuteC0(byte control);
    void EscDispatch(char final, byte intermediate);
    void EscDispatch(char final, ReadOnlySpan<byte> intermediates) =>
        EscDispatch(final, intermediates.IsEmpty ? (byte)0 : intermediates[^1]);
    void Vt52Dispatch(char final, byte row = 0, byte column = 0)
    {
    }
    void CsiDispatch(char final, ReadOnlySpan<int> parameters, byte intermediate, bool privateMarker);
    void CsiDispatch(char final, ReadOnlySpan<int> parameters, byte intermediate, byte privateMarker) =>
        CsiDispatch(final, parameters, intermediate, privateMarker != 0);
    void DcsDispatch(
        char final,
        ReadOnlySpan<int> parameters,
        ReadOnlySpan<byte> intermediates,
        byte privateMarker,
        ReadOnlySpan<byte> data)
    {
    }
    void OscDispatch(int command, ReadOnlySpan<char> data);
}
