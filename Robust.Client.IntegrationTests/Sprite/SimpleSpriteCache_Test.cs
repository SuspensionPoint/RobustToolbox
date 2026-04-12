using System.Threading.Tasks;
using NUnit.Framework;
using Robust.Client;
using Robust.Client.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Graphics.RSI;
using Robust.Shared.Maths;
using static Robust.Client.Graphics.RSI;

namespace Robust.UnitTesting.Client.Sprite;

public sealed class SimpleSpriteCache_Test : RobustIntegrationTest
{
    private static readonly string Prototypes = @"
- type: entity
  id: simpleCacheDir1
  components:
  - type: Sprite
    sprite: debugRotation.rsi
    layers:
    - state: direction1

- type: entity
  id: simpleCacheDir1Multi
  components:
  - type: Sprite
    sprite: debugRotation.rsi
    layers:
    - state: direction1
    - state: direction1

- type: entity
  id: simpleCacheDir4
  components:
  - type: Sprite
    sprite: debugRotation.rsi
    layers:
    - state: direction4
";

    private IntegrationInstance _client = default!;
    private IEntityManager _entMan = default!;
    private SpriteSystem _sys = default!;

    [OneTimeSetUp]
    public async Task Setup()
    {
        _client = StartClient(new ClientIntegrationOptions { ExtraPrototypes = Prototypes });
        await _client.WaitIdleAsync();
        var baseClient = _client.Resolve<IBaseClient>();
        await _client.WaitPost(() => baseClient.StartSinglePlayer());
        await _client.WaitIdleAsync();
        _entMan = _client.EntMan;
        _sys = _client.System<SpriteSystem>();
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        await _client.WaitIdleAsync();
        _client.Dispose();
    }

    // ── Qualification tests ──────────────────────────────────────────────

    [Test]
    public async Task Dir1SingleLayer_QualifiesForFastPath()
    {
        EntityUid uid = default;
        await _client.WaitPost(() => uid = _entMan.Spawn("simpleCacheDir1"));
        await _client.WaitRunTicks(1); // Let IsInert update queue process

        var ent = new Entity<SpriteComponent>(uid, _entMan.GetComponent<SpriteComponent>(uid));

        Assert.That(ent.Comp.IsInert, Is.True, "IsInert should be true for a static Dir1 sprite");
        Assert.That(ent.Comp.PostShader, Is.Null, "PostShader should be null");
        Assert.That(ent.Comp.GranularLayersRendering, Is.False, "GranularLayersRendering should be false");
        Assert.That(ent.Comp.Layers.Count, Is.EqualTo(1), "Should have exactly 1 layer");

        var layer = ent.Comp.Layers[0];
        Assert.That(layer.ActualState, Is.Not.Null, "ActualState should be resolved");
        Assert.That(layer.ActualState!.RsiDirections, Is.EqualTo(RsiDirectionType.Dir1), "Should be Dir1");
        Assert.That(layer.Shader, Is.Null, "Layer Shader should be null");
        Assert.That(layer.CopyToShaderParameters, Is.Null, "CopyToShaderParameters should be null");

        await _client.WaitPost(() => _entMan.DeleteEntity(uid));
    }

    [Test]
    public async Task Dir1MultiLayer_QualifiesForFastPath()
    {
        EntityUid uid = default;
        await _client.WaitPost(() => uid = _entMan.Spawn("simpleCacheDir1Multi"));
        await _client.WaitRunTicks(1); // Let IsInert update queue process

        var ent = new Entity<SpriteComponent>(uid, _entMan.GetComponent<SpriteComponent>(uid));

        Assert.That(ent.Comp.IsInert, Is.True, "IsInert should be true");
        Assert.That(ent.Comp.PostShader, Is.Null, "PostShader should be null");
        Assert.That(ent.Comp.GranularLayersRendering, Is.False, "GranularLayersRendering should be false");
        Assert.That(ent.Comp.Layers.Count, Is.EqualTo(2), "Should have exactly 2 layers");

        for (var i = 0; i < 2; i++)
        {
            var layer = ent.Comp.Layers[i];
            Assert.That(layer.ActualState, Is.Not.Null, $"Layer {i} ActualState should be resolved");
            Assert.That(layer.ActualState!.RsiDirections, Is.EqualTo(RsiDirectionType.Dir1), $"Layer {i} should be Dir1");
            Assert.That(layer.Shader, Is.Null, $"Layer {i} Shader should be null");
            Assert.That(layer.CopyToShaderParameters, Is.Null, $"Layer {i} CopyToShaderParameters should be null");
        }

        await _client.WaitPost(() => _entMan.DeleteEntity(uid));
    }

