#pragma once

#include <Windows.h>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <limits>

namespace Nilesoft::Shell
{
	// Owns one bounded, top-down row DIB for a native owner-draw callback.
	// The callback keeps its original DRAWITEMSTRUCT coordinates while the
	// viewport maps that row rectangle to the DIB origin.
	class NativeRowSurface final
	{
	public:
		using Sink = void (*)(void *, DRAWITEMSTRUCT *, const uint8_t *,
			long, long) noexcept;

		NativeRowSurface(DRAWITEMSTRUCT *draw, void *context, Sink sink,
			uint64_t maxPixels) noexcept
			: draw_(draw), context_(context), sink_(sink)
		{
			if(!draw_ || !draw_->hDC || !sink_)
				return;

			rect_ = draw_->rcItem;
			width_ = rect_.right - rect_.left;
			height_ = rect_.bottom - rect_.top;
			if(width_ <= 0 || height_ <= 0)
				return;

			const auto pixels = static_cast<uint64_t>(width_) *
				static_cast<uint64_t>(height_);
			if(pixels == 0 || pixels > maxPixels ||
				pixels > static_cast<uint64_t>(
					(std::numeric_limits<size_t>::max)() / sizeof(uint32_t)))
				return;

			const auto byteCount = static_cast<size_t>(pixels) * sizeof(uint32_t);
			BITMAPINFO info{};
			info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
			info.bmiHeader.biWidth = width_;
			info.bmiHeader.biHeight = -height_;
			info.bmiHeader.biPlanes = 1;
			info.bmiHeader.biBitCount = 32;
			info.bmiHeader.biCompression = BI_RGB;

			memory_ = ::CreateCompatibleDC(draw_->hDC);
			if(!memory_)
				return;
			bitmap_ = ::CreateDIBSection(draw_->hDC, &info, DIB_RGB_COLORS,
				reinterpret_cast<void **>(&bits_), nullptr, 0);
			if(!bitmap_ || !bits_)
			{
				cleanup();
				return;
			}
			oldBitmap_ = ::SelectObject(memory_, bitmap_);
			if(!oldBitmap_ || oldBitmap_ == HGDI_ERROR)
			{
				cleanup();
				return;
			}
			std::memset(bits_, 0, byteCount);

			saved_ = ::SaveDC(memory_);
			if(saved_ == 0 || !::SetViewportOrgEx(memory_, -rect_.left,
				-rect_.top, nullptr))
			{
				cleanup();
				return;
			}

			original_ = draw_->hDC;
			draw_->hDC = memory_;
			active_ = true;
		}

		~NativeRowSurface() noexcept
		{
			if(active_)
			{
				if(!skipPresentation_)
				{
					// A DIB section can still contain queued GDI operations when
					// the owner callback returns.  Flush before handing pixels to
					// the capture cache or presenting them to the menu DC.
					::GdiFlush();
					if(sink_)
						sink_(context_, draw_, bits_, width_, height_);
				}

				// The owner callback has finished.  Present the complete row with
				// the temporary viewport and clipping state removed.
				if(saved_ != 0)
				{
					::SetViewportOrgEx(memory_, 0, 0, nullptr);
					::SelectClipRgn(memory_, nullptr);
				}
				if(!skipPresentation_)
					::BitBlt(original_, rect_.left, rect_.top, width_, height_,
						memory_, 0, 0, SRCCOPY);
				if(excluded_)
					::ExcludeClipRect(original_, rect_.left, rect_.top,
						rect_.right, rect_.bottom);

				draw_->hDC = original_;
			}
			cleanup();
		}

		NativeRowSurface(const NativeRowSurface &) = delete;
		NativeRowSurface &operator=(const NativeRowSurface &) = delete;

		void mark_excluded() noexcept { excluded_ = active_; }
		void skip_presentation() noexcept { skipPresentation_ = true; }

	private:
		void cleanup() noexcept
		{
			if(draw_ && active_)
				draw_->hDC = original_;
			if(memory_ && saved_ != 0)
				::RestoreDC(memory_, saved_);
			if(memory_ && oldBitmap_ && oldBitmap_ != HGDI_ERROR)
				::SelectObject(memory_, oldBitmap_);
			if(bitmap_)
				::DeleteObject(bitmap_);
			if(memory_)
				::DeleteDC(memory_);
			active_ = false;
			memory_ = nullptr;
			bitmap_ = nullptr;
			oldBitmap_ = nullptr;
			bits_ = nullptr;
			saved_ = 0;
		}

		DRAWITEMSTRUCT *draw_{};
		void *context_{};
		Sink sink_{};
		HDC original_{};
		HDC memory_{};
		HBITMAP bitmap_{};
		HGDIOBJ oldBitmap_{};
		uint8_t *bits_{};
		RECT rect_{};
		long width_{};
		long height_{};
		int saved_{};
		bool active_{};
		bool excluded_{};
		bool skipPresentation_{};
	};
}
