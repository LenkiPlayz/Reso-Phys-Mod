using System.Numerics;
using Elements.Core;
using FrooxEngine;
using Renderite.Shared;
using Softbody.Core;

namespace Softbody;

/// <summary>
/// In-world vertex-paint brush for <see cref="Softbody"/> components. It adds paint dabs to the Softbody it points at
/// (the component's synced <see cref="Softbody.Paint"/> list, in the renderer slot's local space), so the paint is
/// non-destructive, syncs to everyone, saves with the object, and erasing brings the mesh's own colours back.
///
/// Built like the engine's applier tools (ColorTool, WigglerApplier): a <see cref="Tool"/> with UsesLaser, whose
/// slot gets ObjectRoot + Grabbable from Tool.OnAttach and a grab collider, handle, tip bubble and hit marker from
/// <see cref="OnAttach"/>. Grabbing it physically in VR equips it (InteractionHandler.Grab -> GripEquip); on
/// desktop grab it and use the context menu's Equip, or just hold it (CanUseWhenHolding).
///
/// Aim: equipped, it paints where the interaction laser points (desktop: through the cursor); held without being
/// equipped, along the holding laser (or the tip's forward direction); and in VR the tip itself paints when it is
/// within Radius of the surface. Rays are cast against the rest meshes of the Softbodies running on this client; on a
/// skinned mesh against its CPU-skinned pose (0.8.0, refreshed at most 10 Hz while aimed near), and the hit is mapped
/// back to the bind-space mesh through the hit triangle's barycentric coordinates, where the dab is stored.
///
/// Input runs only on the client of the user using it (InteractionHandler.OnInputUpdate), so the synced dab list
/// is written by that user alone. Visual drives are written locally on every client.
/// </summary>
[Category(new[] { "Tools" })]
public class SoftbodyBrush : Tool
{
    public enum BrushMode { Paint, Erase, Pick, Blur }
    /// <summary>0.13.0: the hue presets: None (white, grey, black: a SkinnedSoftbody's base layer) and the seven ROYGBIV hues
    /// of its hue layers (and of the lattice Softbody's hue bands).</summary>
    public enum PaintHue { None, Red, Orange, Yellow, Green, Blue, Indigo, Violet }

    /// <summary>The paint colour. Its hue picks the hue band, its brightness the softness (Value channel: black = firm).</summary>
    public readonly Sync<colorX> Color;
    /// <summary>0.13.0: the hue the menu's presets paint (follows Color when it is picked some other way). Changing it
    /// keeps the brightness, so the softness.</summary>
    public readonly Sync<PaintHue> Hue;
    /// <summary>Brush radius in metres at the brush's own scale 1 (scaling the brush scales it). 0.002..2.</summary>
    public readonly Sync<float> Radius;
    /// <summary>0..1, how far each dab moves the colour towards Color at its centre.</summary>
    public readonly Sync<float> Strength;
    /// <summary>0..1, the fraction of the radius at full strength before the smooth falloff (1 = hard edge).</summary>
    public readonly Sync<float> Hardness;
    /// <summary>Paint adds dabs, Erase removes the dabs centred within the brush, Pick takes the colour under the brush.</summary>
    public readonly Sync<BrushMode> Mode;
    /// <summary>Distance between dabs along a stroke, as a fraction of the radius (0.05..2).</summary>
    public readonly Sync<float> Spacing;
    /// <summary>What the brush is over or did last (local to each client, never synced or saved).</summary>
    public readonly RawOutput<string> Status;

    // visuals (drives: written locally on every client, never synced)
    protected readonly SyncList<FieldDrive<float>> _radiusDrives;
    protected readonly SyncList<FieldDrive<colorX>> _fillDrives;
    protected readonly SyncList<FieldDrive<colorX>> _rimDrives;
    protected readonly FieldDrive<float3> _markerPosition;
    protected readonly FieldDrive<bool> _markerActive;

    public const float MaxDistance = 5f;
    public const float MinRadius = 0.005f, MaxRadius = 2f;
    /// <summary>Resize speed: full stick (or a held key) changes the radius by e^1.2, about 3.3x, per second.</summary>
    const float ResizeRate = 1.2f;
    float _keyHeld;
    const int MaxDabsPerStep = 16;

    // local state (only meaningful on the client of the user holding the brush)
    readonly List<IPaintable> _targets = new();
    readonly List<Softbody.PaintDab> _lastStroke = new();
    IPaintable _lastStrokeTarget, _hoverTarget, _strokeTarget;
    Vector3 _lastDabLocal;
    bool _hasLastDab, _capWarned;
    double _lastLocalUse = double.MinValue;
    double _statusAt;
    string _status = "idle", _flushedStatus;

    public override bool UsesLaser => true;
    public override bool CanUseWhenHolding => true;
    public override bool UsesSecondary => true;
    public override string PrimaryDescription => Mode.Value switch { BrushMode.Erase => "Erase paint", BrushMode.Pick => "Pick colour", BrushMode.Blur => "Blur (soften transitions)", _ => "Paint softness" };
    public override string SecondaryDescription => "Next mode (paint, blur, erase, pick)";

    // ------------------------------------------------------------------ setup