    [Test]
    public async Task Dir4_DoesNotQualify()
    {
        EntityUid uid = default;
        await _client.WaitPost(() => uid = _entMan.Spawn("simpleCacheDir4"));

        var ent = new Entity<SpriteComponent>(uid, _entMan.GetComponent<SpriteComponent>(uid));

        var layer = ent.Comp.Layers[0];
        Assert.That(layer.ActualState, Is.Not.Null, "ActualState should be resolved");
        Assert.That(layer.ActualState!.RsiDirections, Is.Not.EqualTo(RsiDirectionType.Dir1),
            "Dir4 state should not be Dir1 — disqualifies from fast path");

        await _client.WaitPost(() => _entMan.DeleteEntity(uid));
    }

    // ── Invalidation tests ──────────────────────────────────────────────

    [Test]
    public async Task ChangeLayerState_InvalidatesCache()
    {
        EntityUid uid = default;
        await _client.WaitPost(() => uid = _entMan.Spawn("simpleCacheDir1"));

        var comp = _entMan.GetComponent<SpriteComponent>(uid);
        var ent = new Entity<SpriteComponent>(uid, comp);

        await _client.WaitPost(() =>
        {
            comp._simpleCache.Valid = true;
            _sys.LayerSetRsiState(ent!, 0, new StateId("direction4"));
        });

        Assert.That(comp._simpleCache.Valid, Is.False, "Cache should be invalidated after LayerSetRsiState");

        await _client.WaitPost(() => _entMan.DeleteEntity(uid));
    }

    [Test]
    public async Task ChangeLayerColor_InvalidatesCache()
    {
        EntityUid uid = default;
        await _client.WaitPost(() => uid = _entMan.Spawn("simpleCacheDir1"));

        var comp = _entMan.GetComponent<SpriteComponent>(uid);
        var ent = new Entity<SpriteComponent>(uid, comp);

        await _client.WaitPost(() =>
        {
            comp._simpleCache.Valid = true;
            _sys.LayerSetColor(ent!, 0, Color.Red);
        });

        Assert.That(comp._simpleCache.Valid, Is.False, "Cache should be invalidated after LayerSetColor");

        await _client.WaitPost(() => _entMan.DeleteEntity(uid));
    }

    [Test]
    public async Task ChangeSpriteColor_InvalidatesCache()
    {
        EntityUid uid = default;
        await _client.WaitPost(() => uid = _entMan.Spawn("simpleCacheDir1"));

        var comp = _entMan.GetComponent<SpriteComponent>(uid);
        var ent = new Entity<SpriteComponent>(uid, comp);

        await _client.WaitPost(() =>
        {
            comp._simpleCache.Valid = true;
            _sys.SetColor(ent!, Color.Blue);
        });

        Assert.That(comp._simpleCache.Valid, Is.False, "Cache should be invalidated after SetColor");

        await _client.WaitPost(() => _entMan.DeleteEntity(uid));
    }

    [Test]
    public async Task AddLayer_InvalidatesCache()
    {
        EntityUid uid = default;
        await _client.WaitPost(() => uid = _entMan.Spawn("simpleCacheDir1"));

        var comp = _entMan.GetComponent<SpriteComponent>(uid);
        var ent = new Entity<SpriteComponent>(uid, comp);

        await _client.WaitPost(() =>
        {
            comp._simpleCache.Valid = true;
            _sys.AddBlankLayer(ent);
        });

        Assert.That(comp._simpleCache.Valid, Is.False, "Cache should be invalidated after AddBlankLayer");

        await _client.WaitPost(() => _entMan.DeleteEntity(uid));
    }

