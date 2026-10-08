#pragma once

#include <cstddef>
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
	class NativeMenu;

	// Trace entries are intentionally plain text because the managed Studio
	// contract exposes them as an ordered list of explanations.  The native
	// evaluator appends entries at the point where a condition is evaluated;
	// the serializer only transfers the bounded list and never re-evaluates a
	// rule after the menu has been built.
	using StudioCaptureTrace = std::vector<std::wstring>;

	// Structured evidence is collected at the same evaluation sites that feed
	// the human-readable trace.  NativeMenu is forward declared here so the
	// source object remains owned by the parsed cache for the lifetime of a
	// capture; the serializer copies only bounded source identity fields.
	struct StudioCaptureRuleOutcome
	{
		const NativeMenu *source = nullptr;
		std::string ruleId;
		std::string outcome = "unknown";
		std::wstring reason;
	};

	struct StudioCapturePropertyEffect
	{
		const NativeMenu *source = nullptr;
		std::string property;
		std::string effect = "unknown";
		std::wstring value;
	};

	struct StudioCaptureEvidence
	{
		static constexpr uint32_t Version = 1;
		// Keep one explicit bound for both evidence arrays.  The producer marks
		// the evidence as truncated as soon as the bound is reached so a later
		// serializer cannot silently present a partial ledger as complete.
		static constexpr uint32_t MaxItems = 128;
		std::vector<StudioCaptureRuleOutcome> ruleOutcomes;
		std::vector<StudioCapturePropertyEffect> propertyEffects;
		bool truncated = false;
		uint32_t messageLimit = 0;

		bool empty() const noexcept
		{
			return ruleOutcomes.empty() && propertyEffects.empty() && !truncated;
		}
	};

	struct StudioCaptureCompleteness
	{
		std::string state = "unavailable";
		bool childrenCaptured = false;
		bool complete = false;
		// A nonzero value is the configured bound that was reached.  Zero means
		// that this limit did not contribute to the branch result.  The wire
		// serializer omits zero values so managed BranchCompleteness receives its
		// nullable integer contract instead of a bool-shaped approximation.
		uint32_t depthLimit = 0;
		uint32_t itemLimit = 0;
		uint32_t messageLimit = 0;
		uint32_t providerLimit = 0;
		uint32_t evaluationLimit = 0;
		std::vector<std::wstring> diagnostics;
	};

	/// The immutable selection state used to build a captured menu.  Studio
	/// receives this alongside the semantic menu tree so a preview can reuse
	/// the native selection predicates without querying the preview machine.
	struct StudioCaptureSelectionItem
	{
		std::wstring path;
		std::wstring raw;
		std::wstring name;
		std::wstring title;
		std::wstring extension;
		int32_t type = -1;
		int32_t group = -1;
		bool readOnly = false;
		bool hidden = false;
		bool isLink = false;
	};

	struct StudioCaptureSelection
	{
		static constexpr uint32_t Version = 1;
		bool background = false;
		int32_t windowId = 0;
		int32_t mode = 0;
		// This is the native FSO index selected by the runtime, not an item index.
		int32_t front = -1;
		bool windowDesktop = false;
		bool windowExplorer = false;
		bool windowExplorerTree = false;
		std::wstring parent;
		std::wstring parentRaw;
		std::wstring directory;
		std::vector<int32_t> types;
		std::vector<StudioCaptureSelectionItem> items;
	};

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
		StudioCaptureSelection selection;
		// Effective gates are captured after native settings expressions have
		// been evaluated.  The JSON fragments are intentionally opaque to the
		// native transport and are emitted only when evidence is available.
		bool hasEffectiveSettings = false;
		bool modifyItemsEnabled = true;
		bool modifyItemsTitle = true;
		bool modifyItemsVisibility = true;
		bool modifyItemsParent = true;
		bool modifyItemsSeparator = true;
		bool modifyItemsKeys = true;
		int32_t modifyItemsImage = 1;
		int32_t modifyItemsPosition = 1;
		bool removeDuplicate = false;
		bool removeDisabled = false;
		bool removeSeparator = false;
		bool newItemsEnabled = true;
		bool newItemsImage = true;
		bool newItemsKeys = true;
		std::vector<std::string> effectiveSettingDiagnostics;
	};

	/// Pixels from the bounded native-rendered rectangle of one popup.  The
	/// bitmap is top-down premultiplied BGRA32; it never contains an HWND, HDC,
	/// or desktop coordinates.  An unavailable appearance carries no pixel data
	/// while the semantic final snapshot remains usable.
	struct StudioCaptureAppearanceRow
	{
		std::string entryId;
		uint32_t x = 0;
		uint32_t y = 0;
		uint32_t width = 0;
		uint32_t height = 0;
	};

	struct StudioCaptureAppearance
	{
		static constexpr uint32_t Version = 2;
		static constexpr std::string_view Source = "native-renderer";
		static constexpr std::string_view AlphaMode = "premultiplied";
		bool available = false;
		uint32_t width = 0;
		uint32_t height = 0;
		uint32_t dpi = 0;
		bool desktopEffectsOmitted = false;
		std::string unavailableReason;
		std::vector<uint8_t> pixels;
		std::vector<StudioCaptureAppearanceRow> rows;
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
		static constexpr size_t MaxSelectionItems = 4096;
		// Posted to the owning window after a valid capture.start handshake.
		// wParam identifies this StudioCapture and lParam carries the active epoch
		// so a queued notification from an earlier context cannot arm a later one.
		static constexpr UINT CaptureArmedMessage = WM_APP + 0x3A6;
		// Posted when the currently armed capture is retired.  wParam identifies
		// this StudioCapture and lParam carries the retired epoch so the owning
		// context can discard only matching cached appearance data.
		static constexpr UINT CaptureRetiredMessage = WM_APP + 0x3A7;

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
		/// Returns the identity of the currently armed capture, or zero when the
		/// capture is inactive.  Epochs are never reset across reconnects.
		uint64_t ActiveEpoch() const;
		bool WantsOriginal() const;
		/// Terminates the active capture with a structured error message.
		/// No-op when no authenticated capture is active.
		void Fail(std::string_view code, std::string_view message) noexcept;
		/// Reports an error only if expectedEpoch still identifies the active
		/// capture.  A stale publisher cannot retire a newer reconnect.
		void FailIfEpoch(uint64_t expectedEpoch, std::string_view code,
			std::string_view message) noexcept;

		/// Publishes the original system menu tree, if a Studio request is
		/// active or arrives before this context is destroyed.
		bool PublishOriginal(const menuitem_t *root, const StudioCaptureMetadata &metadata);

		/// Publishes the final displayed entries for one popup.  parentPath is
		/// empty for the root menu and identifies lazily opened submenus.
		bool PublishFinal(const std::vector<MenuItemInfo *> &entries,
			const StudioCaptureMetadata &metadata,
			std::wstring_view parentPath = {});

		/// Publishes a post-paint final snapshot with an optional exact popup
		/// appearance.  expectedEpoch must be the nonzero value returned by
		/// ActiveEpoch before composing the appearance; publication is dropped if
		/// the capture retires or reconnects while composition is in progress.  The
		/// semantic entries remain serialized by the same path as PublishFinal; an
		/// unavailable appearance never drops that snapshot.
		bool PublishFinal(const std::vector<MenuItemInfo *> &entries,
			const StudioCaptureMetadata &metadata,
			std::wstring_view parentPath,
			const StudioCaptureAppearance &appearance,
			uint64_t expectedEpoch);

		/// Builds the id used by SerializeFinal for one displayed entry.  Keeping
		/// this helper shared lets the post-paint row hit rectangles refer to the
		/// exact ids already present in the semantic entries array.
		static std::string FinalEntryId(const MenuItemInfo *item,
			std::wstring_view inheritedParent, size_t index);

#ifdef STUDIO_CAPTURE_SERIALIZATION_TESTS
		// Test-only accessors keep serializer tests independent of the pipe worker.
		// The production header does not expose these entry points.
		static std::string SerializeOriginalForTesting(const menuitem_t *root,
			const StudioCaptureMetadata &metadata);
		static std::string SerializeFinalForTesting(
			const std::vector<MenuItemInfo *> &entries,
			const StudioCaptureMetadata &metadata, std::wstring_view parentPath);
#endif

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
		uint64_t activeEpoch_ = 0;
		std::string captureId_;
		std::deque<Outbound> outbound_;

		void Run() noexcept;
		void HandleClient(HANDLE pipe) noexcept;

		bool IsStopping() const;
		void SetPipe(HANDLE pipe);
		void ClearConnection();
		void RetireLocked() noexcept;
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
		static std::string SerializeFinal(const std::vector<MenuItemInfo *> &entries,
			const StudioCaptureMetadata &metadata, std::wstring_view parentPath,
			const StudioCaptureAppearance &appearance);
	};
}
