#include <windows.h>
#include <notificationactivationcallback.h>
#include <shlobj.h>
#include <shobjidl.h>

#include <atomic>
#include <cstring>
#include <filesystem>
#include <new>
#include <string>

// Development-package CLSID. This must match Package.appxmanifest.
static constexpr CLSID CLSID_OpenTerminalHere{
    0xf4a5f6ac, 0x02b1, 0x46bd, { 0x93, 0x9d, 0x53, 0x5d, 0x39, 0x1b, 0xe1, 0x51 }
};
static constexpr CLSID CLSID_ToastActivator{
    0xa3aeb121, 0x45d9, 0x4cd9, { 0xa2, 0x78, 0x4b, 0x43, 0xd1, 0x9b, 0x95, 0xb1 }
};
static std::atomic<long> g_objects{};
extern "C" IMAGE_DOS_HEADER __ImageBase;

static HRESULT module_path(std::filesystem::path& path) noexcept
{
    const auto module = reinterpret_cast<HMODULE>(&__ImageBase);

    std::wstring buffer(32768, L'\0');
    const auto length = GetModuleFileNameW(module, buffer.data(), static_cast<DWORD>(buffer.size()));
    if (length == 0 || length >= buffer.size())
    {
        return HRESULT_FROM_WIN32(GetLastError() ? GetLastError() : ERROR_INSUFFICIENT_BUFFER);
    }
    buffer.resize(length);
    path = buffer;
    return S_OK;
}

static void append_quoted(std::wstring& command, std::wstring_view value)
{
    command.push_back(L'"');
    unsigned backslashes = 0;
    for (const auto character : value)
    {
        if (character == L'\\')
        {
            ++backslashes;
            continue;
        }

        if (character == L'"')
        {
            command.append((backslashes * 2) + 1, L'\\');
            command.push_back(L'"');
            backslashes = 0;
            continue;
        }
        command.append(backslashes, L'\\');
        backslashes = 0;
        command.push_back(character);
    }
    command.append(backslashes * 2, L'\\');
    command.push_back(L'"');
}

static HRESULT duplicate_string(std::wstring_view value, LPWSTR* result) noexcept
{
    if (!result)
    {
        return E_POINTER;
    }
    *result = static_cast<LPWSTR>(CoTaskMemAlloc((value.size() + 1) * sizeof(wchar_t)));
    if (!*result)
    {
        return E_OUTOFMEMORY;
    }
    std::memcpy(*result, value.data(), value.size() * sizeof(wchar_t));
    (*result)[value.size()] = L'\0';
    return S_OK;
}

