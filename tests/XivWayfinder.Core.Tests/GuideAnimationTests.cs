using System.Buffers.Binary;
using System.Text;

namespace XivWayfinder.Core.Tests;

public sealed class GuideAnimationTests
{
    [Fact] public void ChocoboChickUsesItsAvailableBattleRunAtBothPaces()
    {
        var clips=GuideAnimationSet.FromNames(["cbbm_02f_lp0","cbnm_id0","cbnm_idact1"]);
        Assert.Equal((ushort)3,clips.Choose(0,true).Timeline);
        Assert.Equal((ushort)50,clips.Choose(1.5f,true).Timeline);
        Assert.Equal((ushort)50,clips.Choose(4.5f,true).Timeline);
        Assert.True(clips.Choose(1.5f,true).Speed<clips.Choose(4.5f,true).Speed);
    }

    [Fact] public void MoogleUsesItsFlightClipAndNoAbsentWalkClip()
    {
        var clips=GuideAnimationSet.FromNames(["cbnm_02f_lp0","cbnm_id0","cbnm_idact1"]);
        Assert.Equal((ushort)0,clips.Walk);
        Assert.Equal((ushort)22,clips.Choose(1.5f,true).Timeline);
        Assert.Equal((ushort)3,clips.Choose(4.5f,false).Timeline);
    }

    [Fact] public void PropsWithoutLocomotionKeepTheirOwnIdle()
    {
        var clips=GuideAnimationSet.FromNames(["cbnm_id0"]);
        Assert.Equal(new GuideAnimationPose(3,1,false),clips.Choose(5,true));
        Assert.Equal((ushort)0,default(GuideAnimationSet).Choose(5,true).Timeline);
    }

    [Fact] public void PapNamesAreBoundedToTheirHeaderTable()
    {
        var bytes=Pack("cbnm_id0","cbbm_02f_lp0");
        Assert.Equal(new[]{"cbnm_id0","cbbm_02f_lp0"},GuideAnimationPack.Names(bytes));
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(8),3000);
        Assert.Empty(GuideAnimationPack.Names(bytes));
        bytes=Pack("cbnm_id0");
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14),int.MaxValue);
        Assert.Empty(GuideAnimationPack.Names(bytes));
        Assert.Empty(GuideAnimationPack.Names(bytes.AsSpan(0,20)));
    }

    [Fact] public void DemihumanDiscoveryCombinesItsOwnBodyAndSeparateMovementPacks()
    {
        var files=new Dictionary<string,byte[]>
        {
            ["chara/demihuman/d0001/animation/a0001/bt_common/resident/demihuman.pap"]=Pack("cbbm_id0"),
            ["chara/demihuman/d0001/animation/a0001/bt_common/resident/move_a.pap"]=Pack("cbbm_01f_lp0","cbbm_02f_lp0"),
        };
        var clips=GuideAnimationPack.ReadResident("chara/demihuman/d0001",1,2,path=>files.GetValueOrDefault(path));
        Assert.Equal(new GuideAnimationSet(34,41,50),clips);
        Assert.Equal((ushort)41,clips.Choose(1.5f,true).Timeline);
    }

    [Fact] public void HumanDiscoveryUsesTheDrawObjectsAnimationVariant()
    {
        var files=new Dictionary<string,byte[]>
        {
            ["chara/human/c1101/animation/a0002/bt_common/resident/idle.pap"]=Pack("cbnm_id0"),
            ["chara/human/c1101/animation/a0002/bt_common/resident/move_b.pap"]=Pack("cbnm_02f_lp0"),
        };
        var clips=GuideAnimationPack.ReadResident("chara/human/c1101/",2,1,path=>files.GetValueOrDefault(path));
        Assert.Equal(new GuideAnimationSet(3,0,22),clips);
        Assert.Equal(default,GuideAnimationPack.ReadResident("chara/human/c1101",1,1,path=>files.GetValueOrDefault(path)));
    }

    private static byte[] Pack(params string[] names)
    {
        var bytes=new byte[26+40*names.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes,0x20706170);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(8),(short)names.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14),26);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18),bytes.Length);
        for(var i=0;i<names.Length;i++) Encoding.ASCII.GetBytes(names[i],bytes.AsSpan(26+i*40));
        return bytes;
    }
}
