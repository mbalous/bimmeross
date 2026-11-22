using System.Text.RegularExpressions;
using BmwCoder.Core.Models;
using Terminal.Gui;

namespace BmwCoder.Tui;

internal sealed class LocalToolsDialog : Dialog
{
    private readonly AppSettings _settings;
    private readonly string _settingsPath;
    private readonly TextField _ista;
    private readonly TextField _data;
    private readonly TextField _java;
    private readonly TextField _esys;
    private readonly TextField _series;

    internal LocalToolsDialog(AppSettings settings, string settingsPath, DetectedTools detected)
        : base("Local tools setup", 78, 22)
    {
        _settings = settings;
        _settingsPath = settingsPath;
        Add(new Label("Detected paths are layout-checked, not compatibility-tested.")
            { X = 1, Y = 0, Width = Dim.Fill(1) });
        Add(new Label("Blank = not found. Edit paths or skip; no vehicle connection is made.")
            { X = 1, Y = 1, Width = Dim.Fill(1) });
        _ista = Field("ISTA root", detected.Ista, 3);
        _data = Field("PSdZData root", detected.PsdzData, 5);
        _java = Field("Java 8 x64", detected.Java, 7);
        _esys = Field("E-Sys (optional)", detected.Esys, 9);
        _series = Field("Main series", settings.PsdzSeries, 11);
        Add(new Label(detected.BridgePresent ? "Packaged PSdZ helper: present" : "Packaged PSdZ helper: MISSING; coding reads unavailable")
            { X = 1, Y = 13, Width = Dim.Fill(1) });
        bool omitted = new[] { (settings.IstaPath, detected.Ista), (settings.PsdzDataPath, detected.PsdzData),
            (settings.JavaPath, detected.Java), (settings.EsysPath, detected.Esys) }
            .Any(pair => !string.IsNullOrWhiteSpace(pair.Item1) && !SamePath(pair.Item1, pair.Item2));
        Add(new Label(omitted ? "Unverified saved paths omitted. Skip keeps existing settings." :
            "Use applies for this session; Save also persists the settings.")
            { X = 1, Y = 14, Width = Dim.Fill(1) });
        if (detected.Warnings.Length > 0)
        {
            Add(new Label("Some locations could not be inspected; choose Details.")
                { X = 1, Y = 15, Width = Dim.Fill(1) });
            var details = new Button("Details");
            details.Clicked += () => MessageBox.Query("Detection warnings", string.Join("\n", detected.Warnings), "OK");
            AddButton(details);
        }
        var use = new Button("Use", true);
        use.Clicked += () => Apply(save: false);
        var save = new Button("Save");
        save.Clicked += () => Apply(save: true);
        var skip = new Button("Skip");
        skip.Clicked += () => Application.RequestStop();
        AddButton(use);
        AddButton(save);
        AddButton(skip);
    }

    private static bool SamePath(string saved, string detected)
    {
        try { return string.Equals(Path.GetFullPath(saved), detected, StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException) { return false; }
    }

    private TextField Field(string label, string value, int y)
    {
        var input = new TextField(value) { X = 18, Y = y, Width = Dim.Fill(2) };
        Add(new Label(label) { X = 1, Y = y }, input);
        return input;
    }

    private void Apply(bool save)
    {
        try
        {
            string ista = _ista.Text.ToString()!.Trim();
            string data = _data.Text.ToString()!.Trim();
            string java = _java.Text.ToString()!.Trim();
            string esys = _esys.Text.ToString()!.Trim();
            string series = _series.Text.ToString()!.Trim().ToUpperInvariant();
            ValidatePaths(ista, data, java, esys, series);

            var updated = new AppSettings
            {
                Host = _settings.Host, Port = _settings.Port, LastVin = _settings.LastVin,
                AdditionalSettings = _settings.AdditionalSettings,
                IstaPath = ista, PsdzDataPath = data, JavaPath = java, EsysPath = esys, PsdzSeries = series
            };
            if (save)
                updated.SaveTo(_settingsPath);
            _settings.IstaPath = ista;
            _settings.PsdzDataPath = data;
            _settings.JavaPath = java;
            _settings.EsysPath = esys;
            _settings.PsdzSeries = series;
            Application.RequestStop();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or FormatException)
        {
            MessageBox.ErrorQuery("Local tools setup", ex.Message, "OK");
        }
    }

    internal static void ValidatePaths(string ista, string data, string java, string esys, string series)
    {
        if (ista.Length > 0 && !LocalToolDiscovery.IsIsta(ista))
            throw new FormatException("ISTA must contain the PSdZ API, native libraries, providers and metadata.");
        if (data.Length > 0 && !LocalToolDiscovery.IsPsdzData(data))
            throw new FormatException("PSdZData must contain at least one CAF in swe\\cafd.");
        if (java.Length > 0 && !LocalToolDiscovery.IsJava8(java))
            throw new FormatException("Java must be a 64-bit Java 8 java.exe with matching release metadata.");
        if (esys.Length > 0 && !LocalToolDiscovery.IsEsys(esys))
            throw new FormatException("E-Sys must contain its Java executable and PSdZ API/core jars.");
        if (!Regex.IsMatch(series, @"\A[A-Z][0-9A-Z]{3}\z"))
            throw new FormatException("Main series must be four letters or digits, starting with a letter (for example F020).");
    }
}
