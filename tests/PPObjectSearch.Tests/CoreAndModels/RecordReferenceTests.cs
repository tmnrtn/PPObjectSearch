using PPObjectSearch.Models;

namespace PPObjectSearch.Tests.CoreAndModels;

/// <summary>A row as a person has it to hand: a link to it, or its table and id.</summary>
public sealed class RecordReferenceTests
{
    private const string Id = "0f6a1c2d-3e4f-4a5b-8c7d-9e0f1a2b3c4d";

    private static RecordReference Parsed(string text)
    {
        Assert.True(RecordReference.TryParse(text, out var reference, out var problem), problem);
        return reference!;
    }

    [Theory]
    [InlineData($"https://contoso.crm11.dynamics.com/main.aspx?appid=1b2c&pagetype=entityrecord&etn=account&id={Id}")]
    [InlineData($"https://contoso.crm11.dynamics.com/main.aspx?pagetype=entityrecord&etn=Account&id=%7b{Id}%7d")]
    [InlineData($"https://contoso.crm11.dynamics.com/main.aspx?forceUCI=1#etn=account&id={Id}")]
    [InlineData($"  \"https://contoso.crm11.dynamics.com/main.aspx?etn=account&id={Id}\"  ")]
    public void An_app_link_names_its_table_and_row_and_environment(string link)
    {
        var reference = Parsed(link);

        Assert.Equal("account", reference.Table);
        Assert.Equal(Guid.Parse(Id), reference.Id);
        Assert.Equal("contoso.crm11.dynamics.com", reference.Host);
        Assert.Null(reference.TypeCode);
    }

    [Fact]
    public void An_old_link_names_its_table_by_type_code()
    {
        var reference = Parsed($"https://contoso.crm11.dynamics.com/main.aspx?etc=1&id=%7B{Id}%7D&pagetype=entityrecord");

        Assert.Equal("1", reference.Table);
        Assert.Equal(1, reference.TypeCode);
    }

    [Theory]
    [InlineData($"https://contoso.crm11.dynamics.com/api/data/v9.2/accounts({Id})")]
    [InlineData($"https://contoso.crm11.dynamics.com/api/data/v9.0/accounts({Id})?$select=name")]
    public void A_web_api_link_names_its_tables_entity_set(string link)
    {
        var reference = Parsed(link);

        Assert.Equal("accounts", reference.Table);
        Assert.Equal(Guid.Parse(Id), reference.Id);
        Assert.Equal("contoso.crm11.dynamics.com", reference.Host);
    }

    [Theory]
    [InlineData($"account {Id}")]
    [InlineData($"Account, {{{Id}}}")]
    [InlineData($"account:{Id}")]
    [InlineData($"account/{Id}")]
    [InlineData($"account({Id})")]
    [InlineData($"{Id}  account")]
    [InlineData($"account\t{Id}\r\n")]
    public void A_table_and_id_typed_in_have_no_environment(string text)
    {
        var reference = Parsed(text);

        Assert.Equal("account", reference.Table);
        Assert.Equal(Guid.Parse(Id), reference.Id);
        Assert.Null(reference.Host);
    }

    [Theory]
    [InlineData("", "Paste a link")]
    [InlineData("account", "no row id")]
    [InlineData(Id, "Which table")]
    [InlineData($"account {Id} {Id}", "more than one row id")]
    [InlineData($"my account {Id}", "nothing else")]
    [InlineData($"acc-ount {Id}", "nothing else")]
    [InlineData("https://contoso.crm11.dynamics.com/main.aspx?pagetype=entitylist&etn=account", "not to a row")]
    [InlineData("https://make.powerapps.com/environments/x/solutions", "not to a row")]
    public void Anything_else_says_what_is_missing(string text, string problem)
    {
        Assert.False(RecordReference.TryParse(text, out var reference, out var said));
        Assert.Null(reference);
        Assert.Contains(problem, said);
    }
}
