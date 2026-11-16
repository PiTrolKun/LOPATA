using System.Runtime.InteropServices;

namespace Lopata.Recovery;

// The Windows SDK ABI keeps the supervisor independent of the main UI and of additional runtimes.
internal static class WindowsToast
{
    private const int FirstInspectableMethod = 6;
    private static readonly Guid XmlDocumentId = new("f7f3a506-1e87-42d6-bcfb-b8c809fa5494");
    private static readonly Guid XmlDocumentIOId = new("6cd0e74e-ee65-4489-9ebf-ca43e87ba637");
    private static readonly Guid ToastFactoryId = new("04124b20-82c6-4229-b109-fd9ed4662b53");
    private static readonly Guid ToastManagerId = new("50ac103f-d235-4598-bbef-98fe4d1a3ad4");
    private static readonly Guid ToastNotification2Id = new("9dfb9fd1-143a-490e-90bf-b9fba7132de7");

    internal static void Show(string appId, string payload, bool suppressPopup)
    {
        const uint multiThreadedApartment = 1;
        Check(RoInitialize(multiThreadedApartment));
        try
        {
            using var manager = Factory("Windows.UI.Notifications.ToastNotificationManager", ToastManagerId);
            using var identifier = new RuntimeString(appId);
            var createNotifierWithId = Method<GetObject>(manager.Pointer, FirstInspectableMethod + 1);
            Check(createNotifierWithId(manager.Pointer, identifier.Pointer, out var notifierPointer));
            using var notifier = new RuntimeObject(notifierPointer);

            using var xmlClass = new RuntimeString("Windows.Data.Xml.Dom.XmlDocument");
            Check(RoActivateInstance(xmlClass.Pointer, out var xmlPointer));
            using var xml = new RuntimeObject(xmlPointer);
            using var document = xml.Query(XmlDocumentId);
            using var documentIO = xml.Query(XmlDocumentIOId);
            using var content = new RuntimeString(payload);
            var loadXml = Method<SetObject>(documentIO.Pointer, FirstInspectableMethod);
            Check(loadXml(documentIO.Pointer, content.Pointer));

            using var factory = Factory("Windows.UI.Notifications.ToastNotification", ToastFactoryId);
            var createToast = Method<GetObject>(factory.Pointer, FirstInspectableMethod);
            Check(createToast(factory.Pointer, document.Pointer, out var toastPointer));
            using var toast = new RuntimeObject(toastPointer);
            if (suppressPopup)
            {
                using var options = toast.Query(ToastNotification2Id);
                var setSuppressPopup = Method<SetBoolean>(options.Pointer, FirstInspectableMethod + 4);
                Check(setSuppressPopup(options.Pointer, 1));
            }

            var show = Method<SetObject>(notifier.Pointer, FirstInspectableMethod);
            Check(show(notifier.Pointer, toast.Pointer));
        }
        finally { RoUninitialize(); }
    }

    private static RuntimeObject Factory(string name, Guid iid)
    {
        using var className = new RuntimeString(name);
        Check(RoGetActivationFactory(className.Pointer, in iid, out var factory));
        return new RuntimeObject(factory);
    }

    private static T Method<T>(nint instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

    private static void Check(int result) => Marshal.ThrowExceptionForHR(result);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int GetObject(nint instance, nint argument, out nint result);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int SetObject(nint instance, nint argument);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int SetBoolean(nint instance, byte value);

    private sealed class RuntimeObject(nint pointer) : IDisposable
    {
        internal nint Pointer { get; } = pointer;

        internal RuntimeObject Query(Guid iid)
        {
            Check(Marshal.QueryInterface(Pointer, in iid, out var result));
            return new RuntimeObject(result);
        }

        public void Dispose() => Marshal.Release(Pointer);
    }

    private sealed class RuntimeString : IDisposable
    {
        internal nint Pointer { get; }

        internal RuntimeString(string value)
        {
            Check(WindowsCreateString(value, (uint)value.Length, out var handle));
            Pointer = handle;
        }

        public void Dispose() => WindowsDeleteString(Pointer);
    }

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoInitialize(uint type);
    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern void RoUninitialize();
    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoActivateInstance(nint name, out nint instance);
    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(nint name, in Guid iid, out nint factory);
    [DllImport("combase.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string source, uint length, out nint handle);
    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(nint handle);
}