    protected override void OnAwake()
    {
        base.OnAwake();
        Color.Value = new colorX(0f, 0f, 0f, 1f, ColorProfile.sRGB);
        Hue.Value = PaintHue.None;
        Radius.Value = 0.05f;
        Strength.Value = 0.7f;
        Hardness.Value = 0.5f;
        Mode.Value = BrushMode.Paint;
        Spacing.Value = 0.3f;
    }

    protected override void OnAttach()
    {
        base.OnAttach(); // ObjectRoot + Grabbable (with the Tool's grab check)
        try
        {
            EquipName.Value = "Softbody Brush";
            var visual = Slot.AddSlot("Visual");

            // handle: a cylinder along +Z (forward), with a grab collider that never collides physically
            var handle = visual.AddSlot("Handle");
            handle.LocalPosition = new float3(0f, 0f, 0.02f);
            handle.LocalRotation = floatQ.Euler(90f, 0f, 0f);
            var handleMat = handle.AttachComponent<PBS_Metallic>();
            handleMat.AlbedoColor.Value = new colorX(0.13f, 0.13f, 0.15f, 1f, ColorProfile.sRGB);
            handleMat.Smoothness.Value = 0.6f;
            handleMat.Metallic.Value = 0.3f;
            var cyl = handle.AttachComponent<CylinderMesh>();
            cyl.Radius.Value = 0.011f;
            cyl.Height.Value = 0.13f;
            var hr = handle.AttachComponent<MeshRenderer>();
            hr.Mesh.Target = cyl;
            hr.Materials.Add(handleMat);
            var grab = handle.AttachComponent<BoxCollider>();
            grab.Size.Value = new float3(0.035f, 0.15f, 0.035f);
            grab.Type.Value = ColliderType.NoCollision;

            // ferrule: a cone narrowing to the tip, tinted with the paint colour
            var ferrule = visual.AddSlot("Ferrule");
            ferrule.LocalPosition = new float3(0f, 0f, 0.1f);
            ferrule.LocalRotation = floatQ.Euler(90f, 0f, 0f);
            var ferruleMat = ferrule.AttachComponent<PBS_RimMetallic>();
            var cone = ferrule.AttachComponent<ConeMesh>();
            cone.Height.Value = 0.03f;
            cone.RadiusBase.Value = 0.011f;
            cone.RadiusTop.Value = 0.003f;
            var fr = ferrule.AttachComponent<MeshRenderer>();
            fr.Mesh.Target = cone;
            fr.Materials.Add(ferruleMat);
            _rimDrives.Add().Target = ferruleMat.AlbedoColor;

            // tip: TipReference (Tool.Tip/TipForward) plus a see-through bubble showing the radius and colour
            var tip = Slot.AddSlot("Tip");
            tip.LocalPosition = new float3(0f, 0f, 0.125f);
            TipReference.Target = tip;
            var (tipSphere, tipMat) = Bubble(tip.AddSlot("Radius"));

            // hit marker: the same bubble, placed on the surface where the brush would paint
            var marker = Slot.AddSlot("Marker");
            var (markerSphere, markerMat) = Bubble(marker);
            _markerPosition.Target = marker.Position_Field;
            _markerActive.Target = marker.ActiveSelf_Field;

            _radiusDrives.Add().Target = tipSphere.Radius;
            _radiusDrives.Add().Target = markerSphere.Radius;
            _fillDrives.Add().Target = tipMat.AlbedoColor;
            _fillDrives.Add().Target = markerMat.AlbedoColor;
            _rimDrives.Add().Target = tipMat.RimColor;
            _rimDrives.Add().Target = markerMat.RimColor;
            UpdateVisuals();
            SetMarker(false, default);
        }
        catch (Exception ex) { SoftbodyRuntime.Warn("SoftbodyBrush OnAttach: " + ex); }
    }

    static (SphereMesh, PBS_RimMetallic) Bubble(Slot s)
    {
        var mat = s.AttachComponent<PBS_RimMetallic>();
        mat.Transparent.Value = true;
        mat.RimPower.Value = 1.5f;
        var sphere = s.AttachComponent<SphereMesh>();
        sphere.Radius.Value = 0.05f;
        sphere.Segments.Value = 24;
        sphere.Rings.Value = 12;
        var r = s.AttachComponent<MeshRenderer>();
        r.Mesh.Target = sphere;
        r.Materials.Add(mat);
        r.ShadowCastMode.Value = ShadowCastMode.Off;
        return (sphere, mat);
    }

    protected override void OnChanges()
    {
        base.OnChanges();
        try { SyncHueAndColor(); } catch (Exception ex) { SoftbodyRuntime.Warn("SoftbodyBrush hue: " + ex.Message); }
        try { UpdateVisuals(); } catch (Exception ex) { SoftbodyRuntime.Warn("SoftbodyBrush OnChanges: " + ex.Message); }
    }

    // ------------------------------------------------------------------ 0.13.0: hue presets

