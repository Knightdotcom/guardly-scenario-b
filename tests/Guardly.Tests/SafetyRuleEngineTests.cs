using Guardly.Api.Models;
using Guardly.Api.Options;
using Guardly.Api.Services;
using Xunit;

// Alias för att slippa krocken mellan namnrymden Guardly.Api.Options och
// den statiska klassen Microsoft.Extensions.Options.Options.
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Guardly.Tests;

/// <summary>
/// Tester för regelmotorn. Det är här Guardlys affärsvärde sitter — Computer Vision
/// levererar taggar, men det är de här reglerna som avgör vad som faktiskt flaggas.
/// Därför är det också den del som absolut måste vara testad.
/// </summary>
public class SafetyRuleEngineTests
{
    private static SafetyRuleEngine CreateEngine(SafetyRuleOptions? options = null) =>
        new(MsOptions.Create(options ?? new SafetyRuleOptions()));

    private static VisionAnalysisResult Analysis(
        (string Name, double Confidence)[]? tags = null,
        (string Name, double Confidence)[]? objects = null)
    {
        var result = new VisionAnalysisResult();

        foreach (var (name, confidence) in tags ?? Array.Empty<(string, double)>())
        {
            result.Tags.Add(new DetectedTag(name, confidence));
        }

        foreach (var (name, confidence) in objects ?? Array.Empty<(string, double)>())
        {
            result.Objects.Add(new DetectedTag(name, confidence));
            if (name == "person")
            {
                result.PeopleCount++;
            }
        }

        return result;
    }

    // -----------------------------------------------------------------------
    // Personigenkänning
    // -----------------------------------------------------------------------

    [Fact]
    public void Utan_person_i_bilden_flaggas_NO_PERSON_DETECTED()
    {
        var engine = CreateEngine();

        var warnings = engine.Evaluate(Analysis(
            tags: new[] { ("construction site", 0.95), ("crane", 0.88), ("sky", 0.80) }));

        Assert.Contains(warnings, w => w.Code == SafetyRuleEngine.CodeNoPerson);

        // Utan person går det inte att bedöma skyddsutrustning — inga PPE-varningar ska finnas.
        Assert.DoesNotContain(warnings, w => w.Code == SafetyRuleEngine.CodeMissingHelmet);
        Assert.DoesNotContain(warnings, w => w.Code == SafetyRuleEngine.CodeMissingVest);
    }

    [Fact]
    public void Person_under_konfidenstroskeln_raknas_inte()
    {
        var engine = CreateEngine(new SafetyRuleOptions { PersonConfidence = 0.60, MinimumConfidence = 0.55 });

        // 0.42 ligger under båda trösklarna.
        var count = engine.CountConfidentPeople(Analysis(objects: new[] { ("person", 0.42) }));

        Assert.Equal(0, count);
    }

    [Fact]
    public void Person_i_taggar_men_inte_i_objekt_raknas_anda()
    {
        var engine = CreateEngine();

        // Objektdetekteringen missade personen, men taggen är säker.
        // Då ska vi hellre granska bilden än släppa igenom den.
        var count = engine.CountConfidentPeople(Analysis(tags: new[] { ("person", 0.91) }));

        Assert.Equal(1, count);
    }

    // -----------------------------------------------------------------------
    // Skyddsutrustning
    // -----------------------------------------------------------------------

    [Fact]
    public void Person_utan_hjalm_ger_hog_varning()
    {
        var engine = CreateEngine();

        var warnings = engine.Evaluate(Analysis(
            tags: new[] { ("construction site", 0.95), ("safety vest", 0.84), ("work boots", 0.72) },
            objects: new[] { ("person", 0.93) }));

        var helmet = Assert.Single(warnings, w => w.Code == SafetyRuleEngine.CodeMissingHelmet);
        Assert.Equal(WarningSeverity.High, helmet.Severity);

        // Västen och skorna syntes, så de ska inte flaggas.
        Assert.DoesNotContain(warnings, w => w.Code == SafetyRuleEngine.CodeMissingVest);
        Assert.DoesNotContain(warnings, w => w.Code == SafetyRuleEngine.CodeMissingBoots);
    }

    [Fact]
    public void Komplett_skyddsutrustning_ger_inga_PPE_varningar()
    {
        var engine = CreateEngine();

        var warnings = engine.Evaluate(Analysis(
            tags: new[]
            {
                ("construction site", 0.96),
                ("helmet", 0.89),
                ("safety vest", 0.86),
                ("work boots", 0.74)
            },
            objects: new[] { ("person", 0.94) }));

        Assert.DoesNotContain(warnings, w => w.Code == SafetyRuleEngine.CodeMissingHelmet);
        Assert.DoesNotContain(warnings, w => w.Code == SafetyRuleEngine.CodeMissingVest);
        Assert.DoesNotContain(warnings, w => w.Code == SafetyRuleEngine.CodeMissingBoots);
        Assert.DoesNotContain(warnings, w => w.Code == SafetyRuleEngine.CodeNoPerson);
    }

