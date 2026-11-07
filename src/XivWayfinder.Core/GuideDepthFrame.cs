namespace XivWayfinder.Core;

/// <summary>
/// Clear-once lifetime of the custom world depth for successive native render
/// frames. The caller serializes access and performs the actual GPU clear.
/// The swapchain's last-present count remains the same through native game
/// rendering and all pre-Present UI fallback callbacks, regardless of order.
/// </summary>
public sealed class GuideDepthFrame
{
    private bool armed, cleared, disposed;
    private nint swapChain;
    private uint presentCount;
    public bool CanAcquire => armed && !disposed;
    public bool NeedsClear => CanAcquire && !cleared;

    public bool BeginFrame(nint currentSwapChain, uint lastPresentCount)
    {
        if (disposed || currentSwapChain == 0) return false;
        if (armed && swapChain == currentSwapChain && presentCount == lastPresentCount) return true;
        swapChain = currentSwapChain;
        presentCount = lastPresentCount;
        armed = true;
        cleared = false;
        return true;
    }

    /// <summary>Call only after the clear succeeds; a failed clear remains pending.</summary>
    public void MarkCleared()
    {
        if (CanAcquire) cleared = true;
    }

    /// <summary>A newly created device/size resource has no usable prior contents.</summary>
    public void ResourceChanged() => cleared = false;

    public void Dispose()
    {
        disposed = true;
        armed = cleared = false;
    }
}
