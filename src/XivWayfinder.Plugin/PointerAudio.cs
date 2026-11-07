using System.Diagnostics;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using XivWayfinder.Core;
namespace XivWayfinder.Plugin;

/// <summary>Bounded PCM output on its own worker. The framework only publishes a fresh audio intent.</summary>
internal sealed class PointerAudio : IDisposable
{
    private readonly record struct Intent(float Hum, float Volume, long Bell, long Updated);
    private IntentBox intent = new(new(0, 0, 0, 0));
    private sealed record IntentBox(Intent Value);
    private readonly CancellationTokenSource stop = new();
    private readonly Task worker;
    private long bell, previewStamp;
    public void Preview() => Interlocked.Exchange(ref previewStamp, Stopwatch.GetTimestamp());
    public PointerAudio(IPluginLog log) => worker = Task.Run(async () =>
    {
        try { await Run(); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) { log.Warning(ex, "Wayfinder pointer sound is unavailable; the visual guide remains active."); }
    });
    public void Update(float hum, bool chime, float volume)
    {
        if (chime) bell++;
        Volatile.Write(ref intent, new(new(hum, volume, bell, Stopwatch.GetTimestamp())));
    }
    public void Dispose() { Update(0, false, 0); stop.Cancel(); }
    private async Task Run()
    {
        nint device = 0, memory = 0, header = 0;
        const int rate = 24000, count = 1200;
        var pcm = new short[count]; var gain = 0f; var sample = 0L; var lastBell = 0L; var bellSample = long.MinValue;
        var previewRang = 0L;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var state = Volatile.Read(ref intent).Value;
                var fresh = Stopwatch.GetElapsedTime(state.Updated).TotalSeconds < .5;
                var volume = fresh ? state.Volume : 0;
                if (state.Bell != lastBell)
                {
                    lastBell = state.Bell;
                    if (fresh && volume > 0) bellSample = sample;
                }
                var preview = Interlocked.Read(ref previewStamp);
                var previewAge = Stopwatch.GetElapsedTime(preview).TotalSeconds;
                var previewing = preview != 0 && previewAge < 3;
                if (preview != 0 && previewAge is >= 3 and < 4 && previewRang != preview && fresh && volume > 0)
                { previewRang = preview; bellSample = sample; }
                var ringing = bellSample >= 0 && sample - bellSample < rate * 6;
                if (volume <= 0) { bellSample = long.MinValue; ringing = false; }
                if (device == 0 && volume > 0 && (state.Hum > 0 || ringing || previewing))
                {
                    var format = new WaveFormat { Tag = 1, Channels = 1, Rate = rate, BytesPerSecond = rate * 2, Align = 2, Bits = 16 };
                    Check(waveOutOpen(out device, uint.MaxValue, ref format, 0, 0, 0));
                    memory = Marshal.AllocHGlobal(count * 2); header = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHeader>());
                }
                if (device == 0) { await Task.Delay(50, stop.Token); continue; }
                var wanted = fresh && volume > 0 ? previewing ? (float)(previewAge / 3) : state.Hum : 0;
                for (var i = 0; i < count; i++, sample++)
                {
                    gain += (wanted - gain) / (rate * .12f);
                    pcm[i] = PointerSound.Sample((double)sample / rate, gain,
                        ringing ? (double)(sample - bellSample) / rate : -1, volume);
                }
                Marshal.Copy(pcm, 0, memory, count);
                Marshal.StructureToPtr(new WaveHeader { Data = memory, Length = count * 2 }, header, false);
                Check(waveOutPrepareHeader(device, header, (uint)Marshal.SizeOf<WaveHeader>()));
                try
                {
                    Check(waveOutWrite(device, header, (uint)Marshal.SizeOf<WaveHeader>()));
                    var started = Stopwatch.GetTimestamp();
                    while ((Marshal.PtrToStructure<WaveHeader>(header).Flags & 1) == 0)
                    {
                        stop.Token.ThrowIfCancellationRequested();
                        if (Stopwatch.GetElapsedTime(started).TotalSeconds > 1) throw new TimeoutException("Pointer audio output stalled.");
                        await Task.Delay(5, stop.Token);
                    }
                }
                finally { if (stop.IsCancellationRequested || (Marshal.PtrToStructure<WaveHeader>(header).Flags & 1) == 0) waveOutReset(device); waveOutUnprepareHeader(device, header, (uint)Marshal.SizeOf<WaveHeader>()); }
            }
        }
        finally
        {
            if (device != 0) { waveOutReset(device); waveOutClose(device); }
            if (header != 0) Marshal.FreeHGlobal(header);
            if (memory != 0) Marshal.FreeHGlobal(memory);
        }
    }
    private static void Check(uint code) { if (code != 0) throw new InvalidOperationException("waveOut error " + code); }
    [StructLayout(LayoutKind.Sequential, Pack = 2)] private struct WaveFormat
    { public ushort Tag, Channels; public uint Rate, BytesPerSecond; public ushort Align, Bits, Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct WaveHeader
    { public nint Data; public uint Length, Recorded; public nint User; public uint Flags, Loops; public nint Next, Reserved; }
    [DllImport("winmm.dll")] private static extern uint waveOutOpen(out nint handle, uint device, ref WaveFormat format, nint callback, nint instance, uint flags);
    [DllImport("winmm.dll")] private static extern uint waveOutPrepareHeader(nint handle, nint header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutWrite(nint handle, nint header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutUnprepareHeader(nint handle, nint header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutReset(nint handle);
    [DllImport("winmm.dll")] private static extern uint waveOutClose(nint handle);
}