    [Test]
    public async Task RemoveLayer_InvalidatesCache()
    {
        EntityUid uid = default;
        await _client.WaitPost(() => uid = _entMan.Spawn("simpleCacheDir1Multi"));

        var comp = _entMan.GetComponent<SpriteComponent>(uid);
        var ent = new Entity<SpriteComponent>(uid, comp);

        await _client.WaitPost(() =>
        {
            comp._simpleCache.Valid = true;
            _sys.RemoveLayer(ent!, 1);
        });

        Assert.That(comp._simpleCache.Valid, Is.False, "Cache should be invalidated after RemoveLayer");

        await _client.WaitPost(() => _entMan.DeleteEntity(uid));
    }

    [Test]
    public async Task ChangeLayerVisibility_InvalidatesCache()
    {
        EntityUid uid = default;
        await _client.WaitPost(() => uid = _entMan.Spawn("simpleCacheDir1"));

        var comp = _entMan.GetComponent<SpriteComponent>(uid);
        var ent = new Entity<SpriteComponent>(uid, comp);

        await _client.WaitPost(() =>
        {
            comp._simpleCache.Valid = true;
            _sys.LayerSetVisible(ent!, 0, false);
        });

        Assert.That(comp._simpleCache.Valid, Is.False, "Cache should be invalidated after LayerSetVisible");

        await _client.WaitPost(() => _entMan.DeleteEntity(uid));
    }

    [Test]
    public async Task SetPostShader_InvalidatesCache()
    {
        EntityUid uid = default;
        await _client.WaitPost(() => uid = _entMan.Spawn("simpleCacheDir1"));

        var comp = _entMan.GetComponent<SpriteComponent>(uid);

        await _client.WaitPost(() =>
        {
            comp._simpleCache.Valid = true;
            comp.PostShader = null; // Even setting null triggers the setter
        });

        Assert.That(comp._simpleCache.Valid, Is.False, "Cache should be invalidated after setting PostShader");

        await _client.WaitPost(() => _entMan.DeleteEntity(uid));
    }

    // ── Integration test ─────────────────────────────────────────────────

    [Test]
    public async Task SimpleToComplex_FallsBackCorrectly()
    {
        EntityUid uid = default;
        await _client.WaitPost(() => uid = _entMan.Spawn("simpleCacheDir1"));
        await _client.WaitRunTicks(1); // Let IsInert update queue process

        var comp = _entMan.GetComponent<SpriteComponent>(uid);
        var ent = new Entity<SpriteComponent>(uid, comp);

        // Verify Dir1 qualifies
        Assert.That(comp.IsInert, Is.True);
        Assert.That(comp.Layers[0].ActualState, Is.Not.Null);
        Assert.That(comp.Layers[0].ActualState!.RsiDirections, Is.EqualTo(RsiDirectionType.Dir1));

        // Change to Dir4 — invalidates cache
        await _client.WaitPost(() =>
        {
            comp._simpleCache.Valid = true;
            _sys.LayerSetRsiState(ent!, 0, new StateId("direction4"));
        });

        Assert.That(comp._simpleCache.Valid, Is.False, "Switching to Dir4 should invalidate cache");

        // Verify Dir4 means it won't re-qualify
        Assert.That(comp.Layers[0].ActualState, Is.Not.Null);
        Assert.That(comp.Layers[0].ActualState!.RsiDirections, Is.Not.EqualTo(RsiDirectionType.Dir1),
            "Dir4 state should prevent re-qualification");

        // Change back to Dir1 — should be able to re-qualify
        await _client.WaitPost(() =>
        {
            _sys.LayerSetRsiState(ent!, 0, new StateId("direction1"));
        });

        Assert.That(comp.Layers[0].ActualState, Is.Not.Null);
        Assert.That(comp.Layers[0].ActualState!.RsiDirections, Is.EqualTo(RsiDirectionType.Dir1),
            "After switching back to Dir1, should be eligible for fast path again");

        await _client.WaitPost(() => _entMan.DeleteEntity(uid));
    }
}