    [Fact]
    public void Hjalm_under_konfidenstroskeln_raknas_som_saknad()
    {
        var engine = CreateEngine(new SafetyRuleOptions { MinimumConfidence = 0.55 });

        // 0.31 är för osäkert för att vi ska våga säga att hjälmen finns.
        var warnings = engine.Evaluate(Analysis(
            tags: new[] { ("helmet", 0.31), ("safety vest", 0.88), ("boots", 0.80) },
            objects: new[] { ("person", 0.92) }));

        Assert.Contains(warnings, w => w.Code == SafetyRuleEngine.CodeMissingHelmet);
    }

    [Theory]
    [InlineData("hard hat")]
    [InlineData("hardhat")]
    [InlineData("safety helmet")]
    [InlineData("helmet")]
    public void Alla_hjalmsynonymer_kanns_igen(string tagName)
    {
        var engine = CreateEngine();

        var warnings = engine.Evaluate(Analysis(
            tags: new[] { (tagName, 0.85), ("safety vest", 0.85), ("boots", 0.80) },
            objects: new[] { ("person", 0.92) }));

        Assert.DoesNotContain(warnings, w => w.Code == SafetyRuleEngine.CodeMissingHelmet);
    }

    // -----------------------------------------------------------------------
    // Riskzoner
    // -----------------------------------------------------------------------

    [Fact]
    public void Stege_flaggas_som_riskindikator()
    {
        var engine = CreateEngine();

        var warnings = engine.Evaluate(Analysis(
            tags: new[] { ("ladder", 0.87), ("helmet", 0.90), ("safety vest", 0.88), ("boots", 0.80) },
            objects: new[] { ("person", 0.93) }));

        var hazard = Assert.Single(warnings, w => w.Code == SafetyRuleEngine.CodeZoneHazard);
        Assert.Contains("ladder", hazard.Message);
        Assert.Equal(WarningSeverity.Medium, hazard.Severity);
    }

    [Fact]
    public void Samma_riskindikator_flera_ganger_ger_bara_en_varning()
    {
        var engine = CreateEngine();

        // Samma stege syns både som tagg och som objekt — ska bli en varning, inte två.
        var warnings = engine.Evaluate(Analysis(
            tags: new[] { ("ladder", 0.87), ("helmet", 0.90), ("safety vest", 0.88), ("boots", 0.80) },
            objects: new[] { ("person", 0.93), ("ladder", 0.79) }));

        Assert.Single(warnings, w => w.Code == SafetyRuleEngine.CodeZoneHazard);
    }

    // -----------------------------------------------------------------------
    // Ordmatchning — skyddar mot falska träffar
    // -----------------------------------------------------------------------

    [Fact]
    public void Ord_som_bara_innehaller_nyckelordet_ger_ingen_traff()
    {
        var engine = CreateEngine();

        // "vested" innehåller "vest" som delsträng men är inte en väst.
        // Matchar vi på delsträng skulle vi missa att västen faktiskt saknas.
        var warnings = engine.Evaluate(Analysis(
            tags: new[] { ("vested", 0.90), ("helmet", 0.90), ("boots", 0.80) },
            objects: new[] { ("person", 0.93) }));

        Assert.Contains(warnings, w => w.Code == SafetyRuleEngine.CodeMissingVest);
    }

    // -----------------------------------------------------------------------
    // Bildkvalitet
    // -----------------------------------------------------------------------

    [Fact]
    public void Nastan_inga_sakra_taggar_flaggar_bildkvalitet()
    {
        var engine = CreateEngine();

        var warnings = engine.Evaluate(Analysis(
            tags: new[] { ("dark", 0.60), ("blur", 0.30), ("night", 0.20) }));

        Assert.Contains(warnings, w => w.Code == SafetyRuleEngine.CodeLowQuality);
    }

    // -----------------------------------------------------------------------
    // Allvarlighetsgrad
    // -----------------------------------------------------------------------

    [Fact]
    public void HighestSeverity_returnerar_hogsta_graden()
    {
        var inspection = new Inspection
        {
            Warnings =
            {
                new SafetyWarning("A", "info", WarningSeverity.Info, 0.5),
                new SafetyWarning("B", "hog", WarningSeverity.High, 0.7),
                new SafetyWarning("C", "medel", WarningSeverity.Medium, 0.6)
            }
        };

        Assert.Equal(WarningSeverity.High, inspection.HighestSeverity);
    }

    [Fact]
    public void HighestSeverity_ar_null_utan_varningar()
    {
        Assert.Null(new Inspection().HighestSeverity);
    }
}
