using FlyPhotos.Display.Video;

namespace FlyPhotos.Tests;

[TestClass]
public sealed class VideoTimeFormatTests
{
    [TestMethod]
    public void Zero_is_0_00() => Assert.AreEqual("0:00", VideoTimeFormat.Format(0));

    [TestMethod]
    public void Fractions_round_down_to_whole_seconds() => Assert.AreEqual("1:24", VideoTimeFormat.Format(84.4));

    [TestMethod]
    public void Minutes_and_seconds() => Assert.AreEqual("5:10", VideoTimeFormat.Format(310));

    [TestMethod]
    public void Hours_from_one_hour() => Assert.AreEqual("1:02:05", VideoTimeFormat.Format(3725));

    [TestMethod]
    public void NaN_is_0_00() => Assert.AreEqual("0:00", VideoTimeFormat.Format(double.NaN));
}
