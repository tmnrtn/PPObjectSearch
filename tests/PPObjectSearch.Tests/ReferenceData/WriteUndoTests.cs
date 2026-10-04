using System.IO;
using System.Text.Json.Nodes;
using PPObjectSearch.Models;
using PPObjectSearch.Services;
using static PPObjectSearch.Tests.ReferenceData.RefData;

namespace PPObjectSearch.Tests.ReferenceData;

public class WriteUndoTests
{
    private static string? Sets(string table) => table switch
    {
        "account" => "accounts",
        "contact" => "contacts",
        "new_parent" => "new_parents",
        _ => null
    };

    private static JsonObject Row(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void An_update_is_reversed_column_by_column_and_lookups_bind_back_to_what_they_pointed_at()
    {
        var before = Row($$"""
            {
              "new_name": "old",
              "new_amount": 12.5,
              "_new_parentid_value": "{{G(7)}}",
              "_new_parentid_value@Microsoft.Dynamics.CRM.lookuplogicalname": "new_parent",
              "_new_parentid_value@Microsoft.Dynamics.CRM.associatednavigationproperty": "new_ParentId"
            }
            """);

        var written = new Dictionary<string, object?>
        {
            ["new_name"] = "new",
            ["new_amount"] = 20m,
            ["new_ParentId@odata.bind"] = $"/new_parents({G(8)})",
            ["new_OtherId@odata.bind"] = $"/new_parents({G(9)})"
        };

        var undo = WriteUndo.ForUpdate("new_things", G(1), written, before, Sets);

        Assert.Equal(UndoMethod.Update, undo.Method);
        Assert.Equal("old", undo.Body!["new_name"]!.GetValue<string>());
        Assert.Equal(12.5m, undo.Body["new_amount"]!.GetValue<decimal>());
        Assert.Equal($"/new_parents({G(7)})", undo.Body["new_ParentId@odata.bind"]!.GetValue<string>());

        // Dataverse only annotates a lookup that has a value: one it does not mention was empty.
        Assert.True(undo.Body.ContainsKey("new_OtherId@odata.bind"));
        Assert.Null(undo.Body["new_OtherId@odata.bind"]);
    }

    [Fact]
    public void A_deleted_row_comes_back_with_its_id_its_lookups_and_its_state_set_afterwards()
    {
        var before = Row($$"""
            {
              "new_thingid": "{{G(3)}}",
              "new_name": "gone",
              "createdon": "2026-01-01T00:00:00Z",
              "statecode": 1,
              "statuscode": 2,
              "new_note": null,
              "_parentcustomerid_value": "{{G(5)}}",
              "_parentcustomerid_value@Microsoft.Dynamics.CRM.lookuplogicalname": "account",
              "_parentcustomerid_value@Microsoft.Dynamics.CRM.associatednavigationproperty": "parentcustomerid_account"
            }
            """);

        var columns = new[]
        {
            Col("new_thingid", "UniqueidentifierType", primaryId: true),
            Col("new_name"),
            Col("new_note"),
            Col("createdon", "DateTimeType", validForCreate: false),
            Col("statecode", "StateType"),
            Col("statuscode", "StatusType"),
            Col("parentcustomerid", "CustomerType")
        };

        var undo = WriteUndo.ForDelete("new_things", G(3), "new_thingid", before, columns, Sets);

        Assert.Equal(UndoMethod.Create, undo.Method);
        var body = undo.Body!;
        Assert.Equal(G(3).ToString(), body["new_thingid"]!.GetValue<string>());
        Assert.Equal("gone", body["new_name"]!.GetValue<string>());
        Assert.Equal($"/accounts({G(5)})", body["parentcustomerid_account@odata.bind"]!.GetValue<string>());
        Assert.False(body.ContainsKey("createdon"));
        Assert.False(body.ContainsKey("new_note"));

        // An inactive row is created active and then set back, since its status belongs to the inactive state.
        Assert.False(body.ContainsKey("statecode"));
        Assert.False(body.ContainsKey("statuscode"));
        Assert.Equal(1, undo.Then!["statecode"]!.GetValue<long>());
        Assert.Equal(2, undo.Then["statuscode"]!.GetValue<long>());
    }

    [Fact]
    public void An_active_row_needs_no_second_step()
    {
        var before = Row("""{"statecode":0,"statuscode":1}""");
        var columns = new[] { Col("statecode", "StateType"), Col("statuscode", "StatusType") };

        var undo = WriteUndo.ForDelete("new_things", G(3), "new_thingid", before, columns, Sets);

        Assert.Null(undo.Then);
        Assert.Equal(1, undo.Body!["statuscode"]!.GetValue<long>());
    }

    [Fact]
    public void A_lookup_to_a_table_with_no_known_entity_set_is_refused_rather_than_dropped()
    {
        var before = Row($$"""
            {
              "_new_xid_value": "{{G(4)}}",
              "_new_xid_value@Microsoft.Dynamics.CRM.lookuplogicalname": "new_unknown",
              "_new_xid_value@Microsoft.Dynamics.CRM.associatednavigationproperty": "new_XId"
            }
            """);

        Assert.Throws<PPObjectSearch.Dataverse.DataverseException>(() =>
            WriteUndo.ForDelete("new_things", G(3), "new_thingid", before, new[] { Lookup("new_xid") }, Sets));
    }

    [Fact]
    public void A_run_log_reads_back_what_was_appended()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ppobjectsearch-tests", Guid.NewGuid().ToString("N"));
        var log = WriteLog.Start("reconcile", folder, new DateTimeOffset(2026, 10, 4, 9, 30, 0, TimeSpan.Zero));

        Assert.StartsWith("20261004-093000-reconcile-", log.Run);
        Assert.False(File.Exists(log.Path));

        log.Append(new WriteLogEntry
        {
            Run = log.Run,
            Tool = "reconcile",
            Environment = "https://org.crm.dynamics.com",
            Account = "someone@contoso.com",
            Table = "new_thing",
            Id = G(2),
            Action = "Update",
            Columns = new[] { "new_name" },
            Before = Row("""{"new_name":"old"}"""),
            After = Row("""{"new_name":"new"}"""),
            Succeeded = true,
            Message = "Updated 1 column(s).",
            Undo = new UndoStep(UndoMethod.Update, "new_things", G(2), Row("""{"new_name":"old"}"""))
        });
        log.Append(new WriteLogEntry { Run = log.Run, Table = "new_thing", Action = "Delete" });

        var entries = WriteLog.Read(log.Path);

        Assert.Equal(2, entries.Count);
        Assert.Equal("someone@contoso.com", entries[0].Account);
        Assert.Equal("old", entries[0].Before!["new_name"]!.GetValue<string>());
        Assert.Equal(UndoMethod.Update, entries[0].Undo!.Method);
        Assert.Equal("old", entries[0].Undo!.Body!["new_name"]!.GetValue<string>());
        Assert.Contains("\"method\":\"Update\"", File.ReadAllLines(log.Path)[0]);
        Assert.Null(log.Problem);
    }
}
