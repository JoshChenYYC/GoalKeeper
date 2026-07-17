namespace GoalKeeper.Infrastructure;

public interface INativeCameraFactory
{
    INativeCamera Create();
}

public interface INativeCamera
{
    bool Open(int deviceIndex);

    INativeCameraFrame? Read();

    void Release();
}

public interface INativeCameraFrame : IDisposable
{
    int PixelWidth { get; }

    int PixelHeight { get; }

    byte[]? EncodeJpeg(int quality);
}
