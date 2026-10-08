#include "../../../dll/src/Include/NativeRowSurface.h"

#include <Windows.h>
#include <array>
#include <cstdint>
#include <cstring>
#include <iostream>
#include <stdexcept>

namespace
{
	struct Dib
	{
		HDC dc{};
		HBITMAP bitmap{};
		HGDIOBJ oldBitmap{};
		std::uint8_t *bits{};
		long width{};
		long height{};

		Dib(long widthIn, long heightIn) : width(widthIn), height(heightIn)
		{
			BITMAPINFO info{};
			info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
			info.bmiHeader.biWidth = width;
			info.bmiHeader.biHeight = -height;
			info.bmiHeader.biPlanes = 1;
			info.bmiHeader.biBitCount = 32;
			info.bmiHeader.biCompression = BI_RGB;

			dc = ::CreateCompatibleDC(nullptr);
			if(!dc)
				return;
			bitmap = ::CreateDIBSection(dc, &info, DIB_RGB_COLORS,
				reinterpret_cast<void **>(&bits), nullptr, 0);
			if(!bitmap || !bits)
				return;
			oldBitmap = ::SelectObject(dc, bitmap);
			if(!oldBitmap || oldBitmap == HGDI_ERROR)
			{
				oldBitmap = nullptr;
				return;
			}
		}

		~Dib() noexcept
		{
			if(dc && oldBitmap)
				::SelectObject(dc, oldBitmap);
			if(bitmap)
				::DeleteObject(bitmap);
			if(dc)
				::DeleteDC(dc);
		}

		Dib(const Dib &) = delete;
		Dib &operator=(const Dib &) = delete;

		bool valid() const noexcept
		{
			return dc && bitmap && bits && oldBitmap;
		}

		std::uint8_t *pixel(long x, long y) noexcept
		{
			return bits + (static_cast<std::size_t>(y * width + x) * 4U);
		}
	};

	struct SinkReceipt
	{
		int calls{};
		HDC sinkDC{};
		RECT rect{};
		long width{};
		long height{};
		bool sinkIsMemoryDC{};
		DWORD sinkObjectType{};
		bool copyFailed{};
		std::array<std::uint8_t, 4096> pixels{};
		std::size_t pixelBytes{};
	};

	void CaptureSink(void *context, DRAWITEMSTRUCT *draw,
		const std::uint8_t *pixels, long width, long height) noexcept
	{
		auto *receipt = static_cast<SinkReceipt *>(context);
		if(!receipt)
			return;
		receipt->calls++;
		receipt->sinkDC = draw ? draw->hDC : nullptr;
		receipt->sinkObjectType = draw && draw->hDC
			? ::GetObjectType(draw->hDC) : 0;
		receipt->sinkIsMemoryDC = receipt->sinkObjectType == OBJ_MEMDC;
		if(draw)
			receipt->rect = draw->rcItem;
		receipt->width = width;
		receipt->height = height;
		if(!pixels || width <= 0 || height <= 0)
		{
			receipt->copyFailed = true;
			return;
		}
		const auto bytes = static_cast<std::size_t>(width) *
			static_cast<std::size_t>(height) * 4U;
		if(bytes > receipt->pixels.size())
		{
			receipt->copyFailed = true;
			return;
		}
		receipt->pixelBytes = bytes;
		std::memcpy(receipt->pixels.data(), pixels, bytes);
	}

	void Require(bool condition, const char *message)
	{
		if(!condition)
			throw std::runtime_error(message);
	}

	void PaintSyntheticRow(DRAWITEMSTRUCT *draw, int &calls)
	{
		Require(draw && draw->hDC, "synthetic draw input is invalid");
		++calls;
		auto brush = ::CreateSolidBrush(RGB(0x11, 0xCC, 0x33));
		Require(brush != nullptr, "synthetic brush creation failed");
		::FillRect(draw->hDC, &draw->rcItem, brush);
		::DeleteObject(brush);
	}

	void RequireExcluded(HDC dc, const RECT &row)
	{
		auto region = ::CreateRectRgn(0, 0, 0, 0);
		Require(region != nullptr, "clip region creation failed");
		const auto hasRegion = ::GetClipRgn(dc, region) == 1;
		Require(hasRegion, "marked row exclusion was not transferred");
		Require(::PtInRegion(region, row.left, row.top) == FALSE,
			"marked row remains inside the target clip");
		Require(::PtInRegion(region, 0, 0) != FALSE,
			"row exclusion escaped its nonzero target origin");
		::DeleteObject(region);
	}

