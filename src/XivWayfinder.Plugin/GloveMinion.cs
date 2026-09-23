using System.Numerics;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using XivWayfinder.Core;
using Chara = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using CsQuaternion = FFXIVClientStructs.FFXIV.Common.Math.Quaternion;
using CsVector3 = FFXIVClientStructs.FFXIV.Common.Math.Vector3;

namespace XivWayfinder.Plugin;

/// <summary>
/// The minion glove: the game's own Wind-up Cursor minion model, shown as a purely client-side object that floats
/// beside the bead and points the way.
///
/// How: the model is found by name in the <c>Companion</c> sheet (its <c>Model</c>, a <c>ModelChara</c> row). A
/// local BattleChara is made with <c>ClientObjectManager.CreateBattleCharacter</c>, the way Brio and Ktisis
/// make theirs; its <c>ModelContainer.ModelCharaId</c> is set to that row, it is made untargetable, placed with
/// <c>SetPosition</c> and <c>SetRotation</c>, and drawn with <c>EnableDraw</c> one frame later. Each frame it is
/// moved and turned; when tilting is on, the draw object's rotation is also written with a pitch. It is removed
/// with <c>DeleteObjectByIndex</c>.
///
/// What it is not: a companion summon. Nothing is sent to the server; the object lives only in this client's
/// memory, so no other player sees it. It is removed when not wanted (<see cref="MinionPresence"/>), at once for
/// zone changes, logout, cutscenes, group pose and a hidden UI, and on unload. Everything here must run on the
/// framework thread. None of this has been observed in game.
/// </summary>
internal sealed unsafe class GloveMinion(IDataManager data, IPluginLog log)
{
    private const uint NoIndex = uint.MaxValue;

    private readonly MinionPresence presence = new();
    private bool resolved;
    private int modelChara = -1;
    private uint index = NoIndex;
    private Chara* spawned;
    private int frames;
    private Vector3 position = new(float.NaN);
    private float yaw = float.NaN;
    private float pitch;

    /// <summary>For the settings window: whether the model was found, and whether the glove is in the world.</summary>
    public string Status { get; private set; } = "not looked for yet";

    /// <summary>The model was found in the game data and creating the object has not given up.</summary>
    public bool Available => ModelCharaId() >= 0 && !presence.GaveUp;

    /// <summary>
    /// One framework frame. <paramref name="pose"/> is where the glove should be, null for nowhere;
    /// <paramref name="want"/> says whether it should exist and, if not, how urgently it must go.
    /// </summary>
    public void Update(MinionWant want, MinionPose? pose, float scale, bool tilt, double now, float dt)
    {
        try
        {
            UpdateCore(want, pose, scale, tilt, now, dt);
        }
        catch (Exception ex)
        {
            log.Error(ex, "XivWayfinder: the minion glove failed; removing it");
            presence.Failed(now);
            Remove();
        }
    }

    /// <summary>Starts over after a settings change: forgets failures, so creating is tried again.</summary>
    public void Retry() => presence.Reset();

    private void UpdateCore(MinionWant want, MinionPose? pose, float scale, bool tilt, double now, float dt)
    {
        var model = ModelCharaId();
        if (model < 0)
        {
            Remove();
            return;
        }

        if (pose is null && want == MinionWant.Show)
            want = MinionWant.Soft;
        if (!presence.Keep(want, now))
        {
            Remove();
            return;
        }

        if (index != NoIndex && !StillOurs(model))
        {
            // the game removed it (a zone change, logout): forget it without deleting someone else's object
            log.Debug("XivWayfinder: the minion glove's object went away; forgetting it");
            Forget();
        }

        if (pose is not { } p)
            return; // lingering: stay where it was

        if (index == NoIndex)
        {
            if (!presence.MayCreate(now) || !Create(model, p, scale, now))
                return;
        }

        var chara = spawned;
        frames++;
        position = MinionGlove.EasePosition(position, p.Position, dt, 0.08f, 6f);
        yaw = MinionGlove.EaseAngle(yaw, p.Yaw, dt, 0.1f);
        pitch = tilt ? Pulse.Approach(pitch, p.Pitch, dt, 0.15f) : 0f;
        chara->SetPosition(position.X, position.Y, position.Z);
        chara->SetRotation(yaw);
        if (frames == 2)
        {
            // one frame after creating it, as Brio and Ktisis do, so its model id and place are in before it loads
            chara->EnableDraw();
            Status = $"the Wind-up Cursor model (ModelChara {model}) is in the world, client-side only";
        }

        var draw = chara->DrawObject;
        if (frames > 2 && draw != null)
        {
            var s = Math.Clamp(scale, 0.25f, 4f);
            draw->Object.Scale = new CsVector3(s, s, s);
            if (tilt)
            {
                var q = MinionGlove.Orientation(yaw, pitch);
                draw->Object.Rotation = new CsQuaternion(q.X, q.Y, q.Z, q.W);
            }
        }
    }

