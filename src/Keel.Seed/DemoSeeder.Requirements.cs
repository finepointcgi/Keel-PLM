using Keel.Core;
using Keel.Requirements;

namespace Keel.Seed;

public sealed partial class DemoSeeder
{
    /// <summary>
    /// The demo requirement specification (8.3): a small hierarchy for the ORCA pod, allocated to parts that exist in the demo, one requirement verified with the system specification as
    /// evidence, one link left suspect by a later reword. It stays In Work so the pages can be edited. Everything goes through the real service.
    /// </summary>
    private async Task<string> SeedRequirementsAsync()
    {
        var alice = User("alice");
        var created = (await host.Requirements.CreateAsync(alice, new CreateSpecCommand("ORCA pod system requirements", "ORCA sensor pod", "Synthetic requirements of the ORCA sensor pod, for the demo.", DemoCatalog.Itar()), Corr())).Must("create the requirement specification");
        var number = created.Summary.Number;
        RequirementInput In(string title, string text, string type, string priority, string method, string? parent = null) => new(title, text, type, priority, method, "Synthetic rationale.", ReqStatuses.Approved, parent);
        var edit = (await host.Requirements.EditAsync(alice, number, new EditCommand(1, "Initial requirements",
        [
            new EditOp("add", null, In("Depth rating", "The pod shall operate at depths to 6000 m.", "performance", "critical", "test")),
            new EditOp("add", null, In("Housing pressure rating", "The housing shall withstand 600 bar external pressure without leakage.", "functional", "high", "analysis", "#0")),
            new EditOp("add", null, In("Gimbal range", "The gimbal shall point the sensor through plus and minus 45 degrees.", "functional", "medium", "demonstration")),
            new EditOp("add", null, In("Operating temperature", "The pod shall operate from 0 to 35 degrees Celsius.", "environmental", "medium", "test")),
            new EditOp("add", null, In("Connector interface", "The pod shall interface through one wet-mateable connector.", "interface", "low", "inspection")),
        ]), Corr())).Must("add the requirements");
        var keys = edit.CreatedKeys;
        (await host.Requirements.AddLinkAsync(alice, number, keys[1], new AddLinkCommand("allocated-to", null, null, _part["housing"]), Corr())).Must("allocate the housing rating");
        (await host.Requirements.AddLinkAsync(alice, number, keys[2], new AddLinkCommand("allocated-to", null, null, _part["gimbal"]), Corr())).Must("allocate the gimbal range");
        (await host.Requirements.AddLinkAsync(alice, number, keys[1], new AddLinkCommand("derives-from", null, keys[0], null), Corr())).Must("derive the housing rating");
        (await host.Requirements.AddLinkAsync(alice, number, keys[0], new AddLinkCommand("verified-by", null, null, _doc["spec-system"]), Corr())).Must("plan the verification");
        (await host.Requirements.RecordVerificationAsync(alice, number, keys[2], new RecordVerificationCommand("pass", null, "Demonstrated at the bench.", null, [_doc["spec-system"]], false, null), Corr())).Must("verify the gimbal range");
        // A later reword leaves the allocation of the housing rating suspect until it is reviewed.
        var current = (await host.Requirements.GetRequirementAsync(alice, number, keys[1], Corr())).Must("read the housing rating");
        (await host.Requirements.EditAsync(alice, number, new EditCommand(edit.Iteration, "Reworded after the pressure analysis",
            [new EditOp("update", keys[1], In(current.Title!, "The housing shall withstand 650 bar external pressure without leakage.", "functional", "high", "analysis"))]), Corr())).Must("reword the housing rating");
        log.WriteLine($"Requirements: {number} with {keys.Count} requirements, links to the housing and gimbal, one verification, one suspect link.");
        return number;
    }
}
