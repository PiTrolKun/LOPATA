using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass,DoNotParallelize]
public sealed class StartupFastAuditTests
{
    [TestMethod]
    public void QuickStartupReadsExistingPassportWithoutRescanningOrRewritingIt()
    {
        var folder=Path.Combine(Path.GetTempPath(),"lopata-passport-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        var path=Path.Combine(folder,"passport.json");var service=new ComputerPassportService(path);
        var passport=new ComputerPassport{CreatedAt=DateTimeOffset.Parse("2001-01-01T00:00:00Z"),MachineName=Environment.MachineName,LogicalProcessorCount=7,CpuName="cached CPU",RamTotalGb=42};
        var bytes=JsonSerializer.SerializeToUtf8Bytes(passport,new JsonSerializerOptions(JsonSerializerDefaults.Web));File.WriteAllBytes(path,bytes);
        try
        {
            var cached=service.ForStartup(false);
            Assert.AreEqual(passport.CreatedAt,cached.CreatedAt);Assert.AreEqual("cached CPU",cached.CpuName);Assert.AreEqual(7,cached.LogicalProcessorCount);
            CollectionAssert.AreEqual(bytes,File.ReadAllBytes(path));
            var fresh=service.ForStartup(true);Assert.IsTrue(fresh.CreatedAt>passport.CreatedAt);Assert.AreEqual(Environment.MachineName,fresh.MachineName);
            File.Delete(path);Assert.IsTrue(service.ForStartup(false).LogicalProcessorCount>0,"First run still scans hardware.");
            File.WriteAllText(path,"broken");Assert.IsTrue(service.ForStartup(false).LogicalProcessorCount>0);
        }
        finally{Directory.Delete(folder,true);}
    }

    [TestMethod]
    public void QuickGpuMetadataUsesCacheButFullAuditAndNewDeviceRefreshIt()
    {
        var folder=Path.Combine(Path.GetTempPath(),"lopata-hardware-cache-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        var path=Path.Combine(folder,"gpu.json");var calls=0;
        IReadOnlyList<int> Probe(){calls++;return [8];}
        var gpus=new[]{new GpuPassport{Name="NVIDIA fixture"}};
        try
        {
            HardwareInventoryCache.ComputeMajors(gpus,false,Probe,path);Assert.AreEqual(1,calls);
            HardwareInventoryCache.ComputeMajors(gpus,false,Probe,path);Assert.AreEqual(1,calls);
            HardwareInventoryCache.ComputeMajors(gpus,true,Probe,path);Assert.AreEqual(2,calls);
            HardwareInventoryCache.ComputeMajors([new GpuPassport{Name="changed"}],false,Probe,path);Assert.AreEqual(3,calls);
            File.WriteAllText(path,"broken");HardwareInventoryCache.ComputeMajors(gpus,false,Probe,path);Assert.AreEqual(4,calls);
        }
        finally{Directory.Delete(folder,true);}
    }
}