class OpenTerminalHere final : public IExplorerCommand, public IObjectWithSite
{
public:
    OpenTerminalHere() noexcept { ++g_objects; }
    ~OpenTerminalHere()
    {
        if (_site)
        {
            _site->Release();
        }
        --g_objects;
    }

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** result) noexcept override
    {
        if (!result)
        {
            return E_POINTER;
        }
        *result = nullptr;
        if (iid == IID_IUnknown || iid == __uuidof(IExplorerCommand))
        {
            *result = static_cast<IExplorerCommand*>(this);
        }
        else if (iid == __uuidof(IObjectWithSite))
        {
            *result = static_cast<IObjectWithSite*>(this);
        }
        else
        {
            return E_NOINTERFACE;
        }
        AddRef();
        return S_OK;
    }

    ULONG STDMETHODCALLTYPE AddRef() noexcept override { return ++_references; }
    ULONG STDMETHODCALLTYPE Release() noexcept override
    {
        const auto references = --_references;
        if (!references)
        {
            delete this;
        }
        return references;
    }

    HRESULT STDMETHODCALLTYPE GetTitle(IShellItemArray*, LPWSTR* title) noexcept override
    {
        return duplicate_string(L"Open in Windows Terminal (.NET)", title);
    }
    HRESULT STDMETHODCALLTYPE GetIcon(IShellItemArray*, LPWSTR* icon) noexcept override
    {
        if (!icon)
        {
            return E_POINTER;
        }
        std::filesystem::path path;
        const auto hr = module_path(path);
        if (FAILED(hr))
        {
            return hr;
        }
        path.replace_filename(L"Devolutions.Terminal.exe");
        path += L",0";
        return duplicate_string(path.native(), icon);
    }
    HRESULT STDMETHODCALLTYPE GetToolTip(IShellItemArray*, LPWSTR* tip) noexcept override
    {
        if (!tip)
        {
            return E_POINTER;
        }
        *tip = nullptr;
        return E_NOTIMPL;
    }
    HRESULT STDMETHODCALLTYPE GetCanonicalName(GUID* guid) noexcept override
    {
        if (!guid)
        {
            return E_POINTER;
        }
        *guid = CLSID_OpenTerminalHere;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetState(IShellItemArray* items, BOOL, EXPCMDSTATE* state) noexcept override
    {
        if (!state)
        {
            return E_POINTER;
        }
        IShellItem* item{};
        const auto hr = best_location(items, &item);
        if (FAILED(hr) || !item)
        {
            *state = ECS_HIDDEN;
            return SUCCEEDED(hr) ? S_OK : hr;
        }
        SFGAOF attributes{};
        const auto fileSystem = item->GetAttributes(SFGAO_FILESYSTEM, &attributes) == S_OK;
        const auto compressed = item->GetAttributes(SFGAO_FOLDER | SFGAO_STREAM, &attributes) == S_OK;
        item->Release();
        *state = fileSystem && !compressed ? ECS_ENABLED : ECS_HIDDEN;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE Invoke(IShellItemArray* items, IBindCtx*) noexcept override
    {
        IShellItem* item{};
        auto hr = best_location(items, &item);
        if (FAILED(hr) || !item)
        {
            return FAILED(hr) ? hr : S_FALSE;
        }
        PWSTR location{};
        hr = item->GetDisplayName(SIGDN_FILESYSPATH, &location);
        item->Release();
        if (FAILED(hr))
        {
            return hr;
        }

        std::filesystem::path executable;
        hr = module_path(executable);
        if (SUCCEEDED(hr))
        {
            executable.replace_filename(L"Devolutions.Terminal.exe");
            std::wstring command;
            append_quoted(command, executable.native());
            command += L" -d ";
            append_quoted(command, location);
            STARTUPINFOW startup{ sizeof(startup) };
            PROCESS_INFORMATION process{};
            if (!CreateProcessW(
                    executable.c_str(),
                    command.data(),
                    nullptr,
                    nullptr,
                    FALSE,
                    CREATE_UNICODE_ENVIRONMENT,
                    nullptr,
                    location,
                    &startup,
                    &process))
            {
                hr = HRESULT_FROM_WIN32(GetLastError());
            }
            else
            {
                CloseHandle(process.hThread);
                CloseHandle(process.hProcess);
            }
        }
        CoTaskMemFree(location);
        return hr;
    }
    HRESULT STDMETHODCALLTYPE GetFlags(EXPCMDFLAGS* flags) noexcept override
    {
        if (!flags)
        {
            return E_POINTER;
        }
        *flags = ECF_DEFAULT;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE EnumSubCommands(IEnumExplorerCommand** commands) noexcept override
    {
        if (!commands)
        {
            return E_POINTER;
        }
        *commands = nullptr;
        return E_NOTIMPL;
    }

    HRESULT STDMETHODCALLTYPE SetSite(IUnknown* site) noexcept override
    {
        if (site)
        {
            site->AddRef();
        }
        if (_site)
        {
            _site->Release();
        }
        _site = site;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetSite(REFIID iid, void** site) noexcept override
    {
        return _site ? _site->QueryInterface(iid, site) : E_FAIL;
    }

private:
    HRESULT location_from_site(IShellItem** location) noexcept
    {
        if (!location)
        {
            return E_POINTER;
        }
        *location = nullptr;
        if (!_site)
        {
            return S_FALSE;
        }
        IServiceProvider* provider{};
        auto hr = _site->QueryInterface(IID_PPV_ARGS(&provider));
        if (FAILED(hr))
        {
            return hr;
        }
        IFolderView* view{};
        hr = provider->QueryService(SID_SFolderView, IID_PPV_ARGS(&view));
        provider->Release();
        if (SUCCEEDED(hr))
        {
            hr = view->GetFolder(IID_PPV_ARGS(location));
            view->Release();
        }
        return hr;
    }

    HRESULT best_location(IShellItemArray* items, IShellItem** location) noexcept
    {
        if (!location)
        {
            return E_POINTER;
        }
        *location = nullptr;
        if (items)
        {
            DWORD count{};
            auto hr = items->GetCount(&count);
            if (FAILED(hr))
            {
                return hr;
            }
            if (count)
            {
                hr = items->GetItemAt(0, location);
                if (FAILED(hr) || *location)
                {
                    return hr;
                }
            }
        }
        return location_from_site(location);
    }

    std::atomic<ULONG> _references{ 1 };
    IUnknown* _site{};
};

class ToastActivator final : public INotificationActivationCallback
{
public:
    ToastActivator() noexcept { ++g_objects; }
    ~ToastActivator() { --g_objects; }

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** result) noexcept override
    {
        if (!result)
        {
            return E_POINTER;
        }
        *result = nullptr;
        if (iid != IID_IUnknown && iid != __uuidof(INotificationActivationCallback))
        {
            return E_NOINTERFACE;
        }
        *result = static_cast<INotificationActivationCallback*>(this);
        AddRef();
        return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() noexcept override { return ++_references; }
    ULONG STDMETHODCALLTYPE Release() noexcept override
    {
        const auto references = --_references;
        if (!references)
        {
            delete this;
        }
        return references;
    }

    HRESULT STDMETHODCALLTYPE Activate(
        LPCWSTR,
        LPCWSTR invokedArgs,
        const NOTIFICATION_USER_INPUT_DATA*,
        ULONG) noexcept override
    {
        if (!invokedArgs)
        {
            return E_INVALIDARG;
        }
        constexpr std::wstring_view prefix{ L"--toast-activation " };
        const std::wstring_view arguments{ invokedArgs };
        if (!arguments.starts_with(prefix) ||
            arguments.size() <= prefix.size() ||
            arguments.size() > prefix.size() + 4096)
        {
            return E_INVALIDARG;
        }
        for (const auto character : arguments.substr(prefix.size()))
        {
            if (!((character >= L'A' && character <= L'Z') ||
                  (character >= L'a' && character <= L'z') ||
                  (character >= L'0' && character <= L'9') ||
                  character == L'-' ||
                  character == L'_'))
            {
                return E_INVALIDARG;
            }
        }

        std::filesystem::path executable;
        auto hr = module_path(executable);
        if (FAILED(hr))
        {
            return hr;
        }
        executable.replace_filename(L"Devolutions.Terminal.exe");
        std::wstring command;
        append_quoted(command, executable.native());
        command.push_back(L' ');
        command.append(arguments);
        STARTUPINFOW startup{ sizeof(startup) };
        PROCESS_INFORMATION process{};
        if (!CreateProcessW(
                executable.c_str(),
                command.data(),
                nullptr,
                nullptr,
                FALSE,
                CREATE_UNICODE_ENVIRONMENT,
                nullptr,
                executable.parent_path().c_str(),
                &startup,
                &process))
        {
            return HRESULT_FROM_WIN32(GetLastError());
        }
        CloseHandle(process.hThread);
        CloseHandle(process.hProcess);
        return S_OK;
    }

private:
    std::atomic<ULONG> _references{ 1 };
};

class ClassFactory final : public IClassFactory
{
public:
    explicit ClassFactory(bool toast) noexcept : _toast{ toast } { ++g_objects; }
    ~ClassFactory() { --g_objects; }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** result) noexcept override
    {
        if (!result)
        {
            return E_POINTER;
        }
        *result = nullptr;
        if (iid != IID_IUnknown && iid != IID_IClassFactory)
        {
            return E_NOINTERFACE;
        }
        *result = static_cast<IClassFactory*>(this);
        AddRef();
        return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() noexcept override { return ++_references; }
    ULONG STDMETHODCALLTYPE Release() noexcept override
    {
        const auto references = --_references;
        if (!references)
        {
            delete this;
        }
        return references;
    }
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID iid, void** result) noexcept override
    {
        if (outer)
        {
            return CLASS_E_NOAGGREGATION;
        }
        IUnknown* instance{};
        if (_toast)
        {
            instance = static_cast<INotificationActivationCallback*>(
                new (std::nothrow) ToastActivator());
        }
        else
        {
            instance = static_cast<IExplorerCommand*>(
                new (std::nothrow) OpenTerminalHere());
        }
        if (!instance)
        {
            return E_OUTOFMEMORY;
        }
        const auto hr = instance->QueryInterface(iid, result);
        instance->Release();
        return hr;
    }
    HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) noexcept override
    {
        lock ? ++g_objects : --g_objects;
        return S_OK;
    }
private:
    std::atomic<ULONG> _references{ 1 };
    bool _toast;
};

STDAPI DllGetClassObject(
    REFCLSID clsid,
    REFIID iid,
    LPVOID* result)
{
    const auto explorer = IsEqualCLSID(clsid, CLSID_OpenTerminalHere);
    const auto toast = IsEqualCLSID(clsid, CLSID_ToastActivator);
    if (!explorer && !toast)
    {
        return CLASS_E_CLASSNOTAVAILABLE;
    }
    auto factory = new (std::nothrow) ClassFactory(toast);
    if (!factory)
    {
        return E_OUTOFMEMORY;
    }
    const auto hr = factory->QueryInterface(iid, result);
    factory->Release();
    return hr;
}

STDAPI DllCanUnloadNow()
{
    return g_objects == 0 ? S_OK : S_FALSE;
}

BOOL WINAPI DllMain(HINSTANCE, DWORD, void*) { return TRUE; }