    /// <summary>The exact colour of each hue at full brightness: the band centres (0, 30, 60, 120, 240, 270, 300 degrees).</summary>
    static readonly float[][] HueRgb =
    {
        null,
        new[] { 1f, 0f, 0f }, new[] { 1f, 0.5f, 0f }, new[] { 1f, 1f, 0f }, new[] { 0f, 1f, 0f },
        new[] { 0f, 0f, 1f }, new[] { 0.5f, 0f, 1f }, new[] { 1f, 0f, 1f },
    };
    /// <summary>How each hue shows in the menu (blue and indigo lightened to read on the dark menu).</summary>
    static readonly colorX[] HueShown =
    {
        colorX.White,
        new colorX(1f, 0.15f, 0.15f, 1f, ColorProfile.sRGB), new colorX(1f, 0.55f, 0.1f, 1f, ColorProfile.sRGB), new colorX(1f, 0.95f, 0.2f, 1f, ColorProfile.sRGB),
        new colorX(0.2f, 1f, 0.3f, 1f, ColorProfile.sRGB), new colorX(0.3f, 0.5f, 1f, 1f, ColorProfile.sRGB), new colorX(0.55f, 0.4f, 1f, 1f, ColorProfile.sRGB),
        new colorX(1f, 0.35f, 1f, 1f, ColorProfile.sRGB),
    };

    PaintHue _appliedHue = PaintHue.None;
    colorX _seenColor;
    bool _hueStarted;
    ContextMenuItem _softItem, _halfItem, _firmItem;

    /// <summary>The colour space of what the brush paints on (the hue is exact there): the target under the brush, else the
    /// last one painted, else Linear (most imported meshes).</summary>
    ColorProfile TargetProfile
    {
        get
        {
            try { var t = (_hoverTarget ?? _lastStrokeTarget)?.BrushTarget; if (t != null) return t.Profile; } catch { }
            return ColorProfile.Linear;
        }
    }

    /// <summary>The colour of a hue at a brightness (0 black .. 1 full), in the target's colour space.</summary>
    colorX HueColor(PaintHue hue, float bright)
    {
        var prof = TargetProfile;
        bright = Math.Clamp(float.IsFinite(bright) ? bright : 1f, 0f, 1f);
        var rgb = (int)hue > 0 && (int)hue < HueRgb.Length ? HueRgb[(int)hue] : null;
        return rgb == null ? new colorX(bright, bright, bright, 1f, prof) : new colorX(rgb[0] * bright, rgb[1] * bright, rgb[2] * bright, 1f, prof);
    }

    /// <summary>The brush colour's brightness (its softness) in the target's colour space.</summary>
    float Brightness()
    {
        var c = Color.Value.ToProfile(TargetProfile);
        return MathF.Max(c.r, MathF.Max(c.g, c.b));
    }

    /// <summary>The preset hue nearest a colour (None when it is grey, white or black).</summary>
    PaintHue HueOf(colorX color)
    {
        var c = color.ToProfile(TargetProfile);
        Span<float> w = stackalloc float[HueBands.Count];
        HueBands.Weights(c.r, c.g, c.b, w);
        int best = -1; float bw = 0.15f;
        for (int b = 0; b < HueBands.Count; b++) if (w[b] > bw) { bw = w[b]; best = b; }
        return best < 0 ? PaintHue.None : (PaintHue)(best + 1);
    }

    /// <summary>
    /// The user who changed Hue (the menu's shift item) gets the brush colour moved to that hue, at the same brightness
    /// (from black: full brightness, black has no hue). The user who changed Color some other way (the colour dialog, Pick)
    /// gets Hue moved to the nearest preset. Only on that user's client; everyone else just receives both fields.
    /// </summary>
    void SyncHueAndColor()
    {
        var local = World?.LocalUser;
        if (!_hueStarted) { _hueStarted = true; _appliedHue = Hue.Value; _seenColor = Color.Value; return; }
        if (Hue.Value != _appliedHue)
        {
            _appliedHue = Hue.Value;
            if (local != null && Hue.WasLastModifiedBy(local))
            {
                float v = Brightness();
                Color.Value = HueColor(Hue.Value, v > 0.02f ? v : 1f);
                _seenColor = Color.Value;
                UpdatePresetItems();
                SetStatus(Hue.Value == PaintHue.None
                    ? "hue none: grey/white paints the base layer (brightness = softness)"
                    : $"hue {Hue.Value}: paints the {Hue.Value.ToString().ToLowerInvariant()} hue layer (brightness = softness)");
                return;
            }
        }
        var col = Color.Value;
        if (!col.Equals(_seenColor))
        {
            _seenColor = col;
            if (local != null && Color.WasLastModifiedBy(local))
            {
                var h = HueOf(col);
                // black has no hue: keep the chosen one, so Soft brings it back
                if (h != Hue.Value && Brightness() > 0.02f) { _appliedHue = h; Hue.Value = h; UpdatePresetItems(); }
            }
        }
    }

    /// <summary>The Soft / Half / Firm menu items in the current hue (when the menu is open).</summary>
    void UpdatePresetItems()
    {
        try
        {
            if (_softItem != null && !_softItem.IsRemoved) _softItem.Color.Value = HueColor(Hue.Value, 1f);
            if (_halfItem != null && !_halfItem.IsRemoved) _halfItem.Color.Value = HueColor(Hue.Value, 0.5f);
        }
        catch { }
    }

