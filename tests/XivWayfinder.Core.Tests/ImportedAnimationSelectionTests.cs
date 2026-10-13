namespace XivWayfinder.Core.Tests;

public sealed class ImportedAnimationSelectionTests
{
    [Theory]
    [InlineData("Animation 1")]
    [InlineData("Animation001")]
    [InlineData("animation")]
    [InlineData("ANIMATION_001")]
    public void SingleGenericClipAnimatesMovementButIsNeverAssumedToBeIdle(string name)
    {
        string[] names=[name];
        foreach(var grounded in new[]{true,false})
        {
            Assert.Equal(0,ImportedAnimationSelection.Choose(names,null,1.5f,grounded));
            Assert.Equal(0,ImportedAnimationSelection.Choose(names,null,4.5f,grounded));
            Assert.Equal(-1,ImportedAnimationSelection.Choose(names,null,0,grounded));
            Assert.Equal(-1,ImportedAnimationSelection.Choose(names,null,float.NaN,grounded));
        }
    }

    [Theory]
    [InlineData("Attack")]
    [InlineData("Death")]
    [InlineData("Take 001")]
    [InlineData("mixamo.com")]
    [InlineData("Animation1_Attack")]
    [InlineData("Animation1_Left")]
    [InlineData("WalkStart")]
    public void SingleNamedUnknownOrOneShotClipStillRequiresManualSelection(string name)
    {
        Assert.Equal(-1,ImportedAnimationSelection.Choose([name],null,1.5f,true));
        Assert.Equal(0,ImportedAnimationSelection.Choose([name],name,0,true));
    }

    [Fact]
    public void MultipleGenericClipsAreNotArbitrarilySelected()
    {
        Assert.Equal(-1,ImportedAnimationSelection.Choose(["Animation 1","Animation 2"],null,1.5f,true));
        Assert.Equal(-1,ImportedAnimationSelection.Choose(["Attack","Animation 1"],null,1.5f,true));
    }

    [Fact]
    public void GenericSingleClipAdvancesThroughMovementFadeAndReturnsToRest()
    {
        string[] names=["Animation 1"];
        var clips=new XivVisualAssets.Animation.LocomotionClips(
            ImportedAnimationSelection.Choose(names,null,0,true),
            ImportedAnimationSelection.Choose(names,null,1.5f,true),
            ImportedAnimationSelection.Choose(names,null,4.5f,true));
        var state=new XivVisualAssets.Animation.VisualAnimationState([2]);
        Assert.Equal(-1,state.Update(clips,0,.1).ToClip);
        XivVisualAssets.Animation.VisualAnimationFrame frame=default;
        for(var i=0;i<4;i++) frame=state.Update(clips,1.5f,.1);
        Assert.Equal(0,frame.ToClip); Assert.Equal(1,frame.Blend); Assert.True(frame.ToTime>0);
        for(var i=0;i<4;i++) frame=state.Update(clips,0,.1);
        Assert.Equal(-1,frame.ToClip); Assert.Equal(1,frame.Blend);
    }

    [Fact] public void WalkingOnlyAssetDoesNotWalkInPlaceAtRest()
    {
        Assert.Equal(-1,ImportedAnimationSelection.Choose(["Walking"],"",0,true));
        Assert.Equal(0,ImportedAnimationSelection.Choose(["Walking"],"",1.5f,true));
    }

    [Fact] public void FoxSurveyIsItsIdleAndRunIsOnlyChosenWhileMoving()
    {
        string[] names=["Survey","Walk","Run"];
        Assert.Equal(0,ImportedAnimationSelection.Choose(names,"",0,true));
        Assert.Equal(1,ImportedAnimationSelection.Choose(names,"",1.5f,true));
        Assert.Equal(2,ImportedAnimationSelection.Choose(names,"",4.5f,true));
    }

    [Fact] public void UnknownClipsRequireAnExplicitChoice()
    {
        string[] names=["Attack","Take 001","mixamo.com"];
        Assert.Equal(-1,ImportedAnimationSelection.Choose(names,"",4,true));
        Assert.Equal(2,ImportedAnimationSelection.Choose(names,"mixamo.com",0,true));
        Assert.Equal(-1,ImportedAnimationSelection.Choose(names,"deleted clip",0,true));
    }

    [Fact] public void FloatersUseTheirOwnLoopsWithoutWalking()
    {
        string[] names=["Walk","Idle","Flying","Hover"];
        Assert.Equal(1,ImportedAnimationSelection.Choose(names,"",0,false));
        Assert.Equal(2,ImportedAnimationSelection.Choose(names,"",2,false));
        Assert.Equal(0,ImportedAnimationSelection.Choose(["Swimming"],"",0,false));
        Assert.Equal(-1,ImportedAnimationSelection.Choose(["Walk"],"",3,false));
    }

    [Fact] public void NamedNamespacesCamelCaseAndSuffixesRemainSearchable()
    {
        string[] names=["Armature|Idle_Loop","Rig|Walking.001","RUN02"];
        Assert.Equal(0,ImportedAnimationSelection.Choose(names,"",0,true));
        Assert.Equal(1,ImportedAnimationSelection.Choose(names,"",1,true));
        Assert.Equal(2,ImportedAnimationSelection.Choose(names,"",5,true));
    }

    [Fact] public void TransitionsDirectionalClipsAndIncidentalSubstringsAreNotLoops()
    {
        string[] names=["Prune","Overrun","IdleToWalk","Run_Start","WalkBackwards","RunLeft","BattleIdle"];
        Assert.Equal(6,ImportedAnimationSelection.Choose(names,"",0,true));
        Assert.Equal(6,ImportedAnimationSelection.Choose(names,"",4,true));
        Assert.Equal(2,ImportedAnimationSelection.Choose(names,"IdleToWalk",0,true));
    }
}
