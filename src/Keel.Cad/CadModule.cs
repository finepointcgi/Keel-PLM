using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Keel.Cad;

public static class CadModule
{
    /// <summary>
    /// Registers the CAD services (ADR-0035). The host must also register <see cref="Keel.Core.ICadDocumentPort"/> and <see cref="Keel.Core.ICadPartPort"/>,
    /// implemented by the owners of documents, parts and BOMs.
    /// </summary>
    public static IServiceCollection AddKeelCad(this IServiceCollection services)
    {
        services.AddSingleton<CadGraphService>();
        services.AddSingleton<CadPackageService>();
        services.AddSingleton<CadBomProposalService>();
        services.AddSingleton<AssemblyViewService>(); // 9.5: placed structure of STEP files for the Viewer tab (needs Keel.Core.Viewing.IViewableFileSource)
        services.AddSingleton<Keel.Core.Geometry.IStepProductCounter, Structure.StepProductCounter>(); // 9.1: product count in the model version history
        return services;
    }

    /// <summary>The published JSON schema of the neutral structure, the same file as docs/integration/cad/structure-v1.schema.json.</summary>
    public static string StructureSchema()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Keel.Cad.structure-v1.schema.json")!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