    void SetBrightness(float v, string what)
    {
        Color.Value = HueColor(Hue.Value, v);
        _seenColor = Color.Value;
        SetStatus($"{what}{(Hue.Value != PaintHue.None ? " " + Hue.Value.ToString().ToLowerInvariant() : "")}: brightness {v:0.#}");
    }

    [SyncMethod(typeof(Delegate), null)]
    void OnSoftPreset(IButton button, ButtonEventData eventData) { try { SetBrightness(1f, "soft"); } catch (Exception ex) { SoftbodyRuntime.Warn("SoftbodyBrush soft: " + ex.Message); } }

    [SyncMethod(typeof(Delegate), null)]
    void OnHalfPreset(IButton button, ButtonEventData eventData) { try { SetBrightness(0.5f, "half"); } catch (Exception ex) { SoftbodyRuntime.Warn("SoftbodyBrush half: " + ex.Message); } }

    [SyncMethod(typeof(Delegate), null)]
    void OnFirmPreset(IButton button, ButtonEventData eventData) { try { SetBrightness(0f, "firm"); } catch (Exception ex) { SoftbodyRuntime.Warn("SoftbodyBrush firm: " + ex.Message); } }

    protected override void OnCommonUpdate()
    {
        base.OnCommonUpdate();
        try
        {
            UpdateVisuals();
            // Update() runs every frame for the user equipping or holding the brush, on their client only;
            // everywhere else (and when let go) the marker is hidden.
            if (Time.WorldTime - _lastLocalUse > 0.15) { SetMarker(false, default); _hoverTarget = null; SetStatus(IsEquipped ? "equipped" : "idle"); }
            FlushStatus();
        }
        catch (Exception ex) { SoftbodyRuntime.Warn("SoftbodyBrush update: " + ex.Message); }
    }

    /// <summary>Writes the drive targets when they differ (local writes; every client does this itself).</summary>
    void UpdateVisuals()
    {
        float r = Math.Clamp(float.IsFinite(Radius.Value) ? Radius.Value : 0.05f, 0.002f, 2f);
        var c = Color.Value;
        colorX fill, rim;
        switch (Mode.Value)
        {
            case BrushMode.Erase:
                fill = new colorX(1f, 1f, 1f, 0.08f, ColorProfile.sRGB);
                rim = new colorX(1f, 0.25f, 0.25f, 1f, ColorProfile.sRGB);
                break;
            case BrushMode.Pick:
                fill = new colorX(0f, 0f, 0f, 0.05f, ColorProfile.sRGB);
                rim = new colorX(0.2f, 0.9f, 1f, 1f, ColorProfile.sRGB);
                break;
            case BrushMode.Blur:
                fill = new colorX(0.8f, 0.8f, 1f, 0.12f, ColorProfile.sRGB);
                rim = new colorX(0.75f, 0.6f, 1f, 1f, ColorProfile.sRGB);
                break;
            default:
                fill = new colorX(c.r, c.g, c.b, 0.3f, c.profile);
                rim = new colorX(c.r, c.g, c.b, 1f, c.profile);
                break;
        }
        foreach (var d in _radiusDrives) if (d != null && d.IsLinkValid && d.Target.Value != r) d.Target.Value = r;
        foreach (var d in _fillDrives) if (d != null && d.IsLinkValid && d.Target.Value != fill) d.Target.Value = fill;
        foreach (var d in _rimDrives) if (d != null && d.IsLinkValid && d.Target.Value != rim) d.Target.Value = rim;
    }

    void SetMarker(bool on, float3 globalPoint)
    {
        if (_markerActive.IsLinkValid && _markerActive.Target.Value != on) _markerActive.Target.Value = on;
        if (on && _markerPosition.IsLinkValid)
        {
            var local = Slot.GlobalPointToLocal(in globalPoint);
            if (_markerPosition.Target.Value != local) _markerPosition.Target.Value = local;
        }
    }

    void SetStatus(string s) => _status = s;

    void FlushStatus()
    {
        if (_status == _flushedStatus || Time.WorldTime - _statusAt < 0.2) return;
        _statusAt = Time.WorldTime;
        _flushedStatus = _status;
        try { if (Status.CanWrite) Status.Value = _status; } catch { }
    }

    // ------------------------------------------------------------------ aiming

    struct Aim
    {
        public IPaintable Target;
        public IBrushTarget Inst;
        public Vector3 Local;          // where dabs go: the rest mesh in the renderer slot's space (skinned: bind space)
        public float3 Global;          // where the surface is shown (marker, laser end)
        public MeshHit Hit;
        public float LocalRadius;      // dab radius in the same space as Local
        public float Distance;         // metres along the ray (0 for touch)
        public bool Touch;
    }

    static Vector3 V(in float3 f) => new(f.x, f.y, f.z);
    static float3 F(in Vector3 v) => new(v.X, v.Y, v.Z);
    static float MeanScale(float3 s) => (MathF.Abs(s.x) + MathF.Abs(s.y) + MathF.Abs(s.z)) / 3f;

    /// <summary>Brush radius in metres (Radius times the brush's own scale).</summary>
    float WorldRadius() => Math.Clamp(float.IsFinite(Radius.Value) ? Radius.Value : 0.05f, 0.002f, 2f) * MeanScale(Slot.GlobalScale);

