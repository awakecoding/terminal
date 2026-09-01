using System.Text;
using Microsoft.Terminal.Core;
using Xunit;

namespace Terminal.Core.Tests;

public sealed class DcsParserTests
{
    [Fact]
    public void DispatchesParametersIntermediatesAndPayload()
    {
        var dispatch = new RecordingDispatch();
        var parser = new VtParser(dispatch);

        parser.Process("\u001bP1;;3$qm\u001b\\"u8);

        var dcs = Assert.Single(dispatch.Dcs);
        Assert.Equal('q', dcs.Final);
        Assert.Equal([1, -1, 3], dcs.Parameters);
        Assert.Equal([(byte)'$'], dcs.Intermediates);
        Assert.Equal("m", Encoding.ASCII.GetString(dcs.Data));
    }

    [Fact]
    public void EveryChunkBoundaryMatchesSingleFeed()
    {
        var bytes = "\u001bP7;2q#1;2;100;0;0!3~-$\u001b\\"u8.ToArray();
        var expected = Parse(bytes);

        for (var split = 0; split <= bytes.Length; split++)
        {
            var dispatch = new RecordingDispatch();
            var parser = new VtParser(dispatch);
            parser.Process(bytes.AsSpan(0, split));
            parser.Process(bytes.AsSpan(split));
            AssertDcsEqual(expected, Assert.Single(dispatch.Dcs));
        }
    }

    [Fact]
    public void DeterministicRandomChunkingMatchesSingleFeed()
    {
        var bytes = "\u001bP1;2qfirst\u001b\\text\u001bP$qm\u001b\\"u8.ToArray();
        var expectedDispatch = new RecordingDispatch();
        new VtParser(expectedDispatch).Process(bytes);

        for (var seed = 0; seed < 64; seed++)
        {
            var random = new Random(seed);
            var actualDispatch = new RecordingDispatch();
            var parser = new VtParser(actualDispatch);
            for (var offset = 0; offset < bytes.Length;)
            {
                var count = Math.Min(random.Next(1, 8), bytes.Length - offset);
                parser.Process(bytes.AsSpan(offset, count));
                offset += count;
            }

            Assert.Equal(expectedDispatch.Dcs.Count, actualDispatch.Dcs.Count);
            for (var index = 0; index < expectedDispatch.Dcs.Count; index++)
            {
                AssertDcsEqual(expectedDispatch.Dcs[index], actualDispatch.Dcs[index]);
            }

            Assert.Equal(expectedDispatch.Printed.ToString(), actualDispatch.Printed.ToString());
        }
    }

    [Fact]
    public void C1DcsAndStringTerminatorAreAccepted()
    {
        var dispatch = new RecordingDispatch();
        var parser = new VtParser(dispatch);

        parser.Process([0x90, (byte)'1', (byte)'q', (byte)'~', 0x9C]);

        var dcs = Assert.Single(dispatch.Dcs);
        Assert.Equal([1], dcs.Parameters);
        Assert.Equal("~", Encoding.ASCII.GetString(dcs.Data));
    }

    [Theory]
    [InlineData(0x18)]
    [InlineData(0x1A)]
    public void CancellationAbortsDcsAndReturnsToGround(byte cancel)
    {
        var dispatch = new RecordingDispatch();
        var parser = new VtParser(dispatch);
        parser.Process([(byte)'\u001b', (byte)'P', (byte)'q', (byte)'~', cancel, (byte)'X']);

        Assert.Empty(dispatch.Dcs);
        Assert.Equal("X", dispatch.Printed.ToString());
        Assert.Contains(cancel, dispatch.Controls);
    }

    [Fact]
    public void NonTerminatingEscapeAbortsDcsAndStartsNewEscapeSequence()
    {
        var dispatch = new RecordingDispatch();
        var parser = new VtParser(dispatch);

        parser.Process("\u001bPqdata\u001b[31mX"u8);

        Assert.Empty(dispatch.Dcs);
        Assert.Single(dispatch.Csi);
        Assert.Equal("X", dispatch.Printed.ToString());
    }

