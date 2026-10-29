using Keel.Core.Geometry;
using Keel.Documents;

namespace Keel.Seed;

/// <summary>The two demo models with two versions each (increment 9.1), so the version history and the comparison are visible straight away.</summary>
public sealed record DemoModels(string BracketModel, string PlateModel);

public sealed partial class DemoSeeder
{
    /// <summary>
    /// Two small synthetic STL models generated in code (no binary fixtures): the gimbal bracket, whose second version is 2.0 mm taller with a thicker wall (the change
    /// the vibration report asked for), and an open-data sensor mounting plate whose second version is thicker. Version 2 of each goes in through the same one-step
    /// "upload new version" the UI uses, so the iterations, audit events and measurements are the real thing.
    /// </summary>
    private async Task<DemoModels> CreateModelVersionsAsync()
    {
        var bracket = await CreateModelAsync("ORCA gimbal bracket model", DemoCatalog.Itar(), "Synthetic STL model of the gimbal bracket for the version history demo.", "alice",
            "gimbal-bracket-model.stl", SyntheticMesh.ToBinaryStl(SyntheticMesh.Bracket(40, 30, 4, 20), "Keel demo: gimbal bracket"),
            SyntheticMesh.ToBinaryStl(SyntheticMesh.Bracket(40, 32, 5, 20), "Keel demo: gimbal bracket"),
            "Upright raised by 2.0 mm and wall thickened to 5 mm after the vibration test.");
        var plate = await CreateModelAsync("Sensor mounting plate model", DemoCatalog.Open, "Synthetic STL model of the sensor mounting plate (open data) for the version history demo.", "alice",
            "sensor-mounting-plate.stl", SyntheticMesh.ToBinaryStl(SyntheticMesh.Box(60, 40, 6), "Keel demo: mounting plate"),
            SyntheticMesh.ToBinaryStl(SyntheticMesh.Box(60, 40, 8), "Keel demo: mounting plate"),
            "Plate thickened from 6 mm to 8 mm for stiffness.");
        log.WriteLine($"Model versions: {bracket} and {plate} created with two versions each.");
        return new DemoModels(bracket, plate);
    }

    private async Task<string> CreateModelAsync(string name, Keel.Core.Markings markings, string description, string author, string fileName, byte[] first, byte[] second, string note)
    {
        await using var one = new MemoryStream(first);
        var created = (await host.Documents.CreateAsync(User(author), new CreateDocumentCommand(name, "CAD", markings, description),
            new FileUpload(fileName, "model/stl", one), Corr())).Must($"create model {name}");
        await using var two = new MemoryStream(second);
        (await host.Documents.UploadNewVersionAsync(User(author), created.Number, new FileUpload(fileName, "model/stl", two), note, reviseIfReleased: false, expectedIteration: 1, Corr()))
            .Must($"upload version 2 of {name}");
        return created.Number;
    }
}
