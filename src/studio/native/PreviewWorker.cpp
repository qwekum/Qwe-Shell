#include "PreviewJson.h"

#define NOMINMAX
#include <windows.h>

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cwchar>
#include <fcntl.h>
#include <limits>
#include <mutex>
#include <string>
#include <string_view>
#include <thread>
#include <vector>
#include <io.h>

namespace
{
	using ShellStudio::PreviewJson::Kind;
	using ShellStudio::PreviewJson::Limits;
	using ShellStudio::PreviewJson::Value;

	constexpr std::size_t MaxFrameBytes = 16u * 1024u * 1024u;
	constexpr std::size_t MaxIdBytes = 128;
	constexpr std::size_t MaxOperationBytes = 32;
	constexpr std::uint32_t WorkerExitBadRequest = 2;
	constexpr std::uint32_t WorkerExitProtocol = 4;
	constexpr std::chrono::seconds NativeDeadline{30};

	using PreviewFunction = char* (__cdecl*)(const wchar_t* json, std::size_t length) noexcept;
	using FreeFunction = void (__cdecl*)(void* pointer) noexcept;

	struct Request
	{
		std::string id;
		std::string revision;
		std::string operation;
		// The language export receives the bounded native payload rather than
		// the transport envelope.  The payload is normalized to carry the
		// operation as well, so the export has one self-describing input shape.
		std::string payloadJson;
	};

	struct ProcessBudget
	{
		HANDLE job = nullptr;
		ProcessBudget() = default;
		~ProcessBudget() { if (job) CloseHandle(job); }
		ProcessBudget(const ProcessBudget&) = delete;
		ProcessBudget& operator=(const ProcessBudget&) = delete;
		ProcessBudget(ProcessBudget&& other) noexcept : job(other.job) { other.job = nullptr; }
		ProcessBudget& operator=(ProcessBudget&& other) noexcept
		{
			if (this != &other)
			{
				if (job) CloseHandle(job);
				job = other.job;
				other.job = nullptr;
			}
			return *this;
		}
	};

	std::string Quote(std::string_view value)
	{
		return ShellStudio::PreviewJson::Quote(value);
	}

	std::string Diagnostic(std::string_view code, std::string_view message, std::string_view severity = "error", std::string_view remedy = {})
	{
		std::string result = "{\"code\":" + Quote(code) + ",\"message\":" + Quote(message) + ",\"severity\":" + Quote(severity);
		if (!remedy.empty()) result += ",\"remedy\":" + Quote(remedy);
		result += "}";
		return result;
	}

	std::string Response(std::string_view id, std::string_view revision, std::string_view operation,
		std::string_view status, std::string_view result, std::string_view diagnostics)
	{
		return "{\"version\":1,\"id\":" + Quote(id) + ",\"revision\":" + Quote(revision) +
			",\"operation\":" + Quote(operation) + ",\"status\":" + Quote(status) +
			",\"result\":" + std::string(result.empty() ? "null" : result) +
			",\"diagnostics\":" + std::string(diagnostics.empty() ? "[]" : diagnostics) + "}";
	}

	std::string ErrorResponse(std::string_view id, std::string_view revision, std::string_view operation,
		std::string_view code, std::string_view message, std::string_view remedy = {})
	{
		return Response(id, revision, operation, "error", "null", "[" + Diagnostic(code, message, "error", remedy) + "]");
	}

	bool ReadExact(void* destination, std::size_t size)
	{
		if (size == 0) return true;
		const auto bytes = static_cast<char*>(destination);
		std::size_t total = 0;
		while (total < size)
		{
			const std::size_t remaining = size - total;
			const std::streamsize request = static_cast<std::streamsize>(remaining > static_cast<std::size_t>(std::numeric_limits<std::streamsize>::max())
				? static_cast<std::size_t>(std::numeric_limits<std::streamsize>::max()) : remaining);
			const std::size_t before = total;
			if (std::fread(bytes + total, 1, static_cast<std::size_t>(request), stdin) != static_cast<std::size_t>(request)) return false;
			total += static_cast<std::size_t>(request);
			if (total == before) return false;
		}
		return true;
	}

