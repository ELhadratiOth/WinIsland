//! Painting off-screen, into pixels: the setup's stage draws real pills this way (as the dev
//! sheets do, minus the PNG). COM must be initialised on the calling thread.

use windows::core::{Interface, Result};
use windows::Win32::Graphics::Direct2D::Common::{D2D1_ALPHA_MODE_PREMULTIPLIED, D2D1_COLOR_F, D2D1_PIXEL_FORMAT};
use windows::Win32::Graphics::Direct2D::*;
use windows::Win32::Graphics::Dxgi::Common::DXGI_FORMAT_B8G8R8A8_UNORM;
use windows::Win32::Graphics::Imaging::*;
use windows::Win32::System::Com::{CoCreateInstance, CLSCTX_INPROC_SERVER};

use super::gfx::{Ink, TextKit};

/// `w` × `h` premultiplied BGRA pixels, cleared to `clear` (straight RGBA, 0–1), then `paint`ed.
pub fn render(w: u32, h: u32, clear: [f32; 4], paint: impl FnOnce(&Ink) -> Result<()>) -> Result<Vec<u8>> {
    let text = TextKit::new()?;
    unsafe {
        let wic: IWICImagingFactory = CoCreateInstance(&CLSID_WICImagingFactory, None, CLSCTX_INPROC_SERVER)?;
        let bmp = wic.CreateBitmap(w, h, &GUID_WICPixelFormat32bppPBGRA, WICBitmapCacheOnLoad)?;
        // Factory7: its device contexts draw SVG (the Buds mode's icon).
        let d2f: ID2D1Factory7 = D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, None)?;
        let props = D2D1_RENDER_TARGET_PROPERTIES {
            pixelFormat: D2D1_PIXEL_FORMAT { format: DXGI_FORMAT_B8G8R8A8_UNORM, alphaMode: D2D1_ALPHA_MODE_PREMULTIPLIED },
            dpiX: 96.0,
            dpiY: 96.0,
            ..Default::default()
        };
        let rt = d2f.CreateWicBitmapRenderTarget(&bmp, &props)?;
        let dc: ID2D1DeviceContext = rt.cast()?;
        dc.BeginDraw();
        let [r, g, b, a] = clear;
        dc.Clear(Some(&D2D1_COLOR_F { r, g, b, a }));
        dc.SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);
        dc.SetTextRenderingParams(&text.params);
        let painted = paint(&Ink { dc: &dc, text: &text });
        dc.EndDraw(None, None)?;
        painted?;
        let mut px = vec![0u8; (w * h * 4) as usize];
        bmp.CopyPixels(std::ptr::null(), w * 4, &mut px)?;
        Ok(px)
    }
}