    /// <summary>The paint ray: the interaction laser when equipped (desktop: through the cursor), the holding laser
    /// when held with a laser, else the tip's forward direction.</summary>
    void GetRay(out float3 origin, out float3 dir)
    {
        origin = Tip; dir = TipForward;
        try
        {
            if (ActiveHandler != null) { origin = InteractionOrigin; dir = InteractionDirection; }
            else
            {
                var grabber = Slot.GetComponent<Grabbable>()?.Grabber;
                var h = grabber != null ? InteractionHandlerExtensions.FindInteractionHandler(grabber) : null;
                if (h != null && h.IsHoldingObjectsWithLaser && h.Laser != null) { origin = h.Laser.LastOrigin; dir = h.Laser.LastDirection; }
            }
        }
        catch { origin = Tip; dir = TipForward; }
        var d = V(dir);
        if (!(d.LengthSquared() > 1e-12f) || !float.IsFinite(d.X + d.Y + d.Z)) d = V(TipForward);
        dir = F(Vector3.Normalize(d));
    }

    /// <summary>Where the brush would paint. A tip within the radius of a surface (VR touch) wins over the ray.
    /// Gate held.</summary>
    bool FindAim(bool allowTouch, out Aim aim, float3? rayOrigin = null, float3? rayDir = null)
    {
        aim = default;
        float3 o, d;
        if (rayOrigin.HasValue && rayDir.HasValue) { o = rayOrigin.Value; d = rayDir.Value; }
        else GetRay(out o, out d);
        float worldRadius = WorldRadius();
        var tip = Tip;
        SoftbodyRuntime.CollectPaintTargets(World, _targets);
        bool found = false; float bestTouch = float.MaxValue, bestRay = float.MaxValue;
        if (allowTouch)
        {
            foreach (var c in _targets)
            {
                var inst = c.BrushTarget; var anchor = inst?.Anchor;
                if (anchor == null || anchor.IsRemoved) continue;
                float scale = MeanScale(anchor.GlobalScale);
                if (!(scale > 1e-6f)) continue;
                float localRadius = worldRadius / scale;
                var tl = V(anchor.GlobalPointToLocal(in tip));
                inst.PrepareBrushQuery(tl, tl); // skinned: query the posed mesh (no-op for plain meshes)
                if (!inst.Paint.ClosestPoint(tl, localRadius, out var h)) continue;
                float frac = h.T / localRadius;
                if (frac >= bestTouch) continue;
                bestTouch = frac; found = true;
                aim = new Aim { Target = c, Inst = inst, Local = inst.BindPoint(h), Global = anchor.LocalPointToGlobal(F(h.Point)), Hit = h,
                                LocalRadius = localRadius / inst.BindScale(h), Touch = true };
            }
            if (found) return true;
        }
        var end = o + d * MaxDistance;
        foreach (var c in _targets)
        {
            var inst = c.BrushTarget; var anchor = inst?.Anchor;
            if (anchor == null || anchor.IsRemoved) continue;
            float scale = MeanScale(anchor.GlobalScale);
            if (!(scale > 1e-6f)) continue;
            var lo = V(anchor.GlobalPointToLocal(in o));
            var le = V(anchor.GlobalPointToLocal(in end));
            inst.PrepareBrushQuery(lo, le); // skinned: query the posed mesh (no-op for plain meshes)
            if (!inst.Paint.Raycast(lo, le - lo, 1f, out var h)) continue;
            float dist = h.T * MaxDistance; // affine maps keep ratios along the line
            if (dist >= bestRay) continue;
            bestRay = dist; found = true;
            aim = new Aim { Target = c, Inst = inst, Local = inst.BindPoint(h), Global = anchor.LocalPointToGlobal(F(h.Point)), Hit = h,
                            LocalRadius = worldRadius / scale / inst.BindScale(h), Distance = dist };
        }
        return found;
    }

    /// <summary>The laser ends on the soft mesh's surface where the brush paints (only called while equipped).</summary>
    public override float3? OverrideTargetPoint(float3 origin, float3 direction)
    {
        try
        {
            var d = V(direction);
            if (!(d.LengthSquared() > 1e-12f)) return null;
            lock (SoftbodyRuntime.Gate)
                return FindAim(false, out var aim, origin, F(Vector3.Normalize(d))) ? aim.Global : null;
        }
        catch { return null; }
    }

    /// <summary>Every frame for the user equipping or holding the brush (their client only): hover marker and status.</summary>
    public override void Update(float primaryStrength, float2 secondaryAxis, Digital primary, Digital secondary)
    {
        base.Update(primaryStrength, secondaryAxis, primary, secondary);
        try { ResizeInput(secondaryAxis); }
        catch (Exception ex) { SoftbodyRuntime.Warn("SoftbodyBrush resize: " + ex.Message); }
        try
        {
            _lastLocalUse = Time.WorldTime;
            lock (SoftbodyRuntime.Gate)
            {
                if (FindAim(true, out var aim))
                {
                    _hoverTarget = aim.Target;
                    SetMarker(true, aim.Global);
                    if (!_stroking)
                        SetStatus($"{Mode.Value} on '{SafeName(aim.Target)}' ({(aim.Touch ? "touching" : $"{aim.Distance:0.00} m")}), {aim.Target.PaintDabs.Count} dabs");
                }
                else
                {
                    _hoverTarget = null;
                    SetMarker(false, default);
                    if (!_stroking) SetStatus(_targets.Count == 0 ? "no running Softbody or SkinnedSoftbody in this world" : "not pointing at a Softbody");
                }
            }
        }
        catch (Exception ex) { SoftbodyRuntime.Warn("SoftbodyBrush Update: " + ex.Message); }
    }

