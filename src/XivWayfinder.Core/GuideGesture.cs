using System.Buffers.Binary;

namespace XivWayfinder.Core;

public enum GuideGestureKind { Wait, SitGround, Beckon }

public readonly record struct GuideGestureFrame(GuideGestureKind Kind, long SequenceId, float ElapsedSeconds, bool Paused = false)
{
    public bool Valid => Kind is GuideGestureKind.Wait or GuideGestureKind.SitGround or GuideGestureKind.Beckon
        && SequenceId >= 0 && float.IsFinite(ElapsedSeconds) && ElapsedSeconds >= 0;
}

public readonly record struct GuideGestureCapabilities(bool Wait, bool SitGround, bool Beckon)
{
    public bool Supports(GuideGestureKind kind) => kind switch
    { GuideGestureKind.Wait => Wait, GuideGestureKind.SitGround => SitGround, GuideGestureKind.Beckon => Beckon, _ => false };
}

public readonly record struct GuideGestureSample(bool Restart, float Time, float Rate);

/// <summary>Sequence-owned authored playback. Holds and clock gaps never accumulate a delayed fast-forward.</summary>
public sealed class GuideGestureClock
{
    private GuideGestureFrame? previous;
    private float time;
    public void Reset() { previous = null; time = 0; }
    public GuideGestureSample Update(GuideGestureFrame frame, float dt)
    {
        if (!frame.Valid) { Reset(); return default; }
        var restart = previous is not { } before || before.Kind != frame.Kind || before.SequenceId != frame.SequenceId
            || frame.ElapsedSeconds < before.ElapsedSeconds;
        var elapsed = restart ? 0 : frame.ElapsedSeconds - previous!.Value.ElapsedSeconds;
        if (restart) time = 0;
        if (frame.Paused || elapsed > .5f || !float.IsFinite(dt) || dt <= 0) elapsed = 0;
        elapsed = Math.Clamp(elapsed, 0, .1f);
        time += elapsed; previous = frame;
        return new(restart, time, dt > 0 && float.IsFinite(dt) ? Math.Clamp(elapsed / dt, 0, 2) : 0);
    }
}

public readonly record struct GuideImportedGestures(int Wait, int SitGround, int Beckon, GuideSitClips Sitting = default)
{
    public GuideGestureCapabilities Capabilities => new(Wait >= 0, Sitting.Valid, Beckon >= 0);
    public int Clip(GuideGestureKind kind) => kind switch
    { GuideGestureKind.Wait => Wait, GuideGestureKind.SitGround => SitGround, GuideGestureKind.Beckon => Beckon, _ => -1 };
}

public static class GuideGestureAnimation
{
    public static GuideImportedGestures Imported(IReadOnlyList<GuideAnimationClip> clips)
    {
        var wait = -1; var sit = -1; var beckon = -1;
        for (var i = 0; i < clips.Count; i++)
        {
            var clip = clips[i];
            if (!float.IsFinite(clip.DurationSeconds) || clip.DurationSeconds is <= 0 or > 120) continue;
            var name = new string(clip.Name.ToLowerInvariant().Where(char.IsLetter).ToArray());
            if (wait < 0 && name is "idle" or "wait" or "waiting" or "rest" or "standingidle") wait = i;
            if (sit < 0 && name is "groundsit" or "sitground" or "sitonground" or "floorsit" or "groundsittingidle" or "crossleggedsit") sit = i;
            if (beckon < 0 && name is "beckon" or "beckoning" or "comehere" or "comeon" or "followme") beckon = i;
        }
        return new(wait, sit, beckon);
    }

    /// <summary>TMDH's bounded timeline frame length; the native/PAP format uses 30 frames per second.</summary>
    public static float TimelineSeconds(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 28 || !bytes[..4].SequenceEqual("TMLB"u8)
            || !bytes.Slice(12, 4).SequenceEqual("TMDH"u8)) return 0;
        var size = BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]);
        var count = BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]);
        var chunk = BinaryPrimitives.ReadInt32LittleEndian(bytes[16..]);
        var frames = BinaryPrimitives.ReadInt16LittleEndian(bytes[24..]);
        return size >= 28 && size <= bytes.Length && count is >= 1 and <= 4096 && chunk == 16 && frames is > 0 and <= 3600
            ? frames / 30f : 0;
    }
}