    [Fact]
    public void EscapeDuringDcsReprocessesControls()
    {
        var dispatch = new RecordingDispatch();
        var parser = new VtParser(dispatch);

        parser.Process("\u001bPqdata\u001b\u0007[31mX"u8);

        Assert.Empty(dispatch.Dcs);
        Assert.Contains((byte)0x07, dispatch.Controls);
        Assert.Single(dispatch.Csi);
        Assert.Equal("X", dispatch.Printed.ToString());
    }

    [Fact]
    public void RepeatedEscapeDuringDcsStartsNewEscapeSequence()
    {
        var dispatch = new RecordingDispatch();
        var parser = new VtParser(dispatch);

        parser.Process("\u001bPqdata\u001b\u001b[31mY"u8);

        Assert.Empty(dispatch.Dcs);
        Assert.Single(dispatch.Csi);
        Assert.Equal("Y", dispatch.Printed.ToString());
    }

    [Fact]
    public void BelDoesNotTerminateDcs()
    {
        var dispatch = new RecordingDispatch();
        var parser = new VtParser(dispatch);

        parser.Process("\u001bPqA\u0007B\u001b\\"u8);

        var dcs = Assert.Single(dispatch.Dcs);
        Assert.Equal("AB", Encoding.ASCII.GetString(dcs.Data));
        Assert.Contains((byte)0x07, dispatch.Controls);
    }

    [Fact]
    public void PayloadLimitDropsSequenceAndRecovers()
    {
        var dispatch = new RecordingDispatch();
        var parser = new VtParser(dispatch);
        parser.Process("\u001bPq"u8);
        var payload = new byte[TerminalImageLimits.MaximumDcsPayloadBytes + 1];
        Array.Fill(payload, (byte)'A');
        parser.Process(payload);
        parser.Process("\u001b\\X"u8);

        Assert.Empty(dispatch.Dcs);
        Assert.Equal("X", dispatch.Printed.ToString());
    }

    [Fact]
    public void ColonInDcsParametersIgnoresSequence()
    {
        var dispatch = new RecordingDispatch();
        var parser = new VtParser(dispatch);

        parser.Process("\u001bP1:2qdata\u001b\\X"u8);

        Assert.Empty(dispatch.Dcs);
        Assert.Equal("X", dispatch.Printed.ToString());
    }

    private static DcsRecord Parse(byte[] bytes)
    {
        var dispatch = new RecordingDispatch();
        new VtParser(dispatch).Process(bytes);
        return Assert.Single(dispatch.Dcs);
    }

    private static void AssertDcsEqual(DcsRecord expected, DcsRecord actual)
    {
        Assert.Equal(expected.Final, actual.Final);
        Assert.Equal(expected.Parameters, actual.Parameters);
        Assert.Equal(expected.Intermediates, actual.Intermediates);
        Assert.Equal(expected.PrivateMarker, actual.PrivateMarker);
        Assert.Equal(expected.Data, actual.Data);
    }

    private sealed record DcsRecord(
        char Final,
        int[] Parameters,
        byte[] Intermediates,
        byte PrivateMarker,
        byte[] Data);

    private sealed record CsiRecord(char Final, int[] Parameters);

    private sealed class RecordingDispatch : IVtDispatch
    {
        public List<DcsRecord> Dcs { get; } = [];
        public List<CsiRecord> Csi { get; } = [];
        public List<byte> Controls { get; } = [];
        public StringBuilder Printed { get; } = new();

        public void Print(Rune rune) => Printed.Append(rune);

        public void ExecuteC0(byte control) => Controls.Add(control);

        public void EscDispatch(char final, byte intermediate)
        {
        }

        public void CsiDispatch(char final, ReadOnlySpan<int> parameters, byte intermediate, bool privateMarker) =>
            Csi.Add(new CsiRecord(final, parameters.ToArray()));

        public void DcsDispatch(
            char final,
            ReadOnlySpan<int> parameters,
            ReadOnlySpan<byte> intermediates,
            byte privateMarker,
            ReadOnlySpan<byte> data) =>
            Dcs.Add(new DcsRecord(
                final,
                parameters.ToArray(),
                intermediates.ToArray(),
                privateMarker,
                data.ToArray()));

        public void OscDispatch(int command, ReadOnlySpan<char> data)
        {
        }
    }
}