	bool WriteExact(const void* source, std::size_t size)
	{
		if (size == 0) return true;
		return std::fwrite(source, 1, size, stdout) == size && std::fflush(stdout) == 0;
	}

	bool WriteFrame(std::string_view json)
	{
		if (json.empty() || json.size() > MaxFrameBytes || json.size() > static_cast<std::size_t>(std::numeric_limits<std::int32_t>::max())) return false;
		const std::uint32_t length = static_cast<std::uint32_t>(json.size());
		unsigned char prefix[4] = {
			static_cast<unsigned char>(length & 0xff), static_cast<unsigned char>((length >> 8) & 0xff),
			static_cast<unsigned char>((length >> 16) & 0xff), static_cast<unsigned char>((length >> 24) & 0xff) };
		return WriteExact(prefix, sizeof(prefix)) && WriteExact(json.data(), json.size());
	}

	bool ReadFrame(std::string& json)
	{
		unsigned char prefix[4]{};
		if (!ReadExact(prefix, sizeof(prefix))) return false;
		const std::uint32_t length = static_cast<std::uint32_t>(prefix[0]) |
			(static_cast<std::uint32_t>(prefix[1]) << 8) | (static_cast<std::uint32_t>(prefix[2]) << 16) |
			(static_cast<std::uint32_t>(prefix[3]) << 24);
		if (length == 0 || length > MaxFrameBytes) return false;
		try { json.resize(length); }
		catch (...) { return false; }
		return ReadExact(json.data(), json.size());
	}

	bool RequiredString(const Value& object, std::string_view member, std::size_t maxBytes,
		std::string& destination, std::string& error)
	{
		const auto* value = object.Find(member);
		if (!value || value->kind != Kind::String || value->text.empty())
		{
			error = "Request member '" + std::string(member) + "' must be a non-empty string.";
			return false;
		}
		if (value->text.size() > maxBytes)
		{
			error = "Request member '" + std::string(member) + "' exceeds its size limit.";
			return false;
		}
		destination = value->text;
		return true;
	}

	bool ValidateRequest(std::string const& json, Request& request, std::string& error)
	{
		Value root;
		// Source documents and image payloads may be larger than the default
		// scalar limit, but the complete frame remains bounded.
		if (!ShellStudio::PreviewJson::Parse(json, root, error, Limits{ MaxFrameBytes, 48, 131072, 32768, 12u * 1024u * 1024u })) return false;
		if (root.kind != Kind::Object) { error = "Preview request must be a JSON object."; return false; }
		const auto* version = root.Find("version");
		if (!version || version->kind != Kind::Number || version->text != "1") { error = "Preview request has an unsupported protocol version."; return false; }
		if (!RequiredString(root, "id", MaxIdBytes, request.id, error) ||
			!RequiredString(root, "revision", MaxIdBytes, request.revision, error) ||
			!RequiredString(root, "operation", MaxOperationBytes, request.operation, error)) return false;
		if (request.operation != "analyze" && request.operation != "evaluate" &&
			request.operation != "render" && request.operation != "compose" &&
			request.operation != "capabilities" && request.operation != "resolve")
		{
			error = "Preview operation is not supported.";
			return false;
		}
		const auto* payload = root.Find("payload");
		if (!payload || payload->kind != Kind::Object) { error = "Preview request payload must be an object."; return false; }
		Value nativePayload = *payload;
		const auto* payloadOperation = nativePayload.Find("operation");
		if (payloadOperation && (payloadOperation->kind != Kind::String || payloadOperation->text != request.operation))
		{
			error = "Preview payload operation does not match its transport operation.";
			return false;
		}
		if (!payloadOperation)
		{
			Value operation(Kind::String);
			operation.text = request.operation;
			nativePayload.object.emplace_back("operation", std::move(operation));
		}
		request.payloadJson = ShellStudio::PreviewJson::ToJson(nativePayload);
		return true;
	}

