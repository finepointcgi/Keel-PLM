using Keel.Web.Services;

namespace Keel.UnitTests;

/// <summary>The first guesses for files chosen on the New part page: a name from the file name and a type from the extension. They are starting values only.</summary>
public sealed class FileGuessTests
{
    private static readonly string[] Types = ["Specification", "Drawing", "CAD", "Report", "Procedure", "Other"];

    [Theory]
    [InlineData("bracket_v2.stl", "bracket v2")]
    [InlineData("Gimbal Assembly.STEP", "Gimbal Assembly")]
    [InlineData("noextension", "noextension")]
    [InlineData("a.b.pdf", "a.b")]
    [InlineData(".hidden", ".hidden")]
    public void The_name_is_the_file_name_without_the_extension_and_underscores(string file, string expected) => Assert.Equal(expected, FileGuess.Name(file));

    [Theory]
    [InlineData("bracket.stl", "CAD")]
    [InlineData("pod.STEP", "CAD")]
    [InlineData("mount.3mf", "CAD")]
    [InlineData("Interface spec rev B.pdf", "Specification")]
    [InlineData("assembly procedure.docx", "Procedure")]
    [InlineData("test report.pdf", "Report")]
    [InlineData("bracket drawing.pdf", "Drawing")]
    [InlineData("plan.dwg", "Drawing")]
    [InlineData("notes.txt", "Drawing")] // the fallback
    public void The_type_follows_the_extension_and_then_a_word_in_the_name(string file, string expected) => Assert.Equal(expected, FileGuess.Type(file, Types, "Drawing"));

    [Fact]
    public void A_type_the_administrator_does_not_offer_is_never_guessed()
    {
        Assert.Equal("Drawing", FileGuess.Type("bracket.stl", ["Drawing", "Other"], "Drawing"));
        Assert.Equal("Other", FileGuess.Type("bracket.stl", ["Other"], "Drawing"));
        Assert.Equal("Drawing", FileGuess.Type("x.stl", [], "Drawing"));
    }

    [Theory]
    [InlineData("a.stl", true)]
    [InlineData("a.STP", true)]
    [InlineData("a.pdf", false)]
    public void Models_are_recognised_by_extension(string file, bool model) => Assert.Equal(model, FileGuess.IsModel(file));
}
