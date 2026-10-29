using System.Data.Common;
using Keel.Audit;
using Keel.Bom;
using Keel.Core;
using Keel.Core.Variants;
using Keel.Variants;

namespace Keel.IntegrationTests;

/// <summary>Records the domain events a service writes (the outbox is not needed to check what a module reports).</summary>
internal sealed class VarEventSink : IDomainEventSink
{
    public List<DomainEvent> Events { get; } = [];

    public Task AppendAsync(DbConnection conn, DbTransaction tx, DomainEvent domainEvent, CancellationToken ct)
    {
        Events.Add(domainEvent);
        return Task.CompletedTask;
    }
}

/// <summary>Real services for the variants tests: the MBOM environment (parts, BOM, MBOM, workflow, change) plus the variants service. No mocks.</summary>
internal sealed class VarEnv
{
    public required MbomEnv M { get; init; }
    public required VariantService Var { get; init; }
    public required VarEventSink Sink { get; init; }
    public ConfigEnv C => M.C;
    public BomService Bom => C.Bom;

    public static Subject Alice => ConfigEnv.Alice;
    /// <summary>A U.S. person without the ORCA program: cannot read ORCA-marked data.</summary>
    public static Subject Eve => ConfigEnv.Eve;
    public static Subject Ada => ConfigEnv.Ada;

    public static async Task<VarEnv> CreateAsync(PostgresFixture pg)
    {
        var sink = new VarEventSink();
        var c = await ConfigEnv.CreateAsync(pg, false, (_, _, _) => sink);
        var m = await MbomEnv.CreateAsync(pg, c);
        return new VarEnv { M = m, Var = new VariantService(c.Ds, c.Audit, c.Clock, sink), Sink = sink };
    }

    // ---------------------------------------------------------------- model helpers

    public async Task<ModelView> ModelAsync(Subject by, string product)
    {
        var r = await Var.GetModelAsync(by, product, "corr-model");
        Assert.True(r.IsOk, $"model {product}: {r.Kind} {string.Join("; ", r.Errors)}");
        return r.Value!;
    }

    public static T Ok<T>(Outcome<T> o, string what = "")
    {
        Assert.True(o.IsOk, $"{what}: {o.Kind} {string.Join("; ", o.Errors)}");
        return o.Value!;
    }

    /// <summary>Engine (A, B) and Payload (X, Y), both required, for a product.</summary>
    public async Task EngineAndPayloadAsync(string product, Subject? by = null)
    {
        var who = by ?? Alice;
        Ok(await Var.CreateModelAsync(who, product, null), "create model");
        Ok(await Var.AddFamilyAsync(who, product, new NewFamily("Engine", "Engine"), null));
        Ok(await Var.AddValueAsync(who, product, "Engine", new NewValue("A", "Turbo"), null));
        Ok(await Var.AddValueAsync(who, product, "Engine", new NewValue("B", "Electric"), null));
        Ok(await Var.AddFamilyAsync(who, product, new NewFamily("Payload", "Payload"), null));
        Ok(await Var.AddValueAsync(who, product, "Payload", new NewValue("X", "Camera"), null));
        Ok(await Var.AddValueAsync(who, product, "Payload", new NewValue("Y", "Radar"), null));
    }

    public async Task<ModelView> VariantAsync(string product, string name, string engine, string payload, bool release = false, Subject? by = null)
    {
        var who = by ?? Alice;
        var v = Ok(await Var.CreateVariantAsync(who, product, new NewVariant(name, new Dictionary<string, string> { ["Engine"] = engine, ["Payload"] = payload }), null), "create variant " + name);
        if (release) v = Ok(await Var.ReleaseVariantAsync(who, product, name, null), "release variant " + name);
        return v;
    }

    // ---------------------------------------------------------------- conditions

    public async Task<Outcome<WorkingBomLine>> SetConditionAsync(string part, int find, string? condition, string? validateAgainst = null, Subject? by = null)
    {
        var who = by ?? Alice;
        var line = (await Bom.GetWorkingAsync(who, part, null)).Value!.Lines.First(l => l.FindNumber == find);
        ConditionValidator? validator = validateAgainst is null ? null : (e, ct) => Var.ConditionValidatorFor(who, validateAgainst)(e, ct);
        return await Bom.SetLineConditionAsync(who, part, line.Id, condition, validator, "corr-cond");
    }

