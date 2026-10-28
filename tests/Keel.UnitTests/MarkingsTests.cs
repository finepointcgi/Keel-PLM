using Keel.Core;

namespace Keel.UnitTests;

public sealed class MarkingsTests
{
    [Fact]
    public void Itar_requires_usml_category_and_expt()
    {
        var result = Markings.Create(ExportJurisdiction.Itar, DistributionStatement.D, "ENG",
            controllingOffice: "ACME", distributionReason: "Critical technology");

        Assert.False(result.IsSuccess);
        Assert.Contains("ITAR data requires a USML category.", result.Errors);
        Assert.Contains("Export-controlled data must carry CUI category EXPT.", result.Errors);
    }

    [Fact]
    public void Valid_itar_markings_normalize_codes_and_render_banner()
    {
        var m = Markings.Create(ExportJurisdiction.Itar, DistributionStatement.D, " ENG ",
            usmlCategory: "XI(d)", cuiCategories: ["expt", "cti", "EXPT"],
            controllingOffice: "ACME Corp", distributionReason: "Critical technology", program: "F35-SUB").Value;

        Assert.Equal(["CTI", "EXPT"], m.CuiCategories);
        Assert.Equal("ENG", m.OwningOrg);
        Assert.True(m.IsExportControlled);
        Assert.Equal("CUI//CTI/EXPT · ITAR USML XI(d) · DISTRIBUTION D · PROGRAM F35-SUB", m.BannerText());
    }

    [Theory]
    [InlineData("EAR99", true, false)]
    [InlineData("9e610.a", true, true)]
    [InlineData("3A001.b.1", true, true)]
    [InlineData("XYZ", false, true)]
    public void Ear_eccn_validation(string eccn, bool valid, bool controlled)
    {
        var result = Markings.Create(ExportJurisdiction.Ear, DistributionStatement.C, "ENG", eccn: eccn,
            cuiCategories: controlled ? ["EXPT"] : null, controllingOffice: "ACME", distributionReason: "Export controlled");

        Assert.Equal(valid, result.IsSuccess);
        if (valid) Assert.Equal(controlled, result.Value.IsExportControlled);
    }

    [Fact]
    public void Distribution_a_is_not_allowed_on_export_controlled_data()
    {
        var result = Markings.Create(ExportJurisdiction.Itar, DistributionStatement.A, "ENG", usmlCategory: "IV(h)", cuiCategories: ["EXPT"]);
        Assert.Contains("Export-controlled data cannot carry distribution statement A.", result.Errors);
    }

    [Fact]
    public void Statement_f_requires_program_and_b_to_f_require_controlling_office()
    {
        var result = Markings.Create(ExportJurisdiction.None, DistributionStatement.F, "ENG");
        Assert.Contains("Distribution statement F requires a program.", result.Errors);
        Assert.Contains("Distribution statements B-F require a controlling office and a reason.", result.Errors);
    }

    [Fact]
    public void Unrestricted_data_cannot_carry_export_fields()
    {
        var result = Markings.Create(ExportJurisdiction.None, DistributionStatement.A, "ENG", eccn: "EAR99", exportAuthorizations: ["TAA-1"]);
        Assert.Equal(2, result.Errors.Count);
    }

    [Fact]
    public void Invalid_codes_are_rejected()
    {
        var result = Markings.Create(ExportJurisdiction.None, DistributionStatement.A, "ENG", cuiCategories: ["CTI; DROP TABLE"], program: "bad program");
        Assert.Equal(2, result.Errors.Count);
    }
}