	void TestTranslatedPresentation()
	{
		Dib target(20, 20);
		Require(target.valid(), "target DIB creation failed");
		std::memset(target.bits, 0x5A,
			static_cast<std::size_t>(target.width * target.height) * 4U);

		DRAWITEMSTRUCT draw{};
		draw.hDC = target.dc;
		draw.rcItem = {7, 5, 11, 9};
		const auto originalDC = draw.hDC;
		SinkReceipt receipt;
		int callbackCalls = 0;
		{
			Nilesoft::Shell::NativeRowSurface surface(&draw, &receipt,
				&CaptureSink, 1024);
			Require(draw.hDC != originalDC, "row surface did not redirect the callback");
			Require(::GetObjectType(draw.hDC) == OBJ_MEMDC,
				"callback target is not a memory DC");
			PaintSyntheticRow(&draw, callbackCalls);
			surface.mark_excluded();
		}

		Require(callbackCalls == 1, "synthetic owner callback was replayed or skipped");
		Require(receipt.calls == 1, "row sink was not called exactly once");
		Require(!receipt.copyFailed && receipt.pixelBytes == 4U * 4U * 4U,
			"retained row pixels were incomplete");
		Require(receipt.sinkIsMemoryDC && receipt.sinkObjectType == OBJ_MEMDC,
			"sink did not observe the translated row DIB");
		Require(receipt.rect.left == 7 && receipt.rect.top == 5 &&
			receipt.rect.right == 11 && receipt.rect.bottom == 9,
			"sink received the wrong row rectangle");
		Require(draw.hDC == originalDC, "DRAWITEMSTRUCT HDC was not restored");
		Require(receipt.pixels[0] == 0x33 && receipt.pixels[1] == 0xCC &&
			receipt.pixels[2] == 0x11,
			"nonzero row origin was not mapped to DIB origin");
		const auto *presented = target.pixel(7, 5);
		Require(presented[0] == 0x33 && presented[1] == 0xCC &&
			presented[2] == 0x11,
			"translated row was not presented to the target");
		const auto *outside = target.pixel(0, 0);
		Require(outside[0] == 0x5A && outside[1] == 0x5A && outside[2] == 0x5A,
			"presentation overwrote pixels outside the row");
		RequireExcluded(target.dc, draw.rcItem);
	}

	void TestSkipPresentation()
	{
		Dib target(20, 20);
		Require(target.valid(), "skip target DIB creation failed");
		std::memset(target.bits, 0x47,
			static_cast<std::size_t>(target.width * target.height) * 4U);
		std::array<std::uint8_t, 20 * 20 * 4> before{};
		std::memcpy(before.data(), target.bits, before.size());

		DRAWITEMSTRUCT draw{};
		draw.hDC = target.dc;
		draw.rcItem = {6, 4, 10, 8};
		const auto originalDC = draw.hDC;
		SinkReceipt receipt;
		int callbackCalls = 0;
		{
			Nilesoft::Shell::NativeRowSurface surface(&draw, &receipt,
				&CaptureSink, 1024);
			surface.skip_presentation();
			PaintSyntheticRow(&draw, callbackCalls);
			surface.mark_excluded();
		}

		Require(callbackCalls == 1, "skip path did not execute the synthetic callback once");
		Require(receipt.calls == 0, "skip path cached a partial row");
		Require(draw.hDC == originalDC, "skip path did not restore DRAWITEMSTRUCT HDC");
		Require(std::memcmp(before.data(), target.bits, before.size()) == 0,
			"skip path presented a zero or partial DIB to the target");
		RequireExcluded(target.dc, draw.rcItem);
	}

	void TestOversizedFallbackAndCleanup()
	{
		Dib target(20, 20);
		Require(target.valid(), "oversized target DIB creation failed");
		const auto before = ::GetGuiResources(::GetCurrentProcess(), GR_GDIOBJECTS);
		for(int iteration = 0; iteration < 128; ++iteration)
		{
			DRAWITEMSTRUCT draw{};
			draw.hDC = target.dc;
			draw.rcItem = {1, 2, 18, 19};
			SinkReceipt receipt;
			{
				Nilesoft::Shell::NativeRowSurface surface(&draw, &receipt,
					&CaptureSink, 16);
				Require(draw.hDC == target.dc, "oversized row unexpectedly redirected");
			}
			Require(receipt.calls == 0, "oversized fallback called the sink");
			Require(draw.hDC == target.dc, "oversized fallback changed the HDC");
		}
		const auto after = ::GetGuiResources(::GetCurrentProcess(), GR_GDIOBJECTS);
		Require(after <= before + 2, "oversized fallback leaked GDI objects");
	}
}

int main()
{
	try
	{
		TestTranslatedPresentation();
		std::cout << "PASS translated presentation\n";
		TestSkipPresentation();
		std::cout << "PASS skip presentation\n";
		TestOversizedFallbackAndCleanup();
		std::cout << "PASS oversized fallback and cleanup\n";
		std::cout << "3 native row surface cases passed.\n";
		return 0;
	}
	catch(const std::exception &error)
	{
		std::cerr << "FAIL " << error.what() << '\n';
		return 1;
	}
}