    private bool Create(int model, MinionPose pose, float scale, double now)
    {
        var com = ClientObjectManager.Instance();
        if (com == null)
            return Fail(now, "the game's client object manager is not there");
        var id = com->CreateBattleCharacter();
        if (id == NoIndex || id > ushort.MaxValue)
            return Fail(now, "no free client-side object slot");
        var chara = com->GetObjectByIndex((ushort)id);
        if (chara == null)
        {
            com->DeleteObjectByIndex((ushort)id, 0);
            return Fail(now, "the new client-side object could not be found");
        }

        index = id;
        spawned = chara;
        frames = 0;
        position = pose.Position;
        yaw = pose.Yaw;
        pitch = 0f;
        chara->ModelContainer.ModelCharaId = model;
        chara->TargetableStatus &= ~ObjectTargetableFlags.IsTargetable;
        chara->Scale = Math.Clamp(scale, 0.25f, 4f);
        chara->SetPosition(pose.Position.X, pose.Position.Y, pose.Position.Z);
        chara->SetRotation(pose.Yaw);
        presence.Created();
        Status = $"the Wind-up Cursor model (ModelChara {model}) is being placed";
        log.Debug("XivWayfinder: minion glove created as client object {Index}, ModelChara {Model}", id, model);
        return true;
    }

    private bool Fail(double now, string why)
    {
        presence.Failed(now);
        Status = presence.GaveUp ? $"{why}: gave up; drawing the flat glove instead" : $"{why}: trying again shortly";
        log.Warning("XivWayfinder: minion glove not created: {Why}", why);
        return false;
    }

    /// <summary>Whether the slot still holds the object this made: the same slot, the same memory, the same model.</summary>
    private bool StillOurs(int model)
    {
        var com = ClientObjectManager.Instance();
        if (com == null || index > ushort.MaxValue)
            return false;
        var chara = com->GetObjectByIndex((ushort)index);
        return chara != null && chara == spawned && chara->ModelContainer.ModelCharaId == model;
    }

    /// <summary>Deletes the object if it is still there. Safe to call at any time on the framework thread.</summary>
    public void Remove()
    {
        if (index == NoIndex)
            return;
        try
        {
            var model = ModelCharaId();
            if (model >= 0 && StillOurs(model))
            {
                ClientObjectManager.Instance()->DeleteObjectByIndex((ushort)index, 0);
                log.Debug("XivWayfinder: minion glove removed (client object {Index})", index);
            }
        }
        finally
        {
            Forget();
            if (ModelCharaId() >= 0 && !presence.GaveUp)
                Status = "the Wind-up Cursor model was found; the glove appears while pointing the way";
        }
    }

    private void Forget()
    {
        index = NoIndex;
        spawned = null;
        frames = 0;
        position = new Vector3(float.NaN);
        yaw = float.NaN;
        pitch = 0f;
    }

    /// <summary>The Wind-up Cursor's ModelChara row, from the English Companion sheet; −1 when it is not there.</summary>
    private int ModelCharaId()
    {
        if (resolved)
            return modelChara;
        resolved = true;
        try
        {
            foreach (var row in data.GetExcelSheet<Lumina.Excel.Sheets.Companion>(ClientLanguage.English))
            {
                if (!MinionGlove.IsWindUpCursor(row.Singular.ExtractText()))
                    continue;
                var model = row.Model.RowId;
                if (model == 0 || model > int.MaxValue || row.Model.ValueNullable is null)
                    continue;
                modelChara = (int)model;
                Status = $"the Wind-up Cursor model was found (Companion {row.RowId}, ModelChara {model}); the glove appears while pointing the way";
                if (row.RowId != MinionGlove.MeasuredCompanionRow || model != MinionGlove.MeasuredModelChara)
                    log.Information("XivWayfinder: the Wind-up Cursor is Companion {Row}, ModelChara {Model} in this game version", row.RowId, model);
                return modelChara;
            }

            Status = "the Wind-up Cursor minion was not found in the game data: drawing the flat glove instead";
        }
        catch (Exception ex)
        {
            Status = "the Companion sheet could not be read: drawing the flat glove instead";
            log.Warning(ex, "XivWayfinder: looking up the Wind-up Cursor failed");
        }

        return modelChara;
    }
}
