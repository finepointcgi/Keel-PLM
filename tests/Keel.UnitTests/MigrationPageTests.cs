using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Keel.Migration;
using Keel.Web.Components.Pages.Migration;
using Npgsql;

namespace Keel.UnitTests;

/// <summary>The admin batch viewer renders through PageFrame and degrades to a message when the migration tables cannot be read.</summary>
public sealed class MigrationPageTests : BunitContext
{
    [Fact]
    public async Task Batch_list_shows_a_message_not_an_exception_when_the_tables_cannot_be_read()
    {
        // nothing listens on port 1: the read fails fast
        Services.AddSingleton(new BatchQueries(NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Username=x;Password=x;Database=x;Timeout=1;Pooling=false")));
        var cut = Render<MigrationBatchesPage>();
        await cut.WaitForAssertionAsync(() => Assert.Contains("could not be read", cut.Markup, StringComparison.Ordinal));
        Assert.Equal("Migration batches", cut.Find("h1").TextContent);
        Assert.Contains("CUI-handling artifacts", cut.Markup, StringComparison.Ordinal);
    }
}
