using ZamaRec.Core.Recording;

namespace ZamaRec.Tests;

public class GpuVendorTests
{
    [Theory]
    [InlineData("auto", "amd", "amd")]
    [InlineData("", "intel", "intel")]
    [InlineData("nvidia", "amd", "amd")]              // the old default on a machine without an NVIDIA card
    [InlineData("amd", "nvidia,amd", "amd")]          // a choice the machine can honour stands
    [InlineData("auto", "nvidia,intel", "nvidia")]
    [InlineData("auto", "", "nvidia")]
    [InlineData("intel", "", "intel")]                // nothing detected: the config is taken at its word
    public void ResolvesConfiguredAgainstDetected(string configured, string detected, string expected) =>
        Assert.Equal(expected, GpuVendor.Resolve(configured, detected.Split(',', StringSplitOptions.RemoveEmptyEntries)));

    [Fact]
    public void VendorIdsBecomeVendorsDiscreteFirst()
    {
        Assert.Equal(["nvidia", "intel"], GpuVendor.FromIds(["0x8086", "0x10de"]));
        Assert.Equal(["amd"], GpuVendor.FromIds(["1002", "1002"]));
        Assert.Empty(GpuVendor.FromIds(["0x1234"]));   // a virtual adapter
        Assert.Equal("1002", GpuVendor.FromName("AMD Radeon RX 6700 XT Advanced Micro Devices, Inc."));
        Assert.Equal("10de", GpuVendor.FromName("NVIDIA GeForce RTX 3060 Ti "));
        Assert.Equal("8086", GpuVendor.FromName("Intel(R) UHD Graphics 770 Intel Corporation"));
        Assert.Null(GpuVendor.FromName("Microsoft Basic Display Adapter Microsoft"));
    }
}
