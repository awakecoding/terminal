#include <windows.h>
#include <shobjidl.h>
#include <propsys.h>
#include <propkey.h>
#include <propvarutil.h>
#include <winrt/Windows.Data.Xml.Dom.h>
#include <winrt/Windows.UI.Notifications.h>

#include <algorithm>
#include <cctype>
#include <iostream>
#include <map>
#include <stdexcept>
#include <sstream>
#include <string>
#include <vector>

using winrt::Windows::Data::Xml::Dom::XmlDocument;
using winrt::Windows::UI::Notifications::ToastNotification;
using winrt::Windows::UI::Notifications::ToastNotificationManager;

static constexpr int protocol_version = 1;
static constexpr size_t maximum_request_bytes = 256 * 1024;
static constexpr size_t maximum_profiles = 64;

struct request
{
    int protocol{};
    std::string authentication;
    std::string operation;
    std::map<std::string, std::string> values;
    std::vector<std::string> profiles;
};

static std::string base64url_encode(const std::string& value)
{
    static constexpr char alphabet[] =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
    std::string result;
    result.reserve((value.size() * 4 + 2) / 3);
    unsigned accumulator{};
    int bits{};
    for (const unsigned char byte : value)
    {
        accumulator = (accumulator << 8) | byte;
        bits += 8;
        while (bits >= 6)
        {
            bits -= 6;
            result.push_back(alphabet[(accumulator >> bits) & 0x3f]);
        }
    }
    if (bits)
    {
        result.push_back(alphabet[(accumulator << (6 - bits)) & 0x3f]);
    }
    return result;
}

static bool base64url_decode(std::string_view value, std::string& result)
{
    result.clear();
    unsigned accumulator{};
    int bits{};
    for (const unsigned char character : value)
    {
        int decoded = -1;
        if (character >= 'A' && character <= 'Z') decoded = character - 'A';
        else if (character >= 'a' && character <= 'z') decoded = character - 'a' + 26;
        else if (character >= '0' && character <= '9') decoded = character - '0' + 52;
        else if (character == '-') decoded = 62;
        else if (character == '_') decoded = 63;
        if (decoded < 0)
        {
            return false;
        }
        accumulator = (accumulator << 6) | static_cast<unsigned>(decoded);
        bits += 6;
        if (bits >= 8)
        {
            bits -= 8;
            result.push_back(static_cast<char>((accumulator >> bits) & 0xff));
        }
    }
    return bits < 6;
}

static std::wstring widen(std::string_view utf8)
{
    if (utf8.empty())
    {
        return {};
    }
    const auto length = MultiByteToWideChar(
        CP_UTF8, MB_ERR_INVALID_CHARS, utf8.data(), static_cast<int>(utf8.size()), nullptr, 0);
    if (!length)
    {
        throw std::runtime_error("A request value is not valid UTF-8.");
    }
    std::wstring result(length, L'\0');
    if (!MultiByteToWideChar(
            CP_UTF8, MB_ERR_INVALID_CHARS, utf8.data(), static_cast<int>(utf8.size()), result.data(), length))
    {
        throw std::runtime_error("A request value is not valid UTF-8.");
    }
    return result;
}

static std::string narrow(std::wstring_view value)
{
    if (value.empty())
    {
        return {};
    }
    const auto length = WideCharToMultiByte(
        CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    if (!length)
    {
        return "Windows error";
    }
    std::string result(length, '\0');
    WideCharToMultiByte(
        CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), result.data(), length, nullptr, nullptr);
    return result;
}

static std::string error_text(HRESULT error)
{
    PWSTR message{};
    const auto length = FormatMessageW(
        FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS,
        nullptr,
        static_cast<DWORD>(error),
        0,
        reinterpret_cast<PWSTR>(&message),
        0,
        nullptr);
    std::string result = length ? narrow(std::wstring_view(message, length)) : "Unknown Windows error";
    if (message)
    {
        LocalFree(message);
    }
    while (!result.empty() && std::isspace(static_cast<unsigned char>(result.back())))
    {
        result.pop_back();
    }
    std::ostringstream stream;
    stream << result << " (0x" << std::hex << std::uppercase << static_cast<unsigned>(error) << ')';
    return stream.str();
}

static void respond(
    std::string_view status,
    std::string_view diagnostic,
    std::initializer_list<std::string_view> capabilities = {})
{
    std::cout << "protocol=1\nstatus=" << status << "\ndiagnostic="
              << base64url_encode(std::string(diagnostic)) << '\n';
    for (const auto capability : capabilities)
    {
        std::cout << "capability=" << capability << '\n';
    }
    std::cout << "end=1\n";
}

static bool constant_time_equal(std::string_view left, std::string_view right)
{
    unsigned difference = static_cast<unsigned>(left.size() ^ right.size());
    const auto count = (std::max)(left.size(), right.size());
    for (size_t index = 0; index < count; ++index)
    {
        const auto l = index < left.size() ? left[index] : 0;
        const auto r = index < right.size() ? right[index] : 0;
        difference |= static_cast<unsigned char>(l ^ r);
    }
    return difference == 0;
}