	std::wstring ModuleDirectory()
	{
		std::vector<wchar_t> buffer(512);
		for (;;)
		{
			const DWORD length = GetModuleFileNameW(nullptr, buffer.data(), static_cast<DWORD>(buffer.size()));
			if (length == 0) return {};
			if (length < buffer.size() - 1)
			{
				std::wstring path(buffer.data(), length);
				const std::size_t slash = path.find_last_of(L"\\/");
				return slash == std::wstring::npos ? std::wstring() : path.substr(0, slash);
			}
			if (buffer.size() > 32768) return {};
			buffer.resize(buffer.size() * 2);
		}
	}

	bool Utf8ToWide(std::string_view value, std::wstring& destination)
	{
		if (value.size() > static_cast<std::size_t>(std::numeric_limits<int>::max())) return false;
		const int inputLength = static_cast<int>(value.size());
		const int length = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), inputLength, nullptr, 0);
		if (length <= 0) return false;
		try { destination.resize(static_cast<std::size_t>(length)); }
		catch (...) { return false; }
		return MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), inputLength, destination.data(), length) == length;
	}

	std::wstring LanguagePath(int argc, wchar_t** argv, bool& valid)
	{
		valid = true;
		std::wstring path;
		for (int index = 1; index < argc; ++index)
		{
			if (std::wcscmp(argv[index], L"--language") == 0 && index + 1 < argc)
			{
				if (!path.empty()) { valid = false; return {}; }
				path = argv[++index];
				if (path.empty()) { valid = false; return {}; }
			}
			else { valid = false; return {}; }
		}
		if (path.empty())
		{
			const std::wstring directory = ModuleDirectory();
			if (directory.empty()) { valid = false; return {}; }
			path = directory + L"\\ShellStudio.Language.dll";
		}
		return path;
	}

	ProcessBudget LimitProcess()
	{
		ProcessBudget budget;
		budget.job = CreateJobObjectW(nullptr, nullptr);
		if (!budget.job) return budget;
		JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
		limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_ACTIVE_PROCESS | JOB_OBJECT_LIMIT_PROCESS_MEMORY;
		limits.BasicLimitInformation.ActiveProcessLimit = 1;
		limits.ProcessMemoryLimit = static_cast<SIZE_T>(512u * 1024u * 1024u);
		if (!SetInformationJobObject(budget.job, JobObjectExtendedLimitInformation, &limits, sizeof(limits)) ||
			!AssignProcessToJobObject(budget.job, GetCurrentProcess()))
		{
			CloseHandle(budget.job);
			budget.job = nullptr;
		}
		return budget;
	}

	std::string NativeStatus(const Value& native)
	{
		const auto* status = native.Find("status");
		if (!status)
		{
			const auto* available = native.Find("available");
			if (available && available->kind != Kind::Boolean) return {};
			if (available && !available->boolean) return "unavailable";
			return "ok";
		}
		if (status->kind != Kind::String || status->text.empty()) return {};
		if (status->text == "ok" || status->text == "unavailable" || status->text == "error" || status->text == "cancelled" || status->text == "stale") return status->text;
		return {};
	}

	std::string NativeDiagnostics(const Value& native)
	{
		const auto* diagnostics = native.Find("diagnostics");
		if (!diagnostics || diagnostics->kind != Kind::Array) return "[]";
		return ShellStudio::PreviewJson::ToJson(*diagnostics);
	}

	bool ValidNativeDiagnostics(const Value& native)
	{
		const auto* diagnostics = native.Find("diagnostics");
		if (!diagnostics || diagnostics->kind != Kind::Array || diagnostics->array.size() > 4096) return false;
		for (const auto& diagnostic : diagnostics->array)
		{
			if (diagnostic.kind != Kind::Object) return false;
			const auto* code = diagnostic.Find("code");
			const auto* message = diagnostic.Find("message");
			const auto* severity = diagnostic.Find("severity");
			if (!code || code->kind != Kind::String || code->text.empty() || code->text.size() > 128 ||
				!message || message->kind != Kind::String || message->text.empty() || message->text.size() > 8192 ||
				!severity || severity->kind != Kind::String ||
				(severity->text != "error" && severity->text != "warning" && severity->text != "info") ||
				severity->text.size() > 32)
				return false;
		}
		return true;
	}

	std::string InvokeNative(const Request& request, std::wstring const& languagePath, bool& compositionReady)
	{
		// This one-request process retains the DLL until process exit: composed
		// windows and thread-local destructors still reference its code after return.
		HMODULE language = LoadLibraryExW(languagePath.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
		if (!language)
			return ErrorResponse(request.id, request.revision, request.operation, "PREVIEW_LANGUAGE_UNAVAILABLE",
				"The native preview language library could not be loaded.", "Build and publish ShellStudio.Language.dll beside the preview worker.");
		auto preview = reinterpret_cast<PreviewFunction>(GetProcAddress(language, "shell_studio_preview"));
		auto freeNative = reinterpret_cast<FreeFunction>(GetProcAddress(language, "shell_studio_free"));
		if (!preview || !freeNative)
		{
			return ErrorResponse(request.id, request.revision, request.operation, "PREVIEW_LANGUAGE_VERSION",
				"The native language library does not expose the preview contract.", "Rebuild the matching native language library.");
		}
		std::wstring wideRequest;
		if (!Utf8ToWide(request.payloadJson, wideRequest))
		{
			return ErrorResponse(request.id, request.revision, request.operation, "PREVIEW_UTF8",
				"The preview request could not be converted to UTF-16.");
		}

		std::mutex mutex;
		std::condition_variable condition;
		bool finished = false;
		std::thread watchdog([&]()
		{
			std::unique_lock lock(mutex);
			if (condition.wait_for(lock, NativeDeadline, [&]() { return finished; })) return;
			TerminateProcess(GetCurrentProcess(), WorkerExitProtocol);
		});

		char* resultPointer = nullptr;
		try { resultPointer = preview(wideRequest.data(), wideRequest.size()); }
		catch (...)
		{
			{ std::lock_guard lock(mutex); finished = true; } condition.notify_one();
			watchdog.join();
			return ErrorResponse(request.id, request.revision, request.operation, "PREVIEW_NATIVE_EXCEPTION",
				"The native preview operation failed without returning a result.");
		}
		{
			std::lock_guard lock(mutex);
			finished = true;
		}
		condition.notify_one();
		watchdog.join();

		if (!resultPointer)
		{
			return ErrorResponse(request.id, request.revision, request.operation, "PREVIEW_NATIVE_MEMORY",
				"The native preview operation returned no result.");
		}
		std::string nativeJson;
		try
		{
			const std::size_t maxResultBytes = MaxFrameBytes;
			for (std::size_t length = 0; length < maxResultBytes; ++length)
			{
				if (resultPointer[length] == '\0') { nativeJson.assign(resultPointer, length); break; }
				if (length + 1 == maxResultBytes) nativeJson.clear();
			}
		}
		catch (...) { nativeJson.clear(); }
		freeNative(resultPointer);
		if (nativeJson.empty())
			return ErrorResponse(request.id, request.revision, request.operation, "PREVIEW_RESULT_SIZE",
				"The native preview result is empty or exceeds the worker limit.");

		Value native;
		std::string parseError;
		if (!ShellStudio::PreviewJson::Parse(nativeJson, native, parseError, Limits{ MaxFrameBytes, 48, 131072, 32768, 8u * 1024u * 1024u }) || native.kind != Kind::Object)
			return ErrorResponse(request.id, request.revision, request.operation, "PREVIEW_RESULT_JSON",
				"The native preview result was not a valid JSON object.");
		const auto* version = native.Find("version");
		if (!version || version->kind != Kind::Number || version->text != "1")
			return ErrorResponse(request.id, request.revision, request.operation, "PREVIEW_RESULT_VERSION",
				"The native preview result has an unsupported version.");
		const auto* available = native.Find("available");
		if (!available || available->kind != Kind::Boolean || !ValidNativeDiagnostics(native))
			return ErrorResponse(request.id, request.revision, request.operation, "PREVIEW_RESULT_CONTRACT",
				"The native preview result does not satisfy its versioned result contract.");
		const std::string status = NativeStatus(native);
		if (status.empty() || ((status == "ok") != available->boolean))
			return ErrorResponse(request.id, request.revision, request.operation, "PREVIEW_RESULT_STATUS",
				"The native preview result has an unsupported or inconsistent status.");
		compositionReady = request.operation == "compose" && status == "ok" && available->boolean;
		return Response(request.id, request.revision, request.operation, status, nativeJson, NativeDiagnostics(native));
	}

	// A composed preview is deliberately a worker lifetime, not a detached
	// native UI process.  The language export owns the composed window/state;
	// this thread pumps its messages until the client closes or kills the worker.
	// No subsequent request is consumed, so an untrusted payload cannot turn the
	// persistent worker into a command channel.
	void PumpCompositionLifetime()
	{
		const HANDLE input = ::GetStdHandle(STD_INPUT_HANDLE);
		for (;;)
		{
			MSG message{};
			while (::PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE))
			{
				if (message.message == WM_QUIT) return;
				::TranslateMessage(&message);
				::DispatchMessageW(&message);
			}

			if (input && input != INVALID_HANDLE_VALUE && ::GetFileType(input) == FILE_TYPE_PIPE)
			{
				DWORD available = 0;
				if (!::PeekNamedPipe(input, nullptr, 0, nullptr, &available, nullptr))
					return; // The managed owner closed its protocol pipe.
			}
			::MsgWaitForMultipleObjectsEx(0, nullptr, 25, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
		}
	}
}

