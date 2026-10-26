//! Off-screen Slint rendering for the dev sheet tools: a platform whose one window is drawn by
//! the software renderer, so a tool can draw the settings UI to a PNG without opening a window
//! (nothing pops up or takes focus on the desktop).

use std::rc::Rc;
use std::time::Duration;

use slint::platform::software_renderer::{MinimalSoftwareWindow, RepaintBufferType};
use slint::platform::{Platform, WindowAdapter, WindowEvent};
use slint::PhysicalSize;
use windows::core::{Result, HSTRING};
use windows::Win32::Foundation::GENERIC_WRITE;
use windows::Win32::Graphics::Imaging::*;
use windows::Win32::System::Com::{CoCreateInstance, CoInitializeEx, CLSCTX_INPROC_SERVER, COINIT_MULTITHREADED};

struct Offscreen {
    window: Rc<MinimalSoftwareWindow>,
}

impl Platform for Offscreen {
    fn create_window_adapter(&self) -> std::result::Result<Rc<dyn WindowAdapter>, slint::PlatformError> {
        Ok(self.window.clone())
    }
}

/// Installs the off-screen platform (call before creating any component). `scale` is the
/// device pixel ratio the UI is drawn at (1.25 on a laptop at 125 %).
pub fn install(scale: f32) -> Rc<MinimalSoftwareWindow> {
    let window = MinimalSoftwareWindow::new(RepaintBufferType::NewBuffer);
    window.dispatch_event(WindowEvent::ScaleFactorChanged { scale_factor: scale });
    slint::platform::set_platform(Box::new(Offscreen { window: window.clone() })).expect("Slint platform already set");
    window
}

/// Sizes the window to `w`×`h` logical pixels at `scale`, lets timers and animations run for
/// `settle`, and writes what it shows to `path` as a PNG.
pub fn shot(window: &MinimalSoftwareWindow, w: f32, h: f32, scale: f32, settle: Duration, path: &str) -> Result<()> {
    window.set_size(PhysicalSize::new((w * scale).round() as u32, (h * scale).round() as u32));
    let until = std::time::Instant::now() + settle;
    loop {
        slint::platform::update_timers_and_animations();
        window.request_redraw();
        if std::time::Instant::now() >= until {
            break;
        }
        std::thread::sleep(Duration::from_millis(16));
    }
    let snap = window.take_snapshot().map_err(|_| windows::core::Error::from(windows::Win32::Foundation::E_FAIL))?;
    let (pw, ph) = (snap.width(), snap.height());
    let mut bgra = Vec::with_capacity((pw * ph * 4) as usize);
    for p in snap.as_slice() {
        bgra.extend_from_slice(&[p.b, p.g, p.r, p.a]);
    }
    save_png(path, pw, ph, &bgra)
}

/// Writes straight-alpha BGRA pixels to a PNG with WIC.
pub fn save_png(path: &str, w: u32, h: u32, bgra: &[u8]) -> Result<()> {
    unsafe {
        let _ = CoInitializeEx(None, COINIT_MULTITHREADED);
        let wic: IWICImagingFactory = CoCreateInstance(&CLSID_WICImagingFactory, None, CLSCTX_INPROC_SERVER)?;
        let stream = wic.CreateStream()?;
        stream.InitializeFromFilename(&HSTRING::from(path), GENERIC_WRITE.0)?;
        let encoder = wic.CreateEncoder(&GUID_ContainerFormatPng, std::ptr::null())?;
        encoder.Initialize(&stream, WICBitmapEncoderNoCache)?;
        let mut frame = None;
        encoder.CreateNewFrame(&mut frame, std::ptr::null_mut())?;
        let frame = frame.expect("png frame");
        frame.Initialize(None)?;
        frame.SetSize(w, h)?;
        let mut format = GUID_WICPixelFormat32bppBGRA;
        frame.SetPixelFormat(&mut format)?;
        frame.WritePixels(h, w * 4, bgra)?;
        frame.Commit()?;
        encoder.Commit()
    }
}
