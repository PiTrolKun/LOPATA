using AIHub.Controls;

namespace AIHub;

public partial class MainWindow
{
    private FileMetadataWindow? _metadataWindow;
    private void OpenMetadataUtility()
    {
        if(_metadataWindow is not null){_metadataWindow.Show();_metadataWindow.Activate();return;}
        _metadataWindow=new(this,L);
        _metadataWindow.Closed+=(_,_)=>_metadataWindow=null;
        _metadataWindow.Show();
    }
}
