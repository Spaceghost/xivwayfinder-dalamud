using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Lumina.Excel.Sheets;

namespace XivWayfinder.Plugin;

/// <summary>Builds an event or battle character's authored appearance on our own undrawn local actor.</summary>
internal static unsafe class GuideNpcAppearance
{
    public static void Apply(Character* actor, ENpcBase npc)
    {
        var customize=actor->DrawData.CustomizeData.Data;
        ReadOnlySpan<byte> appearance=[(byte)npc.Race.RowId,npc.Gender,npc.BodyType,npc.Height,
            (byte)npc.Tribe.RowId,npc.Face,npc.HairStyle,npc.HairHighlight,npc.SkinColor,
            npc.EyeHeterochromia,npc.HairColor,npc.HairHighlightColor,npc.FacialFeature,
            npc.FacialFeatureColor,npc.Eyebrows,npc.EyeColor,npc.EyeShape,npc.Nose,
            npc.Jaw,npc.Mouth,npc.LipColor,npc.BustOrTone1,npc.ExtraFeature1,
            npc.ExtraFeature2OrBust,npc.FacePaint,npc.FacePaintColor];
        appearance.CopyTo(customize);
        ClearEquipment(actor);
        if(npc.NpcEquip.RowId!=0 && npc.NpcEquip.ValueNullable is {} outfit)
            ApplyOutfit(actor,outfit);
        SetOutfit(actor->DrawData.EquipmentModelIds,
            [npc.ModelHead,npc.ModelBody,npc.ModelHands,npc.ModelLegs,npc.ModelFeet,npc.ModelEars,npc.ModelNeck,npc.ModelWrists,npc.ModelRightRing,npc.ModelLeftRing],
            [npc.DyeHead.RowId,npc.DyeBody.RowId,npc.DyeHands.RowId,npc.DyeLegs.RowId,npc.DyeFeet.RowId,npc.DyeEars.RowId,npc.DyeNeck.RowId,npc.DyeWrists.RowId,npc.DyeRightRing.RowId,npc.DyeLeftRing.RowId],
            [npc.Dye2Head.RowId,npc.Dye2Body.RowId,npc.Dye2Hands.RowId,npc.Dye2Legs.RowId,npc.Dye2Feet.RowId,npc.Dye2Ears.RowId,npc.Dye2Neck.RowId,npc.Dye2Wrists.RowId,npc.Dye2RightRing.RowId,npc.Dye2LeftRing.RowId],true);
        if(npc.ModelMainHand!=0) actor->DrawData.WeaponData[0].ModelId=Weapon(npc.ModelMainHand,npc.DyeMainHand.RowId,npc.Dye2MainHand.RowId);
        if(npc.ModelOffHand!=0) actor->DrawData.WeaponData[1].ModelId=Weapon(npc.ModelOffHand,npc.DyeOffHand.RowId,npc.Dye2OffHand.RowId);
        actor->DrawData.IsHatHidden=false;
        actor->DrawData.IsVisorToggled=npc.Visor;
        actor->DrawData.IsWeaponHidden=true;
    }

    public static void Apply(Character* actor, BNpcBase npc)
    {
        actor->DrawData.CustomizeData.Data.Clear();
        if (npc.BNpcCustomize.RowId != 0 && npc.BNpcCustomize.ValueNullable is {} customize)
        {
            ReadOnlySpan<byte> appearance = [(byte)customize.Race.RowId,customize.Gender,customize.BodyType,customize.Height,
                (byte)customize.Tribe.RowId,customize.Face,customize.HairStyle,customize.HairHighlight,customize.SkinColor,
                customize.EyeHeterochromia,customize.HairColor,customize.HairHighlightColor,customize.FacialFeature,
                customize.FacialFeatureColor,customize.Eyebrows,customize.EyeColor,customize.EyeShape,customize.Nose,
                customize.Jaw,customize.Mouth,customize.LipColor,customize.BustOrTone1,customize.ExtraFeature1,
                customize.ExtraFeature2OrBust,customize.FacePaint,customize.FacePaintColor];
            appearance.CopyTo(actor->DrawData.CustomizeData.Data);
        }
        ClearEquipment(actor);
        if (npc.NpcEquip.RowId != 0 && npc.NpcEquip.ValueNullable is {} outfit)
            ApplyOutfit(actor,outfit);
        actor->DrawData.IsHatHidden = false;
        actor->DrawData.IsVisorToggled = false;
        actor->DrawData.IsWeaponHidden = true;
    }