int wmain(int argc, wchar_t** argv)
{
	_setmode(_fileno(stdin), _O_BINARY);
	_setmode(_fileno(stdout), _O_BINARY);
	Request request;
	try
	{
		const ProcessBudget budget = LimitProcess();
		bool argumentsValid = false;
		const std::wstring languagePath = LanguagePath(argc, argv, argumentsValid);
		std::string frame;
		std::string error;
		if (!argumentsValid)
		{
			WriteFrame(ErrorResponse({}, {}, {}, "PREVIEW_ARGUMENTS", "The preview worker arguments are invalid.", "Start the worker with an optional --language path."));
			return WorkerExitBadRequest;
		}
		if (!ReadFrame(frame))
		{
			WriteFrame(ErrorResponse({}, {}, {}, "PREVIEW_FRAME", "The preview request frame is missing, truncated, or exceeds its limit."));
			return WorkerExitProtocol;
		}
		if (!ValidateRequest(frame, request, error))
		{
			WriteFrame(ErrorResponse({}, {}, {}, "PREVIEW_REQUEST", error, "Send a version 1 request with bounded id, revision, operation, and object payload fields."));
			return WorkerExitBadRequest;
		}
		bool compositionReady = false;
		const std::string response = InvokeNative(request, languagePath, compositionReady);
		if (!WriteFrame(response)) return WorkerExitProtocol;
		if (compositionReady)
		{
			PumpCompositionLifetime();
			return 0;
		}
		return 0;
	}
	catch (const std::bad_alloc&)
	{
		WriteFrame(ErrorResponse(request.id, request.revision, request.operation, "PREVIEW_MEMORY", "The preview worker exceeded its bounded memory budget."));
		return WorkerExitProtocol;
	}
	catch (...)
	{
		WriteFrame(ErrorResponse(request.id, request.revision, request.operation, "PREVIEW_INTERNAL", "The preview worker failed without returning a result."));
		return WorkerExitProtocol;
	}
}
