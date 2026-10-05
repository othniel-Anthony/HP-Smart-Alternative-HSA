namespace PrintHub.Core.Http;

/// <summary>A stream whose read timeout can be changed while it is open (the USB pipe waits long for a reply, but only briefly when a reply has no length).</summary>
public interface IReadTimeoutStream
{
    void SetReadTimeout(int milliseconds);
}
