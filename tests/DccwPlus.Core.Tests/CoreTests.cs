using System.Globalization;
using System.Text;
using DccwPlus.Core.Calibration;
using DccwPlus.Core.Localization;
using DccwPlus.Core.Presets;
using DccwPlus.Core.Storage;
using Xunit;

namespace DccwPlus.Core.Tests;

public sealed class CoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dccwpp-tests", Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public void ImportKeepsIntentAndCreatesANewIdentity()
    {
        var original = CalibrationPreset.Create("夜间 / Night", new() { Gamma = 2.35, RedGain = 0.9 });
        var store = new PresetStore(_root); store.Add(original);
        string exported = Path.Combine(_root, "shared.json"); store.Export(original.Id, exported);
        var imported = store.Import(exported);
        Assert.NotEqual(original.Id, imported.Id);
        Assert.Equal(original.Calibration, imported.Calibration);
        Assert.Equal(original.Name, imported.Name);
        Assert.Equal(2, store.List().Presets.Count);
    }
    [Fact]
    public void ExportDoesNotOverwriteExistingFile()
    {
        var store = new PresetStore(_root); var preset = CalibrationPreset.Create("Draft"); store.Add(preset);
        string destination = Path.Combine(_root, "existing.json"); File.WriteAllText(destination, "preserve me");
        Assert.Throws<IOException>(() => store.Export(preset.Id, destination));
        Assert.Equal("preserve me", File.ReadAllText(destination));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp"));
    }
    [Theory]
    [InlineData("\"schemaVersion\": 1", "\"schemaVersion\": 99")]
    [InlineData("\"gamma\": 2.2", "\"gamma\": 100")]
    [InlineData("\"colorMode\": \"sdr\"", "\"colorMode\": \"hdr\"")]
    [InlineData("\"name\": \"Draft\"", "\"name\": null")]
    [InlineData("\"gamma\": 2.2", "\"command\": \"malicious.exe\", \"gamma\": 2.2")]
    [InlineData("\"gamma\": 2.2", "\"gamma\": 2.2, \"gamma\": 2.8")]
    public void InvalidOrUnknownPresetDataIsRejected(string from, string to)
    {
        string document = Encoding.UTF8.GetString(PresetCodec.Encode(CalibrationPreset.Create("Draft")));
        Assert.Contains(from, document);
        Assert.Throws<InvalidDataException>(() => PresetCodec.Decode(Encoding.UTF8.GetBytes(document.Replace(from, to))));
    }
    [Fact] public void OversizedInputIsRejectedBeforeParsing() => Assert.Throws<InvalidDataException>(() => PresetCodec.Decode(new byte[PresetCodec.MaxBytes + 1]));
    [Fact]
    public void AFileCannotImpersonateAnotherPreset()
    {
        var store = new PresetStore(_root); var original = CalibrationPreset.Create("Original"); store.Add(original);
        File.WriteAllBytes(Path.Combine(_root, "presets", $"{original.Id:D}.dccw.json"), PresetCodec.Encode(CalibrationPreset.Create("Different")));
        Assert.Throws<InvalidDataException>(() => store.Get(original.Id));
        Assert.Empty(store.List().Presets); Assert.Single(store.List().InvalidFiles);
    }
    [Fact]
    public void BrokenPresetDoesNotHideHealthyPresets()
    {
        var store = new PresetStore(_root); store.Add(CalibrationPreset.Create("Healthy"));
        File.WriteAllText(Path.Combine(_root, "presets", "broken.dccw.json"), "{");
        Assert.Single(store.List().Presets); Assert.Single(store.List().InvalidFiles);
    }
    [Fact]
    public void CatalogsHaveTheSameKeysAndNoBlankTranslations()
    {
        var text = new LocalizationService(); var keys = text.GetCatalog("en-US").Keys.Order().ToArray();
        foreach (string tag in LocalizationService.Languages)
        {
            Assert.Equal(keys, text.GetCatalog(tag).Keys.Order().ToArray());
            Assert.All(text.GetCatalog(tag).Values, value => Assert.False(string.IsNullOrWhiteSpace(value)));
        }
    }
    [Theory]
    [InlineData("zh-TW", "zh-Hant")]
    [InlineData("zh-HK", "zh-Hant")]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("de-DE", "en-US")]
    public void AutomaticLanguageResolvesRegionalVariants(string culture, string expected) => Assert.Equal(expected, LocalizationService.Resolve("system", CultureInfo.GetCultureInfo(culture)));
    [Fact]
    public void SwitchingLanguageNotifiesExistingBindings()
    {
        var text = new LocalizationService(); List<string?> changes = [];
        text.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        text.Language = "zh-Hant";
        Assert.Equal("設定", text["Settings"]); Assert.Contains("Item[]", changes);
    }
    [Fact]
    public void PlanNeverReportsAnUnimplementedCalibrationAsApplied()
    {
        var service = new CalibrationService(new FakeDisplays()); var preset = CalibrationPreset.Create("Draft");
        var plan = service.Plan(preset, "monitor-1");
        Assert.False(plan.CanApply); Assert.Equal("calibration_backend_pending", plan.ReasonCode);
        Assert.Throws<KeyNotFoundException>(() => service.Plan(preset, "missing"));
    }
    private sealed class FakeDisplays : IDisplayService
    { public IReadOnlyList<DisplayInfo> Enumerate() => [new("monitor-1", "Monitor", "display-1", true, "not-probed")]; }
}