    private static void ClearEquipment(Character* actor)
    {
        actor->DrawData.EquipmentModelIds.Clear();
        foreach(ref var weapon in actor->DrawData.WeaponData) weapon.ModelId=default;
        actor->DrawData.GlassesIds.Clear();
    }

    private static void ApplyOutfit(Character* actor, NpcEquip outfit)
    {
        SetOutfit(actor->DrawData.EquipmentModelIds,
            [outfit.ModelHead,outfit.ModelBody,outfit.ModelHands,outfit.ModelLegs,outfit.ModelFeet,outfit.ModelEars,outfit.ModelNeck,outfit.ModelWrists,outfit.ModelRightRing,outfit.ModelLeftRing],
            [outfit.DyeHead.RowId,outfit.DyeBody.RowId,outfit.DyeHands.RowId,outfit.DyeLegs.RowId,outfit.DyeFeet.RowId,outfit.DyeEars.RowId,outfit.DyeNeck.RowId,outfit.DyeWrists.RowId,outfit.DyeRightRing.RowId,outfit.DyeLeftRing.RowId],
            [outfit.Dye2Head.RowId,outfit.Dye2Body.RowId,outfit.Dye2Hands.RowId,outfit.Dye2Legs.RowId,outfit.Dye2Feet.RowId,outfit.Dye2Ears.RowId,outfit.Dye2Neck.RowId,outfit.Dye2Wrists.RowId,outfit.Dye2RightRing.RowId,outfit.Dye2LeftRing.RowId],false);
        actor->DrawData.WeaponData[0].ModelId=Weapon(outfit.ModelMainHand,outfit.DyeMainHand.RowId,outfit.Dye2MainHand.RowId);
        actor->DrawData.WeaponData[1].ModelId=Weapon(outfit.ModelOffHand,outfit.DyeOffHand.RowId,outfit.Dye2OffHand.RowId);
        for(var i=0;i<Math.Min(outfit.Glasses.Count,actor->DrawData.GlassesIds.Length);i++) actor->DrawData.GlassesIds[i]=(ushort)outfit.Glasses[i].RowId;
    }

    // ENpc's ulong weapon fields use the 32-bit all-ones value for an explicit
    // empty slot. Zero retains NpcEquip in Apply; the sentinel must clear it.
    // This follows the game's SetupEventNpc, including removal of stale dyes.
    private static WeaponModelId Weapon(ulong model,uint dye,uint dye2)=>model==uint.MaxValue
        ? default : new(){Value=model,Stain0=(byte)dye,Stain1=(byte)dye2};

    private static void SetOutfit(Span<EquipmentModelId> slots,ReadOnlySpan<uint> models,ReadOnlySpan<uint> dyes,ReadOnlySpan<uint> dyes2,bool overrides)
    {
        for(var i=0;i<slots.Length;i++)
        {
            // SetupEventNpc clears this sentinel in both direct ENpc overrides
            // and inherited NpcEquip. It is not equipment model 65535.
            if(models[i]==uint.MaxValue)
            {
                slots[i]=default;
                continue;
            }
            if(!overrides || models[i]!=0)
            {
                // An explicit replacement item carries its own dyes, including
                // zero (undyed); it must not inherit the previous outfit's dye.
                slots[i]=new EquipmentModelId{Value=models[i],Stain0=(byte)dyes[i],Stain1=(byte)dyes2[i]};
                continue;
            }
            if(!overrides || dyes[i]!=0) slots[i].Stain0=(byte)dyes[i];
            if(!overrides || dyes2[i]!=0) slots[i].Stain1=(byte)dyes2[i];
        }
    }
}