    static string SafeName(IPaintable c)
    {
        try { return c?.Slot?.Name ?? "?"; } catch { return "?"; }
    }

    // ------------------------------------------------------------------ painting (input callbacks: the using client only)

    bool _stroking;

    public override void OnPrimaryPress()
    {
        base.OnPrimaryPress();
        try
        {
            _stroking = true;
            _hasLastDab = false;
            _strokeTarget = null;
            if (Mode.Value == BrushMode.Paint || Mode.Value == BrushMode.Blur) { _lastStroke.Clear(); _lastStrokeTarget = null; }
            Step(press: true);
        }
        catch (Exception ex) { SoftbodyRuntime.Warn("SoftbodyBrush press: " + ex.Message); }
    }

    public override void OnPrimaryHold()
    {
        base.OnPrimaryHold();
        try { if (_stroking) Step(press: false); }
        catch (Exception ex) { SoftbodyRuntime.Warn("SoftbodyBrush hold: " + ex.Message); }
    }

    public override void OnPrimaryRelease()
    {
        base.OnPrimaryRelease();
        EndStroke();
    }

    public override void OnSecondaryPress()
    {
        base.OnSecondaryPress();
        try { Mode.Value = Mode.Value switch { BrushMode.Paint => BrushMode.Blur, BrushMode.Blur => BrushMode.Erase, BrushMode.Erase => BrushMode.Pick, _ => BrushMode.Paint }; }
        catch (Exception ex) { SoftbodyRuntime.Warn("SoftbodyBrush secondary: " + ex.Message); }
    }

    public override void OnDequipped()
    {
        base.OnDequipped();
        EndStroke();
    }

    void EndStroke()
    {
        if (_stroking && _lastStroke.Count > 0) SetStatus($"stroke: {_lastStroke.Count} dabs on '{SafeName(_lastStrokeTarget)}'");
        _stroking = false;
        _hasLastDab = false;
        _strokeTarget = null;
    }

    void Step(bool press)
    {
        lock (SoftbodyRuntime.Gate)
        {
            if (!FindAim(true, out var aim)) { _hasLastDab = false; return; }
            if (!ReferenceEquals(aim.Target, _strokeTarget)) { _hasLastDab = false; _strokeTarget = aim.Target; }
            switch (Mode.Value)
            {
                case BrushMode.Pick:
                    if (press) Pick(aim);
                    break;
                case BrushMode.Erase:
                    Erase(aim);
                    break;
                default:
                    PaintAlong(aim);
                    break;
            }
        }
    }

    void PaintAlong(in Aim aim)
    {
        var list = aim.Target.PaintDabs;
        float r = aim.LocalRadius;
        float spacing = Math.Clamp(float.IsFinite(Spacing.Value) ? Spacing.Value : 0.3f, 0.05f, 2f) * r;
        if (!_hasLastDab) { AddDab(list, aim.Target, aim.Local, r); return; }
        float dist = Vector3.Distance(_lastDabLocal, aim.Local);
        if (dist < spacing) return;
        if (dist > 10f * r) { AddDab(list, aim.Target, aim.Local, r); return; } // a jump, not a stroke: no dabs in between
        // fill the stroke between the last dab and here, one dab per spacing
        int steps = Math.Min(MaxDabsPerStep, (int)(dist / spacing));
        var from = _lastDabLocal;
        for (int k = 1; k <= steps; k++)
        {
            var p = k == steps ? aim.Local : Vector3.Lerp(from, aim.Local, k * spacing / dist);
            if (!AddDab(list, aim.Target, p, r)) break;
        }
    }

    bool AddDab(SyncList<Softbody.PaintDab> list, IPaintable target, Vector3 local, float localRadius)
    {
        if (list.Count >= PaintLayer.MaxDabs)
        {
            SetStatus($"'{SafeName(target)}' has the maximum of {PaintLayer.MaxDabs} dabs: erase some (or ClearPaint) to paint more");
            if (!_capWarned) { _capWarned = true; SoftbodyRuntime.Warn($"brush: '{SafeName(target)}' reached {PaintLayer.MaxDabs} paint dabs"); }
            return false;
        }
        var d = list.Add();
        d.Position.Value = F(local);
        d.Radius.Value = localRadius;
        d.Color.Value = Color.Value;
        d.Strength.Value = Math.Clamp(float.IsFinite(Strength.Value) ? Strength.Value : 0.7f, 0f, 1f);
        d.Hardness.Value = Math.Clamp(float.IsFinite(Hardness.Value) ? Hardness.Value : 0.5f, 0f, 1f);
        d.Blur.Value = Mode.Value == BrushMode.Blur;
        _lastStroke.Add(d);
        _lastStrokeTarget = target;
        _lastDabLocal = local;
        _hasLastDab = true;
        SetStatus($"{(Mode.Value == BrushMode.Blur ? "blurring" : "painting")} '{SafeName(target)}': {_lastStroke.Count} dabs this stroke, {list.Count} in all");
        return true;
    }

