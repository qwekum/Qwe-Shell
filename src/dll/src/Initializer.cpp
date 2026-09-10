
#include <pch.h>
#include "Include\Hooker.h"
#include "Include/ContextMenu.h"
#include "RegistryConfig.h"

#include <limits>

namespace
{
	bool ReadFileWriteTime(const wchar_t *path, uint64_t &value)
	{
		value = 0;
		if(!path || !path[0])
			return false;

		HANDLE handle = ::CreateFileW(path, GENERIC_READ,
			FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
			OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
		if(handle == INVALID_HANDLE_VALUE)
		{
			const auto error = ::GetLastError();
			return error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND;
		}

		FILETIME writeTime{};
		const bool result = ::GetFileTime(handle, nullptr, nullptr, &writeTime) == TRUE;
		::CloseHandle(handle);
		if(result)
		{
			ULARGE_INTEGER integer{};
			integer.LowPart = writeTime.dwLowDateTime;
			integer.HighPart = writeTime.dwHighDateTime;
			value = integer.QuadPart;
		}
		return result;
	}

	bool ReadStudioGeneration(const wchar_t *path, std::wstring &value)
	{
		value.clear();
		if(!path || !path[0])
			return false;

		const auto attributes = ::GetFileAttributesW(path);
		if(attributes == INVALID_FILE_ATTRIBUTES)
		{
			const auto error = ::GetLastError();
			return error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND;
		}
		if((attributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
			return false;

		HANDLE handle = ::CreateFileW(path, GENERIC_READ,
			FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
			OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
		if(handle == INVALID_HANDLE_VALUE)
			return false;

		LARGE_INTEGER size{};
		bool result = ::GetFileSizeEx(handle, &size) == TRUE &&
			size.QuadPart > 0 && size.QuadPart <= 128;
		std::string bytes;
		if(result)
		{
			bytes.resize(static_cast<size_t>(size.QuadPart));
			DWORD read = 0;
			result = ::ReadFile(handle, bytes.data(), static_cast<DWORD>(bytes.size()),
				&read, nullptr) == TRUE && read == bytes.size();
		}
		::CloseHandle(handle);
		if(!result)
			return false;

		int length = ::MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS,
			bytes.data(), static_cast<int>(bytes.size()), nullptr, 0);
		if(length <= 0)
			return false;
		value.resize(static_cast<size_t>(length));
		if(::MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, bytes.data(),
			static_cast<int>(bytes.size()), value.data(), length) != length)
			return false;

		while(!value.empty() && (value.back() == L' ' || value.back() == L'\t' ||
			value.back() == L'\r' || value.back() == L'\n'))
			value.pop_back();
		while(!value.empty() && (value.front() == L' ' || value.front() == L'\t' ||
			value.front() == L'\r' || value.front() == L'\n'))
			value.erase(value.begin());

		if(value.size() != 32)
			return false;
		for(const auto character : value)
		{
			if(!((character >= L'0' && character <= L'9') ||
				(character >= L'a' && character <= L'f') ||
				(character >= L'A' && character <= L'F')))
				return false;
		}
		return true;
	}
}

namespace Nilesoft
{
	namespace Shell
	{
		const Windows::Version *os = &Windows::Version::Instance();

		bool Initializer::init(HINSTANCE hInstance)
		{
			HInstance = hInstance;
			instance = this;

			try
			{
				com_initializer.initialize();

				application.Name = APP_NAME;
				application.FileName = APP_FILENAME_TITLE;
				application.Version = APP_VERSION;

				application.Path = Path::Module(hInstance).move();

				application.Root = Path::GetRoot(application.Path);
				application.Dirctory = Path::Parent(application.Path).move();

				// Nilesoft Shell Script (NSS)
				// Nilesoft Shell Object (NSO)
				application.ConfigPortable = Path::Join(application.Dirctory, L"\\shell.nss").move();
				application.Config = application.ConfigPortable;

				auto len = application.Path.length();
				if(len > 4)
				{
					string path = application.Path.substr(0, len - 3).move();
					application.Manager = (path + L"exe").move();
				}

				auto hwnd_foreground = ::GetForegroundWindow();
				if(os->IsWindowsVersionOrGreater(10, 0, 14393))
					dpi = DLL::User32<uint32_t>("GetDpiForWindow", hwnd_foreground);
				else
					dpi = DC(hwnd_foreground).GetDeviceCapsY();

				//dpi = ::GetDpiForWindow(::GetForegroundWindow());//win10 version 1607 //10.0.14393
				is_elevated = Security::Elevation::IsElevated();
				//WIC::init();
			}
			catch(...)
			{
#ifdef _DEBUG
				Logger::Exception(__func__);
#endif
				return false;
			}
			return true;
		}

		Initializer::~Initializer()
		{
			try
			{
				//hooker.destroy();
				this->uninit();
				// Process teardown is the final opportunity to release caches that
				// were kept alive for a context menu during a hot reload.
				for(auto retired : _retired_caches)
					delete retired;
				_retired_caches.clear();
				WIC::release();
				D2D::destroy_Factory();
			}
			catch(...)
			{
#ifdef _DEBUG
				Logger::Exception(__func__);
#endif
			}
		}

		bool Initializer::init()
		{
			string current_directory;
			bool directory_changed = false;
			try
			{
				// Resolve the same registry/portable selection that Parser uses before
				// checking the transaction marker. Parser normally fills Config in its
				// constructor, which is too late to protect the first load.
				string configured;
				if(RegistryConfig::get(nullptr, L"config", configured) && !configured.empty() &&
					Path::IsFileExists(configured))
					application.Config = configured;
				else if(application.Config.empty() || !Path::IsFileExists(application.Config))
					application.Config = application.ConfigPortable;

				const string config_path = application.Config;
				const string transaction_marker = config_path + L".studio-transaction.json";
				const string generation_marker = config_path + L".studio-generation";
				// A transaction marker is published before any staged file replaces
				// occur.  Keep the last good cache visible until the marker is gone;
				// on startup, fail closed until Studio recovery completes.
				if(!config_path.empty() &&
					Path::IsFileExists(transaction_marker))
				{
					Status.Refresh = true;
					Status.Error = cache == nullptr;
					Status.Loaded = cache != nullptr;
					return cache != nullptr;
				}

				uint64_t write_before = 0;
				std::wstring generation_before;
				if(!ReadFileWriteTime(config_path.c_str(), write_before) ||
					!ReadStudioGeneration(generation_marker.c_str(), generation_before))
				{
					Status.Refresh = true;
					Status.Error = cache == nullptr;
					Status.Loaded = cache != nullptr;
					return cache != nullptr;
				}

				if(cache && !Status.Refresh)
				{
					Status.Loaded = true;
					return true;
				}

				std::unique_ptr<CACHE> candidate(new CACHE);

				// Determine the required bitmap size.
				candidate->glyph.name = FontCache::Default;

				current_directory = Path::CurrentDirectory();
				Path::CurrentDirectory(application.Dirctory);
				directory_changed = true;
				Parser parser(candidate.get());

				load_mui();

				candidate->Packages.load();

				if(!parser.Load())
				{
					Path::CurrentDirectory(current_directory);
					directory_changed = false;
					// Keep a complete previous cache serving Explorer.  The failed
					// candidate is discarded and a later explicit refresh can retry.
					Status.Refresh = true;
					Status.Error = cache == nullptr;
					Status.Loaded = cache != nullptr;
					return cache != nullptr;
				}

				// A file can change while the isolated candidate is being parsed.  Do
				// not publish a mixed generation; let the next query retry after the
				// writer has completed.  The same check catches a transaction that
				// started during parsing even when the root file timestamp is unchanged.
				uint64_t write_after = 0;
				std::wstring generation_after;
				if(application.Config != config_path ||
					Path::IsFileExists(transaction_marker) ||
					!ReadFileWriteTime(application.Config.c_str(), write_after) ||
					!ReadStudioGeneration((application.Config + L".studio-generation").c_str(), generation_after) ||
					write_before != write_after || generation_before != generation_after)
				{
					Path::CurrentDirectory(current_directory);
					directory_changed = false;
					Status.Refresh = true;
					Status.Error = false;
					Status.Loaded = cache != nullptr;
					return cache != nullptr;
				}

				Path::CurrentDirectory(current_directory);
				directory_changed = false;
				candidate->fonts.init(HInstance);
				ContextMenu::FontNotFound = false;

				// Resolve process-global image references without publishing candidate
				// pointers.  Building this list can allocate, so keep all such work
				// private until the candidate and publication bookkeeping are ready.
				struct PendingMuidImage
				{
					MUID *uid;
					Expression *image;
				};
				std::vector<PendingMuidImage> pending_muid_images;
				pending_muid_images.reserve(MAP_MUID.size());
				for(auto &entry : MAP_MUID)
				{
					for(auto &image : candidate->images)
					{
						if(image.equals(entry.second.id))
						{
							pending_muid_images.push_back({ &entry.second, image.value });
							break;
						}
					}
				}

				// Prepare every potentially throwing publication operation before
				// changing either the active cache or MAP_MUID.  Retaining the previous
				// cache first means a failed allocation cannot leave it unowned.
				std::wstring next_runtime_config_path(application.Config.c_str(),
					application.Config.length());
				std::wstring next_runtime_generation = std::move(generation_after);
				candidate->config_path = next_runtime_config_path;
				candidate->runtime_generation = next_runtime_generation;
				CACHE *previous = cache;
				if(previous)
					_retired_caches.reserve(_retired_caches.size() + 1);
				if(previous)
					_retired_caches.push_back(previous);

				// Publish only a fully parsed and initialized cache.  Existing
				// ContextMenu objects may still reference the old one, so defer its
				// destruction until all active instances have gone away.
				cache = candidate.release();
				_runtime_generation.swap(next_runtime_generation);
				_runtime_config_path.swap(next_runtime_config_path);
				_last_write_time = write_after;
				_config_state_initialized = true;

				// These assignments are non-throwing and happen only after all setup
				// that can discard the candidate has completed.
				for(auto &entry : MAP_MUID)
					entry.second.image = nullptr;
				for(const auto &pending : pending_muid_images)
					pending.uid->image = pending.image;

				if(previous)
				{
					collect_retired_caches();
				}

				LastError = {};
				Status.Loaded = true;
				Status.Error = false;
				Status.Refresh = false;
				Status.Disabled = false;

				//::SHChange_Notify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, nullptr, nullptr);
				return true;
			}
			catch(...)
			{
				if(directory_changed)
					Path::CurrentDirectory(current_directory);
				Status.Refresh = false;
				Status.Error = cache == nullptr;
				Status.Loaded = cache != nullptr;
#ifdef _DEBUG
				Logger::Exception(__func__);
#endif
			}
			return cache != nullptr;
		}

		bool Initializer::uninit()
		{
			__try
			{
				if(cache)
				{
					// ContextMenu instances retain pointers into their cache. Route the
					// active cache through the same retired list used by reloads so a
					// disable/uninitialize path cannot free it underneath Explorer.
					_retired_caches.push_back(cache);
					cache = nullptr;
				}

				_runtime_generation.clear();
				_runtime_config_path.clear();
				_last_write_time = 0;
				_config_state_initialized = false;
				collect_retired_caches();
				if(ContextMenu::Processes.empty())
					MAP_MUID.clear();
			}
			__except(EXCEPTION_EXECUTE_HANDLER)
			{
#ifdef _DEBUG
				Logger::Exception(__func__);
#endif
			}

			Status.Loaded = false;
			Status.Refresh = false;
			//Status->Unload = false;
			return true;
		}

		void Initializer::collect_retired_caches()
		{
			// ContextMenu::Processes contains every live menu instance.  Do not
			// release any retired cache while one could still dereference it.
			if(!ContextMenu::Processes.empty())
				return;

			for(auto retired : _retired_caches)
				delete retired;
			_retired_caches.clear();
		}

		bool Initializer::query(int ch)
		{
			__try
			{
				if(Status.Disabled)
				{
					uninit();
					return false;
				}

				if(Status.Error && ch < 2)
					return false;

				// Check the root timestamp at the same boundary that publishes a
				// context-menu cache.  The transaction marker makes this a no-op
				// while Studio is replacing its multi-file configuration.
				if(Status.Loaded && !Status.Refresh && config_has_changed())
					Status.Refresh = true;

				return init();
			}
			__except(EXCEPTION_EXECUTE_HANDLER)
			{
#ifdef _DEBUG
				Logger::Exception(__func__);
#endif
			}
			return false;
		}

		//determine config file changed
		bool Initializer::config_has_changed()
		{
			try
			{
				if(!_config_state_initialized || application.Config.empty())
					return false;
				if(Path::IsFileExists(application.Config + L".studio-transaction.json"))
					return false;

			uint64_t write_time = 0;
			std::wstring generation;
			const string generation_marker = application.Config + L".studio-generation";
			if(!ReadFileWriteTime(application.Config.c_str(), write_time) ||
				!ReadStudioGeneration(generation_marker.c_str(), generation))
				return true;

			const std::wstring current_path(application.Config.c_str(), application.Config.length());
			return current_path != _runtime_config_path ||
				write_time != _last_write_time || generation != _runtime_generation;
			}
			catch(...)
			{
#ifdef _DEBUG
				Logger::Exception(__func__);
#endif
				return true;
			}
		}

		bool Initializer::has_error(bool detect_changes)
		{
			if(Status.Error)
			{
				return detect_changes ? !config_has_changed() : true;
			}
			return false;
		}

		void Initializer::load_mui()
		{
			auto ldstr = [&, this](const wchar_t *dll,
								std::initializer_list<MUID> list)
			{
				auto hModule = ::GetModuleHandleW(dll);
				bool load_as_dynamic = false;
				if(!hModule)
				{
					hModule = ::LoadLibraryExW(dll, nullptr, LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE);
					load_as_dynamic = true;
				}

				if(hModule)
				{
					for(auto &it : list)
					{
						auto uid = &MAP_MUID[it.id];
						if(uid->title.empty())
						{
							*uid = it;
							auto len = ::LoadStringW(hModule, uid->res_id, uid->title.buffer(MAX_PATH), MAX_PATH);
							if(len > 0)
							{
								uid->title.release(len);
								uid->set_hash();
								//get_image(uid);
							}
						}
					}
				}
				else
				{
					for(auto &it : list)
					{
						auto uid = &MAP_MUID[it.id];
						*uid = it;
						//get_image(uid);
					}
				}

				if(hModule && load_as_dynamic)
					::FreeLibrary(hModule);
			};

			auto parse_menu = [&](HMODULE hModule, uint32_t menu_id,
								 std::initializer_list<MUID> list)
			{
				if(hModule)
				{
					auto hMenu = ::LoadMenuW(hModule, MAKEINTRESOURCEW(menu_id));
					if(hMenu)
					{
						for(auto &it : list)
						{
							auto uid = &MAP_MUID[it.id];
							if(uid->title.empty())
							{
								*uid = it;
								MENUITEMINFOW mii{ sizeof(mii), MIIM_STRING };
								mii.dwTypeData = uid->title.buffer(MAX_PATH);
								mii.cch = MAX_PATH;
								if(::GetMenuItemInfoW(hMenu, uid->res_id, false, &mii) && mii.cch > 0)
								{
									uid->title.release(mii.cch);
									uid->set_hash();

									//get_image(uid);
								}
								else if(it.title_fallback.length() > 0)
								{
									uid->title = it.title_fallback;
									uid->set_hash();
								}
							}
						}
						::DestroyMenu(hMenu);
					}
					else for(auto &it : list)
					{
						auto uid = &MAP_MUID[it.id];
						*uid = it;
						//uid->title = it.title_fallback;
					//	uid->set_hash();
						//get_image(uid);
					}
				}
				else for(auto &it : list)
				{
					auto uid = &MAP_MUID[it.id];
					*uid = it;
				//	uid->title = it.title_fallback;
				//	uid->set_hash();
					//get_image(uid);
				}
			};

			auto ldmenu = [&](const wchar_t *dll, uint32_t menu_id,
								 std::initializer_list<MUID> list)
			{
				HMODULE lib{}, hModule = ::GetModuleHandleW(dll);
				if(!hModule)
				{
					lib = ::LoadLibraryExW(dll, nullptr, LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE);
					hModule = lib;
				}

				parse_menu(hModule, menu_id, list);

				if(lib) ::FreeLibrary(lib);
			};

			struct menu_res {
				uint32_t id{};
				std::initializer_list<MUID> items;
			};

			auto ldmenu2 = [&](const wchar_t *dll, std::initializer_list<menu_res> list)
			{
				HMODULE lib{}, hModule = ::GetModuleHandleW(dll);
				if(!hModule)
				{
					lib = ::LoadLibraryExW(dll, nullptr, LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE);
					hModule = lib;
				}

				if(hModule)
				{
					for(auto &it : list)
					{
						parse_menu(hModule, it.id, it.items);
					}
				}
				else for(auto &it : list)
				{
					for(auto &ui : it.items)
					{
						auto uid = &MAP_MUID[ui.id];
						if(uid->title.empty())
						{
							*uid = ui;
						//get_image(uid);
						}
					}
				}

				if(lib) ::FreeLibrary(lib);
			};

			auto add_uid = [](std::initializer_list<MUID> list) {
				for(auto &&uid : list)
					MAP_MUID.emplace(uid.id, uid);
			};

			auto isw11 = [](uint32_t val1, uint32_t val2)->uint32_t { return os->IsWindows11OrGreater() ? val1 : val2; };
			auto isw8 = [](uint32_t val1, uint32_t val2)->uint32_t { return os->IsWindows8OrGreater() ? val1 : val2; };

			auto user32 = L"user32.dll";
			auto shell32 = L"shell32.dll";
			auto explorerframe = L"explorerframe.dll";
			auto explorer = L"explorer.exe";
			
			ldstr(L"acppage.dll", {
				{IDENT_ID_TROUBLESHOOT_COMPATIBILITY, 2022},
			 });

			if(os->Major >= 10 && os->Build > 10240)
			{
				ldstr(L"ntshrui.dll", {
				   {IDENT_ID_GIVE_ACCESS_TO, 103},
				   {IDENT_ID_SHARE_WITH, 104},
				   {IDENT_ID_SHARE, 107},
				});
			}
			else 
			{
				ldstr(L"ntshrui.dll", {
				   {IDENT_ID_SHARE_WITH, 103},
				   {IDENT_ID_SHARE, 107},
				});

				add_uid({
					{ IDENT_ID_GIVE_ACCESS_TO, 0}
				});
			}

			ldstr(L"appresolver.dll", {
				{ IDENT_ID_OPEN, 8550 },
				{ IDENT_ID_RUN_AS_ADMINISTRATOR, 8551},
				{IDENT_ID_RUN_AS_DIFFERENT_USER, 8558},
				//{ IDENT_ID_OPEN_FILE_LOCATION, 8552, { L'\uE0E8', 0 } },
			});

			ldstr(L"windows.storage.dll", {
				{IDENT_ID_OPEN_IN_NEW_WINDOW, 8517},
				{IDENT_ID_OPEN_IN_NEW_TAB, 8519},
			});

			ldmenu2(shell32, 
			{ 
				{208, {
					{IDENT_ID_OPEN_WITH, 1},
					//{ IDENT_ID_CANCEL, 0 },
				}},
				{	210, {
					{ IDENT_ID_CREATE_SHORTCUT, 16},
					{ IDENT_ID_CUT, 24},
					{ IDENT_ID_COPY, 25},
					{ IDENT_ID_PASTE, 26},
					{ IDENT_ID_RENAME, 18},
					{ IDENT_ID_DELETE, 17},
				}},
				{	211, {
					{ IDENT_ID_PROPERTIES, 19 } 
				}},
				{	215, {
					{ IDENT_ID_ARRANGE_BY, 28678},
					{ IDENT_ID_VIEW, 28674},
					{ IDENT_ID_GROUP_BY, 28676},
					{ IDENT_ID_SORT_BY, 28673},
					{ IDENT_ID_REFRESH, 28931},
					{ IDENT_ID_CUSTOMIZE_THIS_FOLDER, 28722},
					{ IDENT_ID_PASTE_SHORTCUT, 28700},
					{ IDENT_ID_UNDO, 28699},
					{ IDENT_ID_REDO, 28704}
				}},
				/*{216, {
				    { IDENT_ID_PROPERTIES, 28691},
					{ IDENT_ID_COPY_TO_FOLDER, 28702},
					{ IDENT_ID_MOVE_TO_FOLDER, 28703},
				}},*/
				{217, { 
					{ IDENT_ID_EXTRA_LARGE_ICONS, 28749},
					{ IDENT_ID_LARGE_ICONS, 28751},
					{ IDENT_ID_MEDIUM_ICONS, 28750},
					{ IDENT_ID_SMALL_ICONS, 28752},
					{ IDENT_ID_LIST, 28753},
					{ IDENT_ID_DETAILS, 28747},
					{ IDENT_ID_TILES, 28748},
					{ IDENT_ID_CONTENT, 28754},
					{ IDENT_ID_EXPAND_ALL_GROUPS , 28755},
					{ IDENT_ID_COLLAPSE_ALL_GROUPS , 28756},
					{ IDENT_ID_COPY_TO_FOLDER, 28702},
					{ IDENT_ID_MOVE_TO_FOLDER, 28703},
					{ IDENT_ID_SELECT_ALL, isw11(28707, 28705)},
					{ IDENT_ID_INVERT_SELECTION, isw11(28708,28706)}
				}},
				{	218, {
					{ IDENT_ID_SHOW_DESKTOP_ICONS, 29698}
				}},
				{	223, {
					{IDENT_ID_SEND_TO, 79},
				}},
				{	225, {
					{ IDENT_ID_MAP_NETWORK_DRIVE, 14},
					{IDENT_ID_DISCONNECT_NETWORK_DRIVE, 9},
				}}, 
				{	226, {
					{ IDENT_ID_RESTORE, 139},
				}},
				{	228, {
					{ IDENT_ID_ADD_A_NETWORK_LOCATION, 1},
				}},
				{	247, {
					{ IDENT_ID_FORMAT, 8},
					{ IDENT_ID_DISCONNECT, 9 },
					{ IDENT_ID_EJECT, 10},
					{ IDENT_ID_ERASE_THIS_DISC, 3},
				}},
				{	275, {
					{ IDENT_ID_SHOW_THIS_PC, 4},
					{ IDENT_ID_SHOW_NETWORK, 5},
					{ IDENT_ID_SHOW_LIBRARIES, 3},
					//{ IDENT_ID_SHOW_ALL_FOLDERS, isw8(1, 0), {} },
					//{ IDENT_ID_EXPAND_TO_CURRENT_FOLDER, isw8(2, 1),/*, {L'\uE1B1', 0}*/ },
				}},
				/*{	280, {
					{ IDENT_ID_OPEN_FILE_LOCATION, 0, { L'\uE0E8', 0 } },
				}},
				{	394, {
					{ IDENT_ID_EDIT, 400, { L'\uE0A1', 0 } }
				}},
				{	360, {
					{IDENT_ID_OPEN, 25},
					{IDENT_ID_RUN_AS_ADMINISTRATOR, 32, {L'\uE057', L'\uE058'}},
				}},*/
				{	502, {
					{ IDENT_ID_REMOVE_PROPERTIES, 2 }
				}},
				{	200, {
					{IDENT_ID_COPY_HERE, 1},
					{ IDENT_ID_MOVE_HERE, 2},
					{IDENT_ID_CREATE_SHORTCUTS_HERE, 3},
					{IDENT_ID_CANCEL, 0},
				}},
				{262, {
					{IDENT_ID_INCLUDE_IN_LIBRARY, 0}
				}},
			});

			ldmenu2(explorerframe, { {
				3400, {
					{ IDENT_ID_EXPAND, 8},
					{ IDENT_ID_COLLAPSE, 9}
				}},
				{260,{
						{IDENT_ID_SELECT, 0, {}, {}, L"Select"} //shell32/menu/260/0  | 49927
					}
				},
				{266,{
						{IDENT_ID_GO_TO, 33104}
					}
				}
			});

			ldmenu2(explorer, {
				{ 205,{
					{IDENT_ID_ADJUST_DATE_TIME, 408, {L'\uE1F2'}, {},L"&Adjust date/time"},
					{IDENT_ID_CUSTOMIZE_NOTIFICATION_ICONS, 421, {L'\uE190'}, {},L"&Customize notification icons"},
					{IDENT_ID_CORTANA,445, {L'\ue170'}, {},L"Cortana"},
					{IDENT_ID_SHOW_CORTANA_BUTTON ,449, {L'\ue170'}, {},L"Show Cortana button"},
					{IDENT_ID_NEWS_AND_INTERESTS,621, {}, {},L"&News and interests"},
					{IDENT_ID_SHOW_TASK_VIEW_BUTTON,430, {L'\uE204'}, {},L"Show Task &View button"},
					{IDENT_ID_SHOW_PEOPLE_ON_THE_TASKBAR,435, {L'\uE106'}, {},L"Show &People on the taskbar"},
					{IDENT_ID_SHOW_PEN_BUTTON,437, {}, {},L"Show Pen button"},
					{IDENT_ID_SHOW_TOUCH_KEYBOARD_BUTTON,436, {L'\ue18c'}, {},L"Show touch ke&yboard button"},
					{IDENT_ID_SHOW_TOUCHPAD_BUTTON,438, {L'\uE1EB'}, {},L"Show touch&pad button"},
					{IDENT_ID_CASCADE_WINDOWS,403, {}, {},L"Casca&de windows"},
					{IDENT_ID_SHOW_WINDOWS_STACKED,405, {},{}, L"Show windows stack&ed"},
					{IDENT_ID_SHOW_WINDOWS_SIDE_BY_SIDE,404, {}, {},L"Show windows s&ide by side" },
					{IDENT_ID_SHOW_THE_DESKTOP,407, {}, {},L"&Show the desktop"},
					{IDENT_ID_TASK_MANAGER,420, {}, {},L"Tas&k Manager"},
					{IDENT_ID_LOCK_THE_TASKBAR,424, {}, {},L"&Lock the taskbar"},
					{IDENT_ID_LOCK_ALL_TASKBARS,425, {}, {},L"&Lock all taskbars"},
					{IDENT_ID_TASKBAR_SETTINGS, 413, {},{}, L"Taskbar se&ttings"},
					{IDENT_ID_EXIT_EXPLORER, 518, {L'\uE0D0'},{}, L"E&xit Explorer"}
				}},
				{12000,{
					{IDENT_ID_RESTORE_ALL_WINDOWS, 65493, {}, {}, L"&Restore all windows"},
					{IDENT_ID_MINIMIZE_ALL_WINDOWS, 65492,{}, {}, L"&Minimize all windows"},
					{IDENT_ID_CLOSE_ALL_WINDOWS, 65491, {}, {}, L"&Close all windows"},
				}}
			});

			ldmenu2(user32, {
				{
					1, {{ IDENT_ID_INSERT_UNICODE_CONTROL_CHARACTER, 32787, {L'\uE14e'} }}
				},
				{ 
					16, {
					{ IDENT_ID_MINIMIZE, 61472 },
					{ IDENT_ID_MAXIMIZE, 61488 },
					{ IDENT_ID_MOVE, 61456 },
					{ IDENT_ID_SIZE, 61440 },
				}}
			});

			ldmenu2(L"wpdshext.dll", { 
				{20, {
					{IDENT_ID_IMPORT_PICTURES_AND_VIDEOS, 21}
				}},
				{30, {
					{ IDENT_ID_FORMAT, 31 }
				}}
			});

			ldstr(user32, {
				{IDENT_ID_RECONVERSION, 705, {L'\uE096'}},
			});

			//IDENT_ID_FILE_EXPLORER OPTIONS 22985

			ldstr(shell32, {
				{IDENT_ID_ADD_TO_PLAYLIST, 37427},
				{IDENT_ID_AUTOPLAY, 31384, {L'\ue1f9'}},
				{IDENT_ID_CAST_TO_DEVICE, 31289},
				{IDENT_ID_COMMAND_PROMPT, 22022},
				{IDENT_ID_CONTROL_PANEL, 4161, {L'\uE0F3'}},
				{IDENT_ID_COPY_TO, 30339},
				{IDENT_ID_MOVE_TO, 30340},
				{IDENT_ID_COPY_TO_FOLDER, 30304},
				{IDENT_ID_MOVE_TO_FOLDER, 30305},
				{IDENT_ID_COPY_AS_PATH, 30328},
				{IDENT_ID_COPY_PATH, 30329},
				{IDENT_ID_DESKTOP, 4162},
				{IDENT_ID_DEVICE_MANAGER, 24225},
				{IDENT_ID_EMPTY_RECYCLE_BIN, 10564},
				{IDENT_ID_EXTRACT_ALL, 37514},
				{IDENT_ID_EXTRACT_TO, 37516},
				{IDENT_ID_FILE_EXPLORER, 12352},
				{IDENT_ID_FILE_EXPLORER_OPTIONS, 22985},
				{IDENT_ID_RESTORE_DEFAULT_LIBRARIES, 34645},
				{IDENT_ID_MANAGE, 37602},
				{IDENT_ID_MAP_AS_DRIVE, 37440},
				{IDENT_ID_MOUNT, 31472},
				{IDENT_ID_NEW, 30315},
				{IDENT_ID_OPEN_AUTOPLAY, 8507},
				{IDENT_ID_OPEN_COMMAND_PROMPT, 37415},
				{IDENT_ID_OPEN_COMMAND_WINDOW_HERE, 8506},
				{IDENT_ID_OPEN_FILE_LOCATION, 1033 },
				{IDENT_ID_OPEN_FOLDER_LOCATION, 1040},
				{IDENT_ID_OPEN_IN_NEW_PROCESS, 8518},
				{IDENT_ID_OPEN_NEW_TAB, 37616},
				{IDENT_ID_OPEN_NEW_WINDOW, 37411},
				{IDENT_ID_OPEN_POWERSHELL_WINDOW_HERE, 8508},
				{IDENT_ID_OPEN_WINDOWS_POWERSHELL, 37446},
				{IDENT_ID_OPTIONS, 37459},
				{IDENT_ID_PIN_TO_QUICK_ACCESS, 51377},
				{IDENT_ID_PIN_CURRENT_FOLDER_TO_QUICK_ACCESS, 51388},
				{IDENT_ID_PIN_TO_START, 51201},
				{IDENT_ID_ADD_TO_FAVORITES, 51389},		//37408  windows 11 22H2
				{IDENT_ID_REMOVE_FROM_FAVORITES, 51390},//windows 11 22H2
				{IDENT_ID_REMOVE_FROM_RECENT, 51391},
				{IDENT_ID_PIN_TO_TASKBAR, 5386},
				{IDENT_ID_PLAY, 31283, {L'\uE1A5'}},
				{IDENT_ID_PREVIEW, 51260, {L'\uE275'}},
				{IDENT_ID_PRINT, 31250, {L'\uE0A8', 0}},
				{IDENT_ID_REMOVE_FROM_QUICK_ACCESS, 51381},
				{IDENT_ID_ROTATE_LEFT, 37610},
				{IDENT_ID_ROTATE_RIGHT, 37612},
				{IDENT_ID_RUN, 12710},
				{IDENT_ID_RUN_AS_ANOTHER_USER, 37419},
				{IDENT_ID_SEARCH, 12708, {L'\uE187'}},
				{IDENT_ID_EDIT, 37398},
				//{IDENT_ID_TROUBLESHOOT_COMPATIBILITY, 37425},
				{IDENT_ID_UNPIN_FROM_QUICK_ACCESS,51379},
				{IDENT_ID_UNPIN_FROM_START, 51394},
				{IDENT_ID_UNPIN_FROM_TASKBAR, 5387},
				{IDENT_ID_COMPRESSED, 31462},
				{IDENT_ID_CLEANUP, 37494},
				{IDENT_ID_NEW_FOLDER, 16859},
				{IDENT_ID_NEW_ITEM, 37400},
				{IDENT_ID_AUTO_ARRANGE_ICONS, 31157},//28785
				{IDENT_ID_ALIGN_ICONS_TO_GRID, 31158},// 28788
				{IDENT_ID_MORE_OPTIONS, 31153},
				{IDENT_ID_INSTALL, 10210},
				{IDENT_ID_CONFIGURE, 10209, { L'\uE0F3'}},
				{IDENT_ID_SELECT_ALL, 31276},
				{IDENT_ID_SELECT_NONE, 37378},
				{IDENT_ID_ALL_CONTROL_PANEL_ITEMS, 32012 },
				{IDENT_ID_IMPORT_PICTURES_AND_VIDEOS, 31285, {L'\uE14F'} },
				{IDENT_ID_CLOSE, 31450},
				{IDENT_ID_PERMANENTLY_DELETE, 37394}
			});

			//window 7
			if(!os->IsWindows8OrGreater())
			{
				ldstr(shell32, {
					{IDENT_ID_PIN_TO_START_MENU, 5381},
					{IDENT_ID_UNPIN_FROM_START_MENU, 5382}
				});
			}
			else 
			{
				add_uid({
					{ IDENT_ID_PIN_TO_START_MENU, 0},
					{ IDENT_ID_UNPIN_FROM_START_MENU, 0}
				});
			}
			
			ldstr(explorerframe, {
				{IDENT_ID_FOLDER_OPTIONS, 1030},
				//{IDENT_ID_SHARE, 49928},
				//{IDENT_ID_SHARE_WITH, 49930},
				{IDENT_ID_EXPAND_GROUP, 41220},
				{IDENT_ID_COLLAPSE_GROUP, 41221},
				{IDENT_ID_SELECT, 49927} //shell32/menu/260/0
			});

			/*ldstr(L"unregmp2.exe", {
				{IDENT_ID_ADD_TO_WINDOWS_MEDIA_PLAYER_LIST, 9800},
				{IDENT_ID_PLAY_WITH_WINDOWS_MEDIA_PLAYER, 9801},
			});*/

			ldstr(L"display.dll", { {IDENT_ID_DISPLAY_SETTINGS, 4} });
			ldstr(L"themecpl.dll", { {IDENT_ID_PERSONALIZE, 10} });
			ldstr(L"regedit.exe", { {IDENT_ID_MERGE, 310, {L'\uE142'} } });
			ldstr(L"powershell.exe", { {IDENT_ID_RUN_WITH_POWERSHELL, 108} });

			ldstr(L"stobject.dll", {
				{IDENT_ID_SET_AS_DESKTOP_BACKGROUND, 417},
				{IDENT_ID_NEXT_DESKTOP_BACKGROUND, 416}
			 });

			ldstr(L"fvecpl.dll", {
				{IDENT_ID_TURN_ON_BITLOCKER, 1140},
				{IDENT_ID_TURN_OFF_BITLOCKER, 1141},
			});

			ldstr(L"twext.dll", {
				{IDENT_ID_RESTORE_PREVIOUS_VERSIONS, 1037},
				//{IDENT_ID_RESTORE, 1033},
			});

			ldstr(L"twinui.dll", {
				{IDENT_ID_SETTINGS, 5621},
				{IDENT_ID_STORE, 10669, {L'\uE1B9'}},
				{IDENT_ID_POWER_OPTIONS, 5950},
				//{IDENT_ID_AUTOPLAY, 9914},
				//{IDENT_ID_REFRESH, 10620},
				{IDENT_ID_REMOVE, 11454, {L'\uE256'}},
				{IDENT_ID_ACCOUNT, 11452, {L'\uE106'}},
				{IDENT_ID_COPY_TO_CLIPBOARD, 13648},
				{IDENT_ID_WINDOWS_POWERSHELL, 10928}
			});

			ldstr(L"twinui.pcshell.dll", {
				{IDENT_ID_WINDOWS_POWERSHELL, 10928, {}, {}, L"Windows PowerShell"},
				{IDENT_ID_WINDOWS_TERMINAL, 10944},
				{IDENT_ID_TERMINAL, 10960, {}, {}, L"Terminal" }
			});

			ldstr(L"wpdshext.dll", {
				{ IDENT_ID_OPEN_AS_PORTABLE, 349 }
			});

			//Import_pictures_and_videos
			ldstr(L"isoburn.exe", {
				{IDENT_ID_BURN_DISC_IMAGE, 351}
			});

			ldstr(explorer, {
				{IDENT_ID_WINDOWS_TERMINAL, 22016, {}, {}, L"Windows Terminal"},
				{IDENT_ID_WINDOWS, 11104, {}, {}, L"Windows"},
				{IDENT_ID_TASKBAR, 518},
				{IDENT_ID_FILE_EXPLORER, 6020},
				{IDENT_ID_SHOW_OPEN_WINDOWS, 850},
				//{IDENT_ID_HIDE, 1003},
				//{IDENT_ID_SHOW, 1004}
			});


			add_uid({
				{ IDENT_ID_SET_AS_DESKTOP_WALLPAPER, 0},
				{ IDENT_ID_SHOW_FILE_EXTENSIONS, 0},
				{ IDENT_ID_SHOW_HIDDEN_FILES,  0},
				{ IDENT_ID_MAKE_AVAILABLE_OFFLINE, 0},
				{ IDENT_ID_MAKE_AVAILABLE_ONLINE, 0},
				{ IDENT_ID_SHIELD, 0,}
			});
		}

		// enabled/reload
		// disabled
		// mix
		bool Initializer::OnState(bool istaskbar)
		{
			bool reloaded = false;

			int changed = 0;

			Keyboard kb;
			auto count = kb.get_keys_state();

			if(istaskbar && kb.is_lbutton())
				return false;

			if(kb.is_rbutton())
				count--;

			if(kb.is_contextmenu())
				count -= 2;

			if(count == 0 || count > 2)
				return changed;

			auto kwin = kb.key_win();
			auto kctrl = kb.key_ctrl();
			auto kshift = kb.key_shift();

			//int key = GetSystemMetrics(SM_SWAPBUTTON) ? VK_RBUTTON : VK_LBUTTON;

			if(count == 1)
			{
				if(kctrl || kb.is_lbutton() || kb.key(VK_F5)) // reload/enabled shell
				{
					//Modern(1);
					Status.Disabled = false;
					Status.Refresh = true;
					changed = 1;
				}
				else if(kwin) // modern + shell
				{
					//if(Status.Unload)
					//	Status.Unload = false;
					//Modern(0);
					Status.Refresh = false;
					changed = 1;
				}
			}
			else if(count == 2)
			{
				if(kctrl && kwin) // disabled shell/modern
				{
					//Modern(2);
					Status.Disabled = true;
					Status.Refresh = false;
					changed = 1;
				}
				else if(kshift && kctrl) // reload/enabled shell
				{
					Status.Disabled = false;
					Status.Refresh = true;
					changed = 2;
				}
			}

			if(changed > 0)
			{
				if(Status.Disabled)
					Initializer::instance->uninit();
				else if(Status.Refresh)
				{
					Initializer::instance->query(changed);
					reloaded = true;
				}
			}
			return reloaded;
		}
	}
}