    public async Task<WorkingBomLine> CondAsync(string part, int find, string? condition, string? validateAgainst = null)
    {
        var r = await SetConditionAsync(part, find, condition, validateAgainst);
        Assert.True(r.IsOk, $"condition {condition}: {r.Kind} {string.Join("; ", r.Errors)}");
        return r.Value!;
    }

    // ---------------------------------------------------------------- the scene

    /// <summary>
    /// Rocket (Assembly), latest iteration, with option conditions: 10 Engine A part (Engine = A), 20 Engine B part (Engine = B), 30 camera (Payload = X),
    /// 40 radar (Payload = Y), 50 bracket x2 (always), 60 a restricted part (Engine = A), 70 Pod (Payload = Y) holding a widget (always) and an extra (Engine = A).
    /// </summary>
    public sealed record Scene(string Rocket, string EngineA, string EngineB, string Camera, string Radar, string Bracket, string Secret, string Pod, string Widget, string Extra);

    public async Task<Scene> RocketAsync()
    {
        var engA = await C.NewPartAsync("Engine A part");
        var engB = await C.NewPartAsync("Engine B part");
        var cam = await C.NewPartAsync("Camera");
        var radar = await C.NewPartAsync("Radar");
        var bracket = await C.NewPartAsync("Bracket");
        var secret = await C.NewPartAsync("Stealth coupling", "Component", ConfigEnv.Itar());
        var widget = await C.NewPartAsync("Pod widget");
        var extra = await C.NewPartAsync("Pod extra");
        var pod = await C.NewPartAsync("Pod", "Assembly");
        var rocket = await C.NewPartAsync("Rocket", "Assembly");

        await EngineAndPayloadAsync(rocket);

        await C.CheckOutAsync(pod);
        await C.AddLineAsync(pod, widget, 3, 10);
        await C.AddLineAsync(pod, extra, 1, 20);
        await CondAsync(pod, 20, "Engine = A", rocket);
        await C.CheckInAsync(pod);

        await C.CheckOutAsync(rocket);
        await C.AddLineAsync(rocket, engA, 1, 10);
        await C.AddLineAsync(rocket, engB, 1, 20);
        await C.AddLineAsync(rocket, cam, 1, 30);
        await C.AddLineAsync(rocket, radar, 1, 40);
        await C.AddLineAsync(rocket, bracket, 2, 50);
        await C.AddLineAsync(rocket, secret, 1, 60);
        await C.AddLineAsync(rocket, pod, 2, 70);
        await CondAsync(rocket, 10, "Engine = A", rocket);
        await CondAsync(rocket, 20, "Engine = B", rocket);
        await CondAsync(rocket, 30, "Payload = X", rocket);
        await CondAsync(rocket, 40, "Payload = Y", rocket);
        await CondAsync(rocket, 60, "Engine = A", rocket);
        await CondAsync(rocket, 70, "Payload = Y", rocket);
        await C.CheckInAsync(rocket);
        return new Scene(rocket, engA, engB, cam, radar, bracket, secret, pod, widget, extra);
    }

    public async Task<BomExplosion> ExplodeAsync(string number, Subject? by = null, VariantSelection? variant = null, EffectivityRequest? unit = null, BomConfigSpec spec = BomConfigSpec.Latest)
    {
        var r = await Bom.ExplodeAsync(by ?? Alice, new BomQuery(number, Spec: unit is null ? spec : BomConfigSpec.Effectivity, Effectivity: unit, Variant: variant), "corr-explode");
        Assert.True(r.IsOk, $"explode {number}: {r.Kind} {string.Join("; ", r.Errors)}");
        return r.Value!;
    }

    public static VariantSelection Sel(string engine, string payload) =>
        new($"{engine}/{payload}", new Dictionary<string, string> { ["Engine"] = engine, ["Payload"] = payload });
}