static bool read_request(request& result, std::string& diagnostic)
{
    std::string line;
    size_t total{};
    bool ended{};
    while (std::getline(std::cin, line))
    {
        total += line.size() + 1;
        if (total > maximum_request_bytes)
        {
            diagnostic = "The helper request exceeds 256 KiB.";
            return false;
        }
        if (!line.empty() && line.back() == '\r')
        {
            line.pop_back();
        }
        const auto separator = line.find('=');
        if (separator == std::string::npos)
        {
            diagnostic = "The helper request contains an invalid line.";
            return false;
        }
        const auto key = line.substr(0, separator);
        const auto value = line.substr(separator + 1);
        if (key == "protocol")
        {
            try { result.protocol = std::stoi(value); }
            catch (...) { diagnostic = "The helper protocol version is invalid."; return false; }
        }
        else if (key == "auth") result.authentication = value;
        else if (key == "operation") result.operation = value;
        else if (key == "profile")
        {
            if (result.profiles.size() >= maximum_profiles)
            {
                diagnostic = "The helper request contains more than 64 profiles.";
                return false;
            }
            result.profiles.push_back(value);
        }
        else if (key == "end")
        {
            ended = value == "1";
            break;
        }
        else if (!result.values.emplace(key, value).second)
        {
            diagnostic = "The helper request contains a duplicate field.";
            return false;
        }
    }
    if (!ended || result.protocol == 0 || result.authentication.empty() || result.operation.empty())
    {
        diagnostic = "The helper request is incomplete.";
        return false;
    }
    return true;
}

static bool decoded_value(const request& source, const char* key, std::wstring& result)
{
    const auto iterator = source.values.find(key);
    if (iterator == source.values.end())
    {
        return false;
    }
    std::string utf8;
    if (!base64url_decode(iterator->second, utf8))
    {
        throw std::runtime_error(std::string("The '") + key + "' value is not valid base64url.");
    }
    result = widen(utf8);
    return true;
}

static HRESULT set_string_property(IPropertyStore* store, REFPROPERTYKEY key, std::wstring_view value)
{
    PROPVARIANT property{};
    auto hr = InitPropVariantFromString(std::wstring(value).c_str(), &property);
    if (SUCCEEDED(hr))
    {
        hr = store->SetValue(key, property);
    }
    PropVariantClear(&property);
    return hr;
}

static HRESULT update_jump_list(const request& source)
{
    std::wstring app_id;
    std::wstring executable;
    if (!decoded_value(source, "aumid", app_id) || app_id.empty() ||
        !decoded_value(source, "executable", executable) || executable.empty())
    {
        return E_INVALIDARG;
    }

    ICustomDestinationList* destination{};
    auto hr = CoCreateInstance(CLSID_DestinationList, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&destination));
    if (FAILED(hr)) return hr;
    hr = destination->SetAppID(app_id.c_str());

    UINT slots{};
    IObjectArray* removed{};
    if (SUCCEEDED(hr)) hr = destination->BeginList(&slots, IID_PPV_ARGS(&removed));
    if (removed) removed->Release();

    IObjectCollection* tasks{};
    if (SUCCEEDED(hr))
    {
        hr = CoCreateInstance(
            CLSID_EnumerableObjectCollection, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&tasks));
    }

    size_t added{};
    for (const auto& profile : source.profiles)
    {
        if (FAILED(hr) || added >= slots)
        {
            break;
        }
        const auto first = profile.find('|');
        const auto second = first == std::string::npos ? first : profile.find('|', first + 1);
        if (first == std::string::npos || second == std::string::npos)
        {
            hr = E_INVALIDARG;
            break;
        }
        std::string title_utf8;
        std::string guid_utf8;
        std::string icon_utf8;
        if (!base64url_decode(std::string_view(profile).substr(0, first), title_utf8) ||
            !base64url_decode(std::string_view(profile).substr(first + 1, second - first - 1), guid_utf8) ||
            !base64url_decode(std::string_view(profile).substr(second + 1), icon_utf8))
        {
            hr = E_INVALIDARG;
            break;
        }
        const auto title = widen(title_utf8);
        const auto guid = widen(guid_utf8);
        const auto icon = widen(icon_utf8);

        IShellLinkW* link{};
        hr = CoCreateInstance(CLSID_ShellLink, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&link));
        if (FAILED(hr)) break;
        hr = link->SetPath(executable.c_str());
        const auto arguments = L"-p \"" + guid + L"\"";
        if (SUCCEEDED(hr)) hr = link->SetArguments(arguments.c_str());
        if (SUCCEEDED(hr) && !icon.empty() && GetFileAttributesW(icon.c_str()) != INVALID_FILE_ATTRIBUTES)
        {
            hr = link->SetIconLocation(icon.c_str(), 0);
        }
        else if (SUCCEEDED(hr))
        {
            hr = link->SetIconLocation(executable.c_str(), 0);
        }
        IPropertyStore* properties{};
        if (SUCCEEDED(hr)) hr = link->QueryInterface(IID_PPV_ARGS(&properties));
        if (SUCCEEDED(hr)) hr = set_string_property(properties, PKEY_Title, title);
        if (SUCCEEDED(hr)) hr = set_string_property(properties, PKEY_AppUserModel_ID, app_id);
        if (SUCCEEDED(hr)) hr = properties->Commit();
        if (properties) properties->Release();
        if (SUCCEEDED(hr)) hr = tasks->AddObject(link);
        link->Release();
        if (SUCCEEDED(hr)) ++added;
    }

    if (SUCCEEDED(hr)) hr = destination->AddUserTasks(tasks);
    if (SUCCEEDED(hr)) hr = destination->CommitList();
    else destination->AbortList();
    if (tasks) tasks->Release();
    destination->Release();
    return hr;
}

