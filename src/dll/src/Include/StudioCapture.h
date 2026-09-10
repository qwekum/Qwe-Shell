#pragma once

#include <condition_variable>
#include <cstdint>
#include <deque>
#include <mutex>
#include <string>
#include <string_view>
#include <thread>
#include <vector>
#include <Windows.h>

namespace Nilesoft::Shell
{
	struct menuitem_t;
	struct MenuItemInfo;

	// Trace entries are intentionally plain text because the managed Studio
	// contract exposes them as an ordered list of explanations.  The native
	// evaluator appends entries at the point where a condition is evaluated;
	// the serializer only transfers the bounded list and never re-evaluates a
	// rule after the menu has been built.
	using StudioCaptureTrace = std::vector<std::wstring>;

	/// Metadata attached to a menu snapshot.  The native side only keeps this
	/// data in memory long enough to transfer it to an authenticated Studio
	/// client; it is never written to disk.
	struct StudioCaptureMetadata
	{
		std::wstring configPath;
		std::wstring context;
		std::wstring contextCategory;
		// The generation loaded by the native cache that produced this snapshot.
		// An empty value is retained for installations predating the generation
		// marker; Studio must treat that capture as unverified after an apply.
		std::wstring runtimeGeneration;
		std::vector<std::wstring> paths;
	};

	/// Bounded, opt-in IPC publisher for actual Shell menu snapshots.
	///
	/// A service belongs to one ContextMenu instance.  It is deliberately
	/// started outside DllMain and stopped before that instance releases its
	/// native menu state.  Studio owns the pipe server; the Explorer/UI thread
	/// only serializes bounded data and appends it to a small queue.  All pipe
	/// I/O happens on the worker thread.
	class StudioCapture final
	{
	public:
		static constexpr uint32_t ProtocolVersion = 1;
		static constexpr uint32_t MaxMessageBytes = 4U * 1024U * 1024U;
		// Posted to the owning window after a valid capture.start handshake.
		// The message carries this StudioCapture address in wParam so a queued
		// notification from an earlier context cannot arm a later one.
		static constexpr UINT CaptureArmedMessage = WM_APP + 0x3A6;

		StudioCapture() = default;
		StudioCapture(const StudioCapture &) = delete;
		StudioCapture &operator=(const StudioCapture &) = delete;
		~StudioCapture();

		StudioCapture(StudioCapture &&) = delete;
		StudioCapture &operator=(StudioCapture &&) = delete;

		/// Starts the per-context named-pipe listener.  This is safe to call
		/// more than once and does not wait for a Studio client.
		bool Start(HWND notificationWindow = nullptr);
		void Stop() noexcept;

		/// Returns true only after a valid Studio request has armed this context.
		/// The menu thread uses this gate before doing any original-tree work or
		/// constructing capture metadata.
		bool IsActive() const;
		bool WantsOriginal() const;
		/// Terminates the active capture with a structured error message.
		/// No-op when no authenticated capture is active.
		void Fail(std::string_view code, std::string_view message) noexcept;

		/// Publishes the original system menu tree, if a Studio request is
		/// active or arrives before this context is destroyed.
		bool PublishOriginal(const menuitem_t *root, const StudioCaptureMetadata &metadata);

		/// Publishes the final displayed entries for one popup.  parentPath is
		/// empty for the root menu and identifies lazily opened submenus.
		bool PublishFinal(const std::vector<MenuItemInfo *> &entries,
			const StudioCaptureMetadata &metadata,
			std::wstring_view parentPath = {});

	private:
		struct Outbound
		{
			std::string payload;
		};

		struct Request
		{
			enum class Kind
			{
				Start,
				Cancel,
				End
			} kind = Kind::Start;
			std::string captureId;
			uint32_t sessionId = 0;
			bool includeOriginal = true;
		};

		mutable std::mutex mutex_;
		std::condition_variable changed_;
		std::thread worker_;
		HANDLE stopEvent_ = nullptr;
		HANDLE pipe_ = INVALID_HANDLE_VALUE;
		std::wstring pipeName_;
		HWND notificationWindow_ = nullptr;
		uint32_t sessionId_ = 0;
		bool stopping_ = false;
		bool connected_ = false;
		bool active_ = false;
		bool failed_ = false;
		bool includeOriginal_ = true;
		std::string captureId_;
		std::deque<Outbound> outbound_;

		void Run() noexcept;
		void HandleClient(HANDLE pipe) noexcept;

		bool IsStopping() const;
		void SetPipe(HANDLE pipe);
		void ClearConnection();
		void FailLocked(std::string_view code, std::string_view message) noexcept;
		bool EnqueueLocked(std::string payload) noexcept;

		static std::wstring BuildPipeName(uint32_t &sessionId);
		static HANDLE ConnectToServer(const std::wstring &name, HANDLE stopEvent);
		static bool ValidatePeerUser(HANDLE pipe);
		static bool ReadFrame(HANDLE pipe, HANDLE stopEvent, std::string &payload);
		static bool WriteFrame(HANDLE pipe, HANDLE stopEvent, std::string_view payload);
		static bool ReadExact(HANDLE pipe, HANDLE stopEvent, void *buffer, size_t size);
		static bool WriteExact(HANDLE pipe, HANDLE stopEvent, const void *buffer, size_t size);

		static bool ParseRequest(std::string_view payload, uint32_t expectedSession,
			Request &request);
		static std::string ErrorMessage(std::string_view code, std::string_view message);
		static std::string ReadyMessage(std::string_view captureId, uint32_t sessionId);
		static std::string EndMessage(std::string_view captureId, std::string_view reason);
		static std::string SnapshotMessage(std::string_view captureId,
			std::string_view phase, std::string snapshot);

		static std::string SerializeOriginal(const menuitem_t *root,
			const StudioCaptureMetadata &metadata);
		static std::string SerializeFinal(const std::vector<MenuItemInfo *> &entries,
			const StudioCaptureMetadata &metadata, std::wstring_view parentPath);
	};
}
