#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <shlobj.h>
#include <shobjidl.h>
#include <exdisp.h>
#include <shldisp.h>
#include <shlwapi.h>
#include <shellapi.h>
#undef ShellExecute
#include <wrl/client.h>
#include <cstdio>
#include <vector>

using Microsoft::WRL::ComPtr;

int wmain(int argumentCount, wchar_t** arguments) {
    setvbuf(stdout, nullptr, _IONBF, 0);
    const auto initialized = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(initialized)) return 2;
    if ((argumentCount == 3 && (wcscmp(arguments[1], L"--desktop-execute") == 0 || wcscmp(arguments[1], L"--window-originals") == 0)) ||
        (argumentCount == 4 && (wcscmp(arguments[1], L"--window-execute") == 0 || wcscmp(arguments[1], L"--window-group") == 0))) {
        const bool groupAction = wcscmp(arguments[1], L"--window-group") == 0;
        const bool originalsAction = wcscmp(arguments[1], L"--window-originals") == 0;
        const auto targetWindow = argumentCount == 4 || originalsAction ? static_cast<SHANDLE_PTR>(_wcstoi64(arguments[2], nullptr, 0)) : 0;
        ComPtr<IShellWindows> windows;
        auto hr = CoCreateInstance(CLSID_ShellWindows, nullptr, CLSCTX_LOCAL_SERVER, IID_PPV_ARGS(&windows));
        std::printf("ShellWindows: %08lx\n", static_cast<unsigned long>(hr));
        VARIANT location{}, empty{}; location.vt = VT_I4; location.lVal = CSIDL_DESKTOP;
        LONG desktopWindow = 0;
        ComPtr<IDispatch> desktop;
        if (SUCCEEDED(hr)) hr = windows->FindWindowSW(&location, &empty, SWC_DESKTOP, &desktopWindow, SWFO_NEEDDISPATCH, &desktop);
        std::printf("Desktop dispatch: %08lx\n", static_cast<unsigned long>(hr));
        ComPtr<IShellBrowser> browser;
        if (SUCCEEDED(hr) && desktop) hr = IUnknown_QueryService(desktop.Get(), SID_STopLevelBrowser, IID_PPV_ARGS(&browser));
        std::printf("Desktop browser: %08lx\n", static_cast<unsigned long>(hr));
        ComPtr<IShellView> view;
        if (SUCCEEDED(hr) && browser) hr = browser->QueryActiveShellView(&view);
        std::printf("Desktop view: %08lx\n", static_cast<unsigned long>(hr));
        ComPtr<IShellFolderViewDual> dual;
        if (SUCCEEDED(hr) && view) hr = view->GetItemObject(SVGIO_BACKGROUND, IID_PPV_ARGS(&dual));
        std::printf("Desktop view dual: %08lx\n", static_cast<unsigned long>(hr));
        if ((FAILED(hr) || targetWindow) && windows) {
            dual.Reset();
            LONG windowCount = 0; windows->get_Count(&windowCount);
            for (LONG index = 0; index < windowCount && index < 32 && !dual; ++index) {
                VARIANT position{}; position.vt = VT_I4; position.lVal = index;
                ComPtr<IDispatch> item;
                if (FAILED(windows->Item(position, &item)) || !item) continue;
                ComPtr<IWebBrowser2> web;
                if (FAILED(item.As(&web))) continue;
                SHANDLE_PTR handle = 0;
                if (FAILED(web->get_HWND(&handle)) || handle != targetWindow) continue;
                if (groupAction || originalsAction) {
                    ComPtr<IShellBrowser> targetBrowser;
                    hr = IUnknown_QueryService(item.Get(), SID_STopLevelBrowser, IID_PPV_ARGS(&targetBrowser));
                    ComPtr<IShellView> targetView;
                    if (SUCCEEDED(hr)) hr = targetBrowser->QueryActiveShellView(&targetView);
                    ComPtr<IFolderView2> targetFolderView;
                    if (SUCCEEDED(hr)) hr = targetView.As(&targetFolderView);
                    ComPtr<IShellFolder> targetFolder;
                    if (SUCCEEDED(hr)) hr = targetFolderView->GetFolder(IID_PPV_ARGS(&targetFolder));
                    if (originalsAction && SUCCEEDED(hr)) {
                        ComPtr<IEnumIDList> children;
                        hr = targetFolder->EnumObjects(nullptr, SHCONTF_NONFOLDERS, &children);
                        std::vector<PITEMID_CHILD> ids;
                        PITEMID_CHILD child = nullptr;
                        while (SUCCEEDED(hr) && children && children->Next(1, &child, nullptr) == S_OK && ids.size() < 20000) {
                            ids.push_back(child);
                            STRRET text{}; wchar_t path[32768]{};
                            if (SUCCEEDED(targetFolder->GetDisplayNameOf(child, SHGDN_FORPARSING, &text)) &&
                                SUCCEEDED(StrRetToBufW(&text, child, path, ARRAYSIZE(path)))) std::wprintf(L"Original parsing path: %s\n", path);
                        }
                        ComPtr<IDataObject> data;
                        if (!ids.empty()) hr = targetFolder->GetUIObjectOf(reinterpret_cast<HWND>(handle), static_cast<UINT>(ids.size()),
                            const_cast<PCUITEMID_CHILD_ARRAY>(ids.data()), IID_IDataObject, nullptr, &data);
                        FORMATETC format{CF_HDROP, nullptr, DVASPECT_CONTENT, -1, TYMED_HGLOBAL};
                        STGMEDIUM medium{};
                        if (SUCCEEDED(hr) && data) hr = data->GetData(&format, &medium);
                        if (SUCCEEDED(hr) && medium.hGlobal) {
                            const auto files = DragQueryFileW(static_cast<HDROP>(medium.hGlobal), 0xffffffff, nullptr, 0);
                            for (UINT file = 0; file < files; ++file) {
                                wchar_t path[32768]{}; DragQueryFileW(static_cast<HDROP>(medium.hGlobal), file, path, ARRAYSIZE(path));
                                std::wprintf(L"Original clipboard path: %s\n", path);
                            }
                            ReleaseStgMedium(&medium);
                        }
                        std::printf("Actual original data object: %08lx count=%zu\n", static_cast<unsigned long>(hr), ids.size());
                        for (const auto id : ids) CoTaskMemFree(id);
                        return FAILED(hr) ? 1 : 0;
                    }
                    ComPtr<IContextMenu> menu;
                    if (SUCCEEDED(hr)) hr = targetFolder->CreateViewObject(reinterpret_cast<HWND>(handle), IID_PPV_ARGS(&menu));
                    CMINVOKECOMMANDINFO action{sizeof(action)};
                    action.hwnd = reinterpret_cast<HWND>(handle);
                    action.lpVerb = MAKEINTRESOURCEA(static_cast<WORD>(_wtoi(arguments[3])));
                    if (SUCCEEDED(hr)) hr = menu->InvokeCommand(&action);
                    std::printf("Actual view grouping action: %08lx\n", static_cast<unsigned long>(hr));
                    return FAILED(hr) ? 1 : 0;
                }
                ComPtr<IDispatch> document;
                hr = web->get_Document(&document);
                if (SUCCEEDED(hr) && document) hr = document.As(&dual);
                std::printf("Own folder view dual: %08lx\n", static_cast<unsigned long>(hr));
            }
        }
        ComPtr<IDispatch> application;
        if (SUCCEEDED(hr) && dual) hr = dual->get_Application(&application);
        std::printf("Desktop application: %08lx\n", static_cast<unsigned long>(hr));
        ComPtr<IShellDispatch2> shell;
        if (SUCCEEDED(hr) && application) hr = application.As(&shell);
        std::printf("Desktop shell: %08lx\n", static_cast<unsigned long>(hr));
        VARIANT args{}, directory{}, operation{}, show{};
        args.vt = VT_BSTR; args.bstrVal = SysAllocString(arguments[argumentCount == 4 ? 3 : 2]);
        operation.vt = VT_BSTR; operation.bstrVal = SysAllocString(L"open");
        show.vt = VT_I4; show.lVal = SW_HIDE;
        BSTR executable = SysAllocString(L"C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe");
        if (SUCCEEDED(hr) && shell) hr = shell->ShellExecute(executable, args, directory, operation, show);
        std::printf("Desktop Explorer ShellExecute: %08lx hwnd=%ld\n", static_cast<unsigned long>(hr), desktopWindow);
        SysFreeString(executable); VariantClear(&args); VariantClear(&operation);
        return FAILED(hr) || !shell ? 1 : 0;
    }
    WNDCLASSW windowClass{};
    windowClass.lpfnWndProc = DefWindowProcW;
    windowClass.hInstance = GetModuleHandleW(nullptr);
    windowClass.lpszClassName = L"LopataCollectionDiagnosticHost";
    RegisterClassW(&windowClass);
    HWND window = CreateWindowExW(0, windowClass.lpszClassName, L"ЛОПАТА — проверка представления коллекции",
        WS_OVERLAPPEDWINDOW, CW_USEDEFAULT, CW_USEDEFAULT, 1200, 800, nullptr, nullptr, windowClass.hInstance, nullptr);
    if (!window) { CoUninitialize(); return 3; }
    ComPtr<IExplorerBrowser> browser;
    auto hr = CoCreateInstance(CLSID_ExplorerBrowser, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&browser));
    std::printf("ExplorerBrowser creation: %08lx\n", static_cast<unsigned long>(hr));
    RECT area{}; GetClientRect(window, &area);
    FOLDERSETTINGS settings{FVM_ICON, FWF_AUTOARRANGE};
    if (SUCCEEDED(hr)) hr = browser->Initialize(window, &area, &settings);
    std::printf("ExplorerBrowser initialization: %08lx\n", static_cast<unsigned long>(hr));
    if (SUCCEEDED(hr)) hr = browser->SetOptions(EBO_SHOWFRAMES | EBO_ALWAYSNAVIGATE);
    PIDLIST_ABSOLUTE root = nullptr;
    if (SUCCEEDED(hr)) hr = SHParseDisplayName(L"::{D4ACCFB2-2A17-4F52-BB28-8CF2A584362A}", nullptr, &root, 0, nullptr);
    if (SUCCEEDED(hr)) hr = browser->BrowseToIDList(root, SBSP_ABSOLUTE);
    std::printf("ExplorerBrowser navigation: %08lx\n", static_cast<unsigned long>(hr));
    ShowWindow(window, SW_SHOWNORMAL);
    const auto until = GetTickCount64() + 8000;
    MSG message{};
    while (GetTickCount64() < until && IsWindow(window)) {
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
            TranslateMessage(&message); DispatchMessageW(&message);
        }
        MsgWaitForMultipleObjects(0, nullptr, FALSE, 100, QS_ALLINPUT);
    }
    ComPtr<IFolderView2> view;
    const auto obtained = browser ? browser->GetCurrentView(IID_PPV_ARGS(&view)) : E_UNEXPECTED;
    std::printf("ExplorerBrowser current view: %08lx\n", static_cast<unsigned long>(obtained));
    int count = 0;
    const auto counted = view ? view->ItemCount(SVGIO_ALLVIEW, &count) : E_UNEXPECTED;
    std::printf("ExplorerBrowser items: HRESULT=%08lx count=%d\n", static_cast<unsigned long>(counted), count);
    view.Reset();
    if (browser) browser->Destroy();
    browser.Reset();
    CoTaskMemFree(root);
    if (IsWindow(window)) DestroyWindow(window);
    CoUninitialize();
    return FAILED(hr) || FAILED(obtained) ? 1 : 0;
}
