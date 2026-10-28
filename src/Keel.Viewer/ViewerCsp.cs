namespace Keel.Viewing;

/// <summary>
/// The Content-Security-Policies of the viewer (ADR-0027). The global policy of the application is NOT changed: it stays
/// <c>default-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'[ IdP]</c> everywhere else. Only the viewer's own routes and static
/// assets get one of the policies below, chosen by <see cref="ForPath"/> in one place.
/// </summary>
public static class ViewerCsp
{
    /// <summary>Routes of the standalone viewer documents and data.</summary>
    public const string RoutePrefix = "/viewer/";

    /// <summary>Raw file bytes (<c>/viewer/content/…</c>).</summary>
    public const string ContentPrefix = "/viewer/content/";

    /// <summary>Static viewer assets of the Keel.Web class library (scripts, worker, WebAssembly, styles).</summary>
    public const string AssetPrefix = "/_content/Keel.Web/viewer/";

    /// <summary>The CAD import worker: the only script that runs with <c>'unsafe-eval'</c> (see <see cref="StepWorker"/>).</summary>
    public const string StepWorkerPath = AssetPrefix + "step-worker.js";

    /// <summary>The pdf.js worker.</summary>
    public const string PdfWorkerPath = AssetPrefix + "vendor/pdf.worker.min.mjs";

    /// <summary>
    /// The policy of the viewer page, its styles and its scripts. Compared with the global policy it adds worker-src 'self' (workers load from our own
    /// origin only, never blob:), img-src and connect-src with data: and blob: (decoded glTF textures and embedded buffers are blob:/data: URLs the page
    /// makes itself; neither can load script and connect-src names no network origin, so nothing can be sent anywhere), object-src and frame-src 'none',
    /// and it tightens form-action to 'none'. script-src is NOT widened: the page itself never gets 'unsafe-eval' or 'wasm-unsafe-eval'.
    /// </summary>
    public const string Page =
        "default-src 'self'; worker-src 'self'; img-src 'self' data: blob:; connect-src 'self' data: blob:; " +
        "object-src 'none'; frame-src 'none'; frame-ancestors 'none'; base-uri 'self'; form-action 'none'";

    /// <summary>
    /// The policy of the OpenCascade import worker. A worker's policy is the one on ITS script's response, so the capabilities WebAssembly needs are
    /// granted to this worker alone and never to the page or its DOM. Compiling the importer needs <c>'wasm-unsafe-eval'</c>, and the importer's Embind
    /// layer builds its call wrappers with the <c>Function</c> constructor, which needs <c>'unsafe-eval'</c> (found when testing; the wrapper source is
    /// generated from the module's own type table, never from file content). The worker has no DOM, no network origin other than 'self' (for the .wasm),
    /// and receives only the file bytes by message.
    /// </summary>
    public const string StepWorker = "default-src 'none'; script-src 'self' 'unsafe-eval' 'wasm-unsafe-eval'; connect-src 'self'; base-uri 'none'; form-action 'none'";

    /// <summary>The pdf.js worker: scripts from 'self' and WebAssembly (its JPEG 2000 decoder), no eval, nothing else.</summary>
    public const string PdfWorker = "default-src 'none'; script-src 'self' 'wasm-unsafe-eval'; connect-src 'self'; base-uri 'none'; form-action 'none'";

    /// <summary>
    /// The policy of the raw file bytes: nothing may load or run from the response and it is sandboxed, so an SVG or any other file opened directly
    /// (not through an img element) cannot execute script even though it is served from our origin.
    /// </summary>
    public const string Content = "default-src 'none'; sandbox; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

    /// <summary>The directive names <see cref="Page"/> adds to, or changes from, the global policy.</summary>
    public static readonly IReadOnlyList<string> PageDifferences = ["worker-src", "img-src", "connect-src", "object-src", "frame-src", "form-action"];

    /// <summary>The policy for a request path, or null when the request is not a viewer request (the global policy applies).</summary>
    public static string? ForPath(string path)
    {
        if (path.Equals(StepWorkerPath, StringComparison.OrdinalIgnoreCase)) return StepWorker;
        if (path.Equals(PdfWorkerPath, StringComparison.OrdinalIgnoreCase)) return PdfWorker;
        if (path.StartsWith(ContentPrefix, StringComparison.OrdinalIgnoreCase)) return Content;
        if (path.StartsWith(RoutePrefix, StringComparison.OrdinalIgnoreCase) || path.StartsWith(AssetPrefix, StringComparison.OrdinalIgnoreCase)) return Page;
        return null;
    }
}
