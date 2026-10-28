using Keel.Documents;
using Keel.Documents.Vault;

namespace Keel.UnitTests;

public sealed class FileNameTests
{
    [Theory]
    [InlineData("drawing.pdf")]
    [InlineData("Bracket, mounting (rev A).step")]
    [InlineData("ドローイング.pdf")]
    [InlineData("naïve résumé.docx")]
    [InlineData("archive.tar.gz")]
    [InlineData(".hidden")]
    public void Accepts_ordinary_names(string name) => Assert.Null(FileNames.Validate(name));

    [Theory]
    [InlineData("", "required")]
    [InlineData("../etc/passwd", "path separators")]
    [InlineData("dir/file.txt", "path separators")]
    [InlineData(@"C:\temp\file.txt", "path separators")]
    [InlineData("line\nbreak.txt", "control")]
    [InlineData("tab\tname.txt", "control")]
    [InlineData("nul\0.txt", "control")]
    [InlineData("evil\u202Etxt.exe", "formatting")]
    [InlineData("zero\u200Bwidth.txt", "formatting")]
    [InlineData(" leading.txt", "whitespace")]
    [InlineData("trailing.txt ", "whitespace")]
    [InlineData(".", "not file names")]
    [InlineData("..", "not file names")]
    [InlineData("ends-with-dot.", "dot")]
    public void Rejects_unsafe_names(string name, string reason) => Assert.Contains(reason, FileNames.Validate(name));

    [Fact]
    public void Rejects_broken_utf16() =>
        // Built in code: test-data serialization would replace the lone surrogate before the test sees it.
        Assert.Contains("valid Unicode", FileNames.Validate("lone" + (char)0xD800 + "surrogate.txt"));

    [Fact]
    public void Enforces_maximum_length()
    {
        Assert.Null(FileNames.Validate(new string('a', 251) + ".txt"));
        Assert.Contains("255", FileNames.Validate(new string('a', 252) + ".txt"));
    }

    [Theory]
    [InlineData(null, "application/octet-stream")]
    [InlineData("", "application/octet-stream")]
    [InlineData("Application/PDF", "application/pdf")]
    [InlineData("text/plain; charset=utf-8", "text/plain")]
    [InlineData("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    [InlineData("pdf", null)]
    [InlineData("text/plain\r\nX-Injected: 1", null)]
    [InlineData("a/b/c", null)]
    public void Normalizes_media_types(string? input, string? expected) => Assert.Equal(expected, FileNames.NormalizeMediaType(input));
}

public sealed class VaultOptionsTests
{
    [Fact]
    public void Defaults_need_only_endpoint_and_credentials()
    {
        var o = new VaultOptions { ServiceUrl = "https://vault.site.example:8333", AccessKey = "k", SecretKey = "s" };
        Assert.Empty(o.Validate());
        Assert.Equal(2L * 1024 * 1024 * 1024, o.MaxUploadBytes);
    }

    [Fact]
    public void Rejects_unusable_configuration()
    {
        var o = new VaultOptions { ServiceUrl = "ftp://x", MaxUploadBytes = 6L * 1024 * 1024 * 1024, PartSizeBytes = 1024, Bucket = "" };
        Assert.Equal(5, o.Validate().Count);
        Assert.Throws<InvalidOperationException>(() => new ObjectVault(o));
    }
}
