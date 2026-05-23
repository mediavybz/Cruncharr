using Cruncharr.Core.Utils.DRM;
using Xunit;

namespace Cruncharr.Core.Tests;

public class DrmTests{
    [Fact]
    public void WidevineCdm_LoadsTestFiles_Successfully(){
        var testDir = @"C:\Users\Aorus\Desktop\Crunchyroll-Downloader-v1.5.42-windows-x64\widevine";
        if (!Directory.Exists(testDir)){
            return; // Skip if test files not available
        }

        var cdm = new WidevineCdm(testDir);
        
        Assert.True(cdm.CanDecrypt, "CDM should be able to decrypt with test files");
    }
    
    [Fact]
    public void WidevineCdm_MissingDirectory_CannotDecrypt(){
        var cdm = new WidevineCdm("/nonexistent/widevine/path");
        
        Assert.False(cdm.CanDecrypt);
    }
    
    [Fact]
    public void PSSHBox_ParseValidData_Works(){
        // Create a minimal PSSH box with widevine system ID
        var data = new byte[]{
            0x00, 0x00, 0x00, 0x20, // size = 32
            0x70, 0x73, 0x73, 0x68, // 'pssh'
            0x00, 0x00, 0x00, 0x00, // version/flags
            0xed, 0xef, 0x8b, 0xa9, 0x79, 0xd6, 0x4a, 0xce, 0xa3, 0xc8, 0x27, 0xdc, 0xd5, 0x1d, 0x21, 0xed, // widevine system id
            0x00, 0x00, 0x00, 0x00  // kid count = 0
        };
        
        var box = PSSHBox.FromByteArray(data);
        
        Assert.NotNull(box);
        Assert.Empty(box.KIDs);
    }
}
