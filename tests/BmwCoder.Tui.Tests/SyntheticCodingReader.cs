using BmwCoder.Core.Diagnostics;
using BmwCoder.Core.Models;
using BmwCoder.Core.Workspace;

namespace BmwCoder.Tui.Tests;

internal sealed class SyntheticCodingReader : ICodingReader
{
    public const string Caf = "cafd_00000001.caf.001_002_003";
    public CodingReadRequest? LastRequest { get; private set; }
    public bool Fail { get; set; }
    public Action? OnRead { get; set; }

    public Task<DecodedCoding> ReadAsync(CodingReadRequest request, CancellationToken token)
    {
        LastRequest = request;
        OnRead?.Invoke();
        return Result(request.Ecu, request.ExpectedVin);
    }

    public Task<DecodedCoding> DecodeAsync(string path, string caf, CodingReadRequest settings, CancellationToken token) =>
        Result(null, null);

    private Task<DecodedCoding> Result(int? ecu, string? vin)
    {
        if (Fail)
            throw new FormatException("Synthetic CAF mismatch.");
        const string xml = """
            <caf><sweHeader><cafIdent><processClass>CAFD</processClass><id>00000001</id><mainVersion>001</mainVersion><subVersion>002</subVersion><patchVersion>003</patchVersion></cafIdent></sweHeader>
            <data><datagroup address="3000">
            <function name="synthetic-feature"><storageInfo><start>0</start><end>0</end><mask>00000001b</mask></storageInfo><interpretation><paramName>enabled</paramName><values><unitVal position="0">01</unitVal></values></interpretation><defVal><unitVal position="0">00</unitVal></defVal></function>
            <function name="."><storageInfo><start>0</start><end>0</end><mask>00000010b</mask></storageInfo></function>
            </datagroup></data></caf>
            """;
        return Task.FromResult(DecodedCoding.Parse(Caf, "synthetic-audio", vin, ecu, "synthetic-runtime",
            "synthetic-original", xml, new[] { new CodingBlock(0x3000, "01"), new CodingBlock(0x3001, "AABB") }, new[] { 0x3000 }));
    }
}

internal sealed class SyntheticLiveVehicle : IReadOnlyVehicle
{
    public bool IsDemo => false;
    public bool IsConnected { get; private set; }
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public string BackupDirectory => "synthetic-backups";
    public string? Vin => IsConnected ? "WBATEST0000000001" : null;
    public IReadOnlyList<string> Warnings => Array.Empty<string>();
    public Task ConnectAsync(string host, int port, CancellationToken token) { IsConnected = true; return Task.CompletedTask; }
    public Task<IReadOnlyList<EcuInfo>> DiscoverAsync(CancellationToken token) =>
        Task.FromResult<IReadOnlyList<EcuInfo>>(new[] { new EcuInfo { Address = 0x63, Name = "synthetic-audio" } });
    public Task<DiagnosticReading> ReadDidAsync(byte ecu, ushort did, CancellationToken token) =>
        Task.FromResult(new DiagnosticReading { Ecu = ecu, Did = did, Name = "synthetic", RequestHex = $"22{did:X4}", PayloadHex = "01" });
    public Task<DiagnosticReading> ReadFaultsAsync(byte ecu, CancellationToken token) => throw new NotSupportedException();
    public Task<DiagnosticReading> ReadRequestAsync(byte ecu, byte[] request, CancellationToken token) => throw new NotSupportedException();
    public Task<DiagnosticReading> ProbeAsync(byte ecu, CancellationToken token) => throw new NotSupportedException();
    public Task<IReadOnlyList<DiagnosticReading>> ReadDmeReportAsync(IProgress<DiagnosticReading>? progress, CancellationToken token) => throw new NotSupportedException();
    public Task<IReadOnlyList<DiagnosticDefinition>> LoadDiagnosticDataAsync(string path, string series, byte ecu, CancellationToken token) => throw new NotSupportedException();
    public void ConfigureFaultTexts(string path) => throw new NotSupportedException();
    public Task<ReadOnlyBackup> BackupSuggestedAsync(IReadOnlyList<EcuInfo> ecus, IProgress<ReadOnlyBackup>? progress, CancellationToken token) => throw new NotSupportedException();
    public void Disconnect() => IsConnected = false;
    public void Dispose() => Disconnect();
}
