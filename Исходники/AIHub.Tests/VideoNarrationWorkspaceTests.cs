using AIHub.Services.VideoEditor;

namespace AIHub.Tests;

[TestClass]
public sealed class VideoNarrationWorkspaceTests
{
    [TestMethod]
    public async Task PacketWorkspaceFlowsAcrossAwaitAndRestoresNestedOperationScopes()
    {
        Assert.IsNull(VideoNarrationWorkspace.Directory);
        using (VideoNarrationWorkspace.Capture("first"))
        {
            await Task.Yield(); Assert.AreEqual("first", VideoNarrationWorkspace.Directory);
            using (VideoNarrationWorkspace.Capture("second"))
            {
                Assert.AreEqual("second", await Task.Run(() => VideoNarrationWorkspace.Directory));
            }
            Assert.AreEqual("first", VideoNarrationWorkspace.Directory);
        }
        Assert.IsNull(VideoNarrationWorkspace.Directory);
    }
}
