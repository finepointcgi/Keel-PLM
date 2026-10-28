using System.Text;
using Keel.Core.Viewing;
using Keel.TestSupport;
using Keel.Viewing;

namespace Keel.UnitTests;

/// <summary>Type detection of the viewer (ADR-0027, VWR-3): magic bytes and content checks cross-checked with the extension; the stored media type is never consulted.</summary>
public sealed class ViewerSnifferTests
{
    private static Detection Detect(string name, byte[] bytes) => FileSniffer.Detect(name, bytes.AsSpan(0, Math.Min(bytes.Length, FileSniffer.HeadBytes)), bytes.Length);

    public static TheoryData<string, byte[], string> Genuine => new()
    {
        { "cube.stl", ViewerSamples.AsciiStlCube(), "stl" },
        { "cube-binary.stl", ViewerSamples.BinaryStlCube(), "stl" },
        { "cube.obj", ViewerSamples.ObjCube(), "obj" },
        { "boxes.gltf", ViewerSamples.GltfTwoBoxes(), "gltf" },
        { "boxes.glb", ViewerSamples.GlbTwoBoxes(), "glb" },
        { "box.step", ViewerSamples.StepBox(), "step" },
        { "BOX.STP", ViewerSamples.StepBox(), "step" },
        { "drawing.pdf", ViewerSamples.Pdf(2), "pdf" },
        { "picture.png", ViewerSamples.Png(), "png" },
        { "shape.svg", ViewerSamples.SvgWithScript(), "svg" },
    };

    [Theory]
    [MemberData(nameof(Genuine))]
    public void Genuine_files_are_recognised_without_warning(string name, byte[] bytes, string format)
    {
        var d = Detect(name, bytes);
        Assert.Equal(format, d.Format.Id);
        Assert.Null(d.Warning);
        Assert.True(d.Recognised);
    }

    [Fact]
    public void A_binary_stl_that_starts_with_the_word_solid_is_still_a_binary_stl()
    {
        var bytes = ViewerSamples.BinaryStlCube();
        Assert.StartsWith("solid", Encoding.ASCII.GetString(bytes, 0, 5), StringComparison.Ordinal);
        Assert.Equal("binary STL size check", Detect("c.stl", bytes).Basis);
    }

    [Fact]
    public void Iges_is_recognised_by_its_fixed_format_start_section()
    {
        var line = "Synthetic IGES".PadRight(72) + "S      1\n";
        Assert.Equal("iges", Detect("part.igs", Encoding.ASCII.GetBytes(line + new string(' ', 72) + "G      1\n")).Format.Id);
        Assert.False(Detect("part.igs", Encoding.ASCII.GetBytes("not iges at all")).Recognised);
    }

    [Theory]
    [InlineData("photo.stl", "png")]      // a PNG renamed to .stl is shown as the PNG it is, with a warning
    [InlineData("drawing.png", "pdf")]    // a PDF renamed to .png
    [InlineData("model.pdf", "step")]     // a STEP file renamed to .pdf
    [InlineData("noextension", "pdf")]
    public void Strong_signatures_win_over_the_extension_and_the_mismatch_is_reported(string name, string actual)
    {
        var bytes = actual switch { "png" => ViewerSamples.Png(), "pdf" => ViewerSamples.Pdf(1), _ => ViewerSamples.StepBox() };
        var d = Detect(name, bytes);
        Assert.Equal(actual, d.Format.Id);
        if (name != "noextension") Assert.Contains("is named like", d.Warning);
        else Assert.Null(d.Warning);
    }

    [Theory]
    [InlineData("notes.stl")]
    [InlineData("notes.obj")]
    [InlineData("notes.gltf")]
    [InlineData("notes.svg")]
    [InlineData("notes.igs")]
    [InlineData("notes.glb")]
    [InlineData("notes.png")]
    [InlineData("notes.pdf")]
    [InlineData("notes.step")]
    public void Plain_text_with_a_model_or_image_extension_is_not_previewed(string name)
    {
        var d = Detect(name, Encoding.UTF8.GetBytes("This is an ordinary text file about the bracket.\nIt is not a model.\n"));
        Assert.False(d.Recognised);
        Assert.Contains("its content is not a valid", d.Warning);
    }

    [Fact]
    public void Random_bytes_named_like_a_binary_stl_or_glb_are_rejected()
    {
        var random = new byte[5000];
        new Random(7).NextBytes(random);
        Assert.False(Detect("scan.stl", random).Recognised);
        Assert.False(Detect("scan.glb", random).Recognised);
        Assert.False(Detect("scan.obj", random).Recognised);
    }

    [Fact]
    public void Weak_formats_need_their_extension_so_text_files_cannot_masquerade_as_models()
    {
        // A perfectly good OBJ and a perfectly good SVG without an extension that says so are not previewed.
        Assert.False(Detect("data.txt", ViewerSamples.ObjCube()).Recognised);
        Assert.False(Detect("data.bin", ViewerSamples.SvgWithScript()).Recognised);
        Assert.False(Detect("data", ViewerSamples.AsciiStlCube()).Recognised);
    }

    [Fact]
    public void A_gltf_must_carry_asset_version_when_the_whole_file_is_checked()
    {
        Assert.False(Detect("x.gltf", Encoding.UTF8.GetBytes("{\"scene\":0}")).Recognised);
        Assert.True(Detect("x.gltf", Encoding.UTF8.GetBytes("{\"asset\":{\"version\":\"2.0\"}}")).Recognised);
        Assert.False(Detect("x.gltf", Encoding.UTF8.GetBytes("{\"asset\":{\"version\":\"2.0\"}")).Recognised); // truncated JSON
    }

    [Fact]
    public void A_pdf_must_start_with_the_header_to_be_a_pdf()
    {
        var shifted = Encoding.ASCII.GetBytes("junk\n").Concat(ViewerSamples.Pdf(1)).ToArray();
        Assert.False(Detect("shifted.pdf", shifted).Recognised);
    }

    [Fact]
    public void Extension_table_maps_the_documented_formats()
    {
        Assert.Equal("step", FileFormats.FromFileName("a.STEP").Id);
        Assert.Equal("step", FileFormats.FromFileName("a.stp").Id);
        Assert.Equal("iges", FileFormats.FromFileName("a.igs").Id);
        Assert.Equal("jpeg", FileFormats.FromFileName("a.jpg").Id);
        Assert.Equal(ViewerTypes.None, FileFormats.FromFileName("a.docx").Viewer);
        Assert.Equal(ViewerTypes.None, FileFormats.FromFileName("noext").Viewer);
        Assert.All(FileFormats.All, f => Assert.NotEqual(ViewerTypes.None, f.Viewer));
    }
}
