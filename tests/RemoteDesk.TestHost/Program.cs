using RemoteDesk.TestHost;

Console.WriteLine("RemoteDesk Headless Test Host running...");

using var capturer = new FakeScreenCapturer(1920, 1080);
var injector = new FakeInputInjector();

var frameCount = 0;
capturer.FrameCaptured += frame =>
{
    Interlocked.Increment(ref frameCount);
    frame.Dispose();
};

capturer.Start(targetFps: 30);
Console.WriteLine("Capturing fake video frames for 2 seconds...");
await Task.Delay(2000);
capturer.Stop();

Console.WriteLine($"Captured {frameCount} frames successfully.");
Console.WriteLine("TestHost demonstration complete.");