    void Erase(in Aim aim)
    {
        var list = aim.Target.PaintDabs;
        float r2 = aim.LocalRadius * aim.LocalRadius;
        int removed = 0;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var e = list[i];
            if (e == null) continue;
            if (Vector3.DistanceSquared(V(e.Position.Value), aim.Local) < r2) { list.RemoveAt(i); removed++; }
        }
        if (removed > 0) { _capWarned = false; SetStatus($"erased {removed} dabs on '{SafeName(aim.Target)}', {list.Count} left"); }
    }

    void Pick(in Aim aim)
    {
        var c = aim.Inst.Paint.SampleColor(aim.Hit);
        Color.Value = new colorX(Math.Clamp(c.X, 0f, 1f), Math.Clamp(c.Y, 0f, 1f), Math.Clamp(c.Z, 0f, 1f), 1f, aim.Inst.Profile);
        Mode.Value = BrushMode.Paint;
        SetStatus($"picked {Color.Value} from '{SafeName(aim.Target)}'");
    }

    // ------------------------------------------------------------------ resizing (local user only: Update runs on their client)

    /// <summary>VR: push the thumbstick up to grow the brush, down to shrink it (only clearly vertical pushes count,
    /// so sideways turning doesn't resize). Desktop: ] grows and [ shrinks (also keypad + and -): a tap is one
    /// 20% step, holding keeps going.</summary>
    void ResizeInput(float2 axis)
    {
        float dt = Math.Clamp(Time.Delta, 0f, 0.1f);
        float grow = 0f;
        if (MathF.Abs(axis.y) > 0.5f && MathF.Abs(axis.y) > 2f * MathF.Abs(axis.x))
            grow += MathF.Sign(axis.y) * (MathF.Abs(axis.y) - 0.5f) / 0.5f * ResizeRate * dt;

        var input = InputInterface;
        if (input != null && !input.IsKeyboardActive)
        {
            bool up = input.GetKey(Key.RightBracket) || input.GetKey(Key.KeypadPlus);
            bool down = input.GetKey(Key.LeftBracket) || input.GetKey(Key.KeypadMinus);
            if (input.GetKeyDown(Key.RightBracket) || input.GetKeyDown(Key.KeypadPlus)) { grow += MathF.Log(1.2f); _keyHeld = 0f; }
            if (input.GetKeyDown(Key.LeftBracket) || input.GetKeyDown(Key.KeypadMinus)) { grow -= MathF.Log(1.2f); _keyHeld = 0f; }
            if (up != down)
            {
                _keyHeld += dt;
                if (_keyHeld > 0.35f) grow += (up ? 1f : -1f) * ResizeRate * dt;
            }
            else _keyHeld = 0f;
        }
        if (grow != 0f) ScaleRadius(MathF.Exp(grow));
    }

    void ScaleRadius(float factor)
    {
        float r = float.IsFinite(Radius.Value) && Radius.Value > 0f ? Radius.Value : 0.05f;
        float nr = Math.Clamp(r * factor, MinRadius, MaxRadius);
        if (MathF.Abs(nr - r) < 1e-6f) return;
        Radius.Value = nr;
        SetStatus($"brush radius {nr * 100f:0.#} cm");
    }

    [SyncMethod(typeof(Delegate), null)]
    void OnBigger(IButton button, ButtonEventData eventData)
    {
        try { ScaleRadius(1.5f); } catch (Exception ex) { SoftbodyRuntime.Warn("SoftbodyBrush bigger: " + ex.Message); }
    }

    [SyncMethod(typeof(Delegate), null)]
    void OnSmaller(IButton button, ButtonEventData eventData)
    {
        try { ScaleRadius(1f / 1.5f); } catch (Exception ex) { SoftbodyRuntime.Warn("SoftbodyBrush smaller: " + ex.Message); }
    }

    // ------------------------------------------------------------------ spawning (0.11.3)

    /// <summary>
    /// A new brush half a metre in front of the local user's head, a little below eye level, its tip pointing away from
    /// them (the brush builds its own handle, tip and marker when attached). Called by the SpawnBrush inspector button of a
    /// Softbody or SkinnedSoftbody; runs on the client of whoever pressed it.
    /// </summary>
    internal static SoftbodyBrush SpawnFor(World world)
    {
        var root = world.LocalUser?.Root;
        float3 pos; floatQ rot;
        if (root != null && root.HeadSlot != null)
        {
            var head = root.HeadSlot;
            var fwd = head.Forward; fwd = new float3(fwd.x, 0f, fwd.z);
            if (fwd.x * fwd.x + fwd.z * fwd.z < 1e-6f) fwd = new float3(0f, 0f, 1f);
            fwd = fwd.Normalized;
            pos = head.GlobalPosition + fwd * 0.5f - new float3(0f, 0.2f, 0f);
            rot = floatQ.LookRotation(fwd, float3.Up);
        }
        else { pos = new float3(0f, 1.2f, 1f); rot = floatQ.Identity; }
        var slot = world.AddSlot("Softbody Brush");
        slot.GlobalPosition = pos;
        slot.GlobalRotation = rot;
        var brush = slot.AttachComponent<SoftbodyBrush>();
        SoftbodyRuntime.Log("spawned a Softbody brush for " + (world.LocalUser?.UserName ?? "?"));
        return brush;
    }

    // ------------------------------------------------------------------ context menu

    public override void GenerateMenuItems(InteractionHandler tool, ContextMenu menu)
    {
        base.GenerateMenuItems(tool, menu);
        try
        {
            // 0.11.3: one cycling mode button, and the paint colour from the menu (no inspector needed)
            menu.AddEnumShiftItem<BrushMode>(Mode, "Mode: {0}", (Uri)null, new colorX?(colorX.Green), null, false);
            menu.AddItem("Bigger brush", (Uri)null, new colorX?(colorX.Cyan), new ButtonEventHandler(OnBigger));
            menu.AddItem("Smaller brush", (Uri)null, new colorX?(colorX.Cyan), new ButtonEventHandler(OnSmaller));
            menu.AddItem("Change color", (Uri)null, new colorX?(Color.Value), new ButtonEventHandler(OnChangeColor));
            // 0.13.0: the hue (cycles in place: none, red .. violet), and the softness in that hue
            var hues = new List<OptionDescription<PaintHue>>();
            for (int h = 0; h < HueShown.Length; h++)
            {
                var ph = (PaintHue)h;
                hues.Add(new OptionDescription<PaintHue>(ph, ph == PaintHue.None ? "Hue: none (base)" : "Hue: " + ph, new colorX?(HueShown[h]), null, null, null));
            }
            menu.AddEnumShiftItem<PaintHue>(Hue, "Hue: {0}", (Uri)null, new colorX?(colorX.White), hues, false);
            _softItem = menu.AddItem("Soft", (Uri)null, new colorX?(HueColor(Hue.Value, 1f)), new ButtonEventHandler(OnSoftPreset));
            _halfItem = menu.AddItem("Half", (Uri)null, new colorX?(HueColor(Hue.Value, 0.5f)), new ButtonEventHandler(OnHalfPreset));
            _firmItem = menu.AddItem("Firm (black)", (Uri)null, new colorX?(new colorX(0.25f, 0.25f, 0.25f, 1f)), new ButtonEventHandler(OnFirmPreset));
            if (_lastStroke.Count > 0 && _lastStrokeTarget != null && !_lastStrokeTarget.IsRemoved)
                menu.AddItem("Undo last stroke", (Uri)null, new colorX?(colorX.Orange), new ButtonEventHandler(OnUndoStroke));
            if (_hoverTarget != null || _lastStrokeTarget != null)
                menu.AddItem("Clear all paint", (Uri)null, new colorX?(colorX.Red), new ButtonEventHandler(OnClearPaint));
        }
        catch (Exception ex) { SoftbodyRuntime.Warn("SoftbodyBrush menu: " + ex.Message); }
    }


    // the colour picker this user opened from the menu (local to them, in their user space; not synced)
    Slot _colorDialog;

    /// <summary>Opens Resonite's colour picker bound to the brush colour (live while dragging), like the built-in brushes.</summary>
    [SyncMethod(typeof(Delegate), null)]
    void OnChangeColor(IButton button, ButtonEventData eventData)
    {
        try
        {
            if (_colorDialog != null && !_colorDialog.IsRemoved) _colorDialog.Destroy();
            _colorDialog = LocalUserSpace.AddSlot("Softbody brush colour");
            ColorDialogHelper.SpawnColorDialog(_colorDialog, Color, true, false, false);
            ActiveHandler?.CloseContextMenu();
            SetStatus("pick a colour: brightness is the softness (white soft, black firm), the hue picks a SkinnedSoftbody's hue layer");
        }
        catch (Exception ex) { SoftbodyRuntime.Warn("SoftbodyBrush colour picker: " + ex.Message); }
    }

    [SyncMethod(typeof(Delegate), null)]
    void OnUndoStroke(IButton button, ButtonEventData eventData)
    {
        try
        {
            var target = _lastStrokeTarget;
            if (target == null || target.IsRemoved) return;
            int n = 0;
            foreach (var d in _lastStroke) if (d != null && !d.IsRemoved && target.PaintDabs.Remove(d)) n++;
            _lastStroke.Clear();
            SetStatus($"undid a stroke of {n} dabs on '{SafeName(target)}'");
        }
        catch (Exception ex) { SoftbodyRuntime.Warn("SoftbodyBrush undo: " + ex.Message); }
    }

    [SyncMethod(typeof(Delegate), null)]
    void OnClearPaint(IButton button, ButtonEventData eventData)
    {
        try
        {
            var target = _hoverTarget ?? _lastStrokeTarget;
            if (target == null || target.IsRemoved) return;
            target.ClearPaint();
            _lastStroke.Clear();
            _capWarned = false;
            SetStatus($"cleared the paint on '{SafeName(target)}'");
        }
        catch (Exception ex) { SoftbodyRuntime.Warn("SoftbodyBrush clear: " + ex.Message); }
    }
}