static std::wstring xml_escape(std::wstring_view value)
{
    std::wstring result;
    for (const auto character : value)
    {
        switch (character)
        {
        case L'&': result += L"&amp;"; break;
        case L'<': result += L"&lt;"; break;
        case L'>': result += L"&gt;"; break;
        case L'"': result += L"&quot;"; break;
        case L'\'': result += L"&apos;"; break;
        default:
            if (character < 0x20 && character != L'\t' && character != L'\r' && character != L'\n')
            {
                throw std::runtime_error("Toast text contains an invalid XML character.");
            }
            result.push_back(character);
        }
    }
    return result;
}

static HRESULT publish_toast(const request& source)
{
    std::wstring app_id;
    std::wstring title;
    std::wstring body;
    std::wstring activation;
    std::wstring tag;
    if (!decoded_value(source, "aumid", app_id) || app_id.empty() ||
        !decoded_value(source, "title", title) || title.empty() ||
        !decoded_value(source, "body", body) ||
        !decoded_value(source, "activation", activation) || activation.empty() ||
        !decoded_value(source, "tag", tag) || tag.empty())
    {
        return E_INVALIDARG;
    }

    try
    {
        XmlDocument document;
        const auto xml =
            L"<toast launch=\"--toast-activation " + xml_escape(activation) +
            L"\"><visual><binding template=\"ToastGeneric\"><text>" + xml_escape(title) +
            L"</text><text>" + xml_escape(body) +
            L"</text></binding></visual></toast>";
        document.LoadXml(winrt::hstring{ xml });
        ToastNotification toast{ document };
        toast.Tag(winrt::hstring{ tag.substr(0, (std::min)(tag.size(), size_t{ 16 })) });
        toast.Group(L"WindowsTerminal.NET");
        ToastNotificationManager::CreateToastNotifier(winrt::hstring{ app_id }).Show(toast);
        return S_OK;
    }
    catch (const winrt::hresult_error& error)
    {
        return error.code();
    }
}

int wmain()
{
    request source;
    std::string diagnostic;
    if (!read_request(source, diagnostic))
    {
        respond("invalid", diagnostic);
        return 2;
    }
    if (source.protocol != protocol_version)
    {
        respond("version-mismatch", "Only shell helper protocol version 1 is supported.");
        return 3;
    }

    wchar_t authentication[129]{};
    const auto auth_length = GetEnvironmentVariableW(
        L"WT_SHELL_HELPER_AUTH_TOKEN", authentication, static_cast<DWORD>(std::size(authentication)));
    const auto expected = auth_length && auth_length < static_cast<DWORD>(std::size(authentication))
        ? narrow(std::wstring_view(authentication, auth_length))
        : std::string{};
    SecureZeroMemory(authentication, sizeof(authentication));
    if (expected.empty() || !constant_time_equal(source.authentication, expected))
    {
        respond("unauthorized", "Shell helper authentication failed.");
        return 4;
    }

    if (source.operation == "capabilities")
    {
        respond(
            "success",
            "Shell helper protocol 1 is available. Default-terminal delegation is not available.",
            { "explorer-command.v1", "jump-list.v1", "toast.v1" });
        return 0;
    }
    if (source.operation == "default-terminal")
    {
        respond(
            "unsupported",
            "Default-terminal delegation requires the OpenConsole handoff v3 proxy/stub and host; they are not bundled.");
        return 5;
    }

    HRESULT result = E_INVALIDARG;
    try
    {
        winrt::init_apartment(winrt::apartment_type::multi_threaded);
        if (source.operation == "jump-list") result = update_jump_list(source);
        else if (source.operation == "toast") result = publish_toast(source);
        else
        {
            respond("invalid", "The requested shell helper operation is unknown.");
            return 2;
        }
    }
    catch (const winrt::hresult_error& error)
    {
        respond("failed", error_text(error.code()));
        return 1;
    }
    catch (const std::exception& error)
    {
        respond("invalid", error.what());
        return 2;
    }

    if (SUCCEEDED(result))
    {
        respond("success", source.operation == "jump-list"
            ? "The profile jump list was refreshed."
            : "The system toast was published.");
        return 0;
    }
    respond("failed", error_text(result));
    return 1;
}
