namespace AIHub.Services;

public static class ApplicationClosePolicy
{
    public static bool ShouldAsk(bool askBeforeClosing, bool fullExitRequested,
        bool sessionEnding, bool installingUpdate, bool shutdownInProgress) =>
        askBeforeClosing && !fullExitRequested && !sessionEnding && !installingUpdate && !shutdownInProgress;

    public static bool ShouldHide(bool closeToTray, bool trayAvailable, bool fullExitRequested,
        bool sessionEnding, bool installingUpdate, bool shutdownInProgress) =>
        closeToTray && trayAvailable && !fullExitRequested && !sessionEnding && !installingUpdate && !shutdownInProgress;
}
