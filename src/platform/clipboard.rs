//! Copying a shelf item: the file itself (Explorer pastes it), and what it shows where an app
//! takes that instead: an image's picture (chat apps, Paint, Office) or a text file's text.

use std::path::Path;

use windows::core::{Result, HSTRING};
use windows::Win32::Foundation::{GlobalFree, HANDLE, HGLOBAL};
use windows::Win32::Graphics::Imaging::*;
use windows::Win32::System::Com::{CoCreateInstance, CLSCTX_INPROC_SERVER};
use windows::Win32::System::DataExchange::{CloseClipboard, EmptyClipboard, OpenClipboard, SetClipboardData};
use windows::Win32::System::Memory::{GlobalAlloc, GlobalLock, GlobalUnlock, GMEM_MOVEABLE};
use windows::Win32::System::Ole::{CF_DIB, CF_HDROP, CF_UNICODETEXT};
use windows::Win32::UI::Shell::DROPFILES;

use crate::activities::shelf::FileKind;

/// Text files up to this size are copied as their text too.
const TEXT_MAX_BYTES: u64 = 2 << 20;

/// What goes on the clipboard for a file of `kind`: (the picture, the text) besides the file.
pub fn formats_for(kind: FileKind, name: &str) -> (bool, bool) {
    let lower = name.to_lowercase();
    let picture = kind == FileKind::Image && !lower.ends_with(".svg");
    let text = matches!(kind, FileKind::Code) || [".txt", ".md", ".csv", ".log", ".svg"].iter().any(|e| lower.ends_with(e));
    (picture, text)
}

/// `bytes` in a movable global block, as the clipboard takes it.
fn global(bytes: &[u8]) -> Result<HGLOBAL> {
    unsafe {
        let block = GlobalAlloc(GMEM_MOVEABLE, bytes.len())?;
        let p = GlobalLock(block) as *mut u8;
        if p.is_null() {
            let _ = GlobalFree(Some(block));
            return Err(windows::core::Error::from_thread());
        }
        std::ptr::copy_nonoverlapping(bytes.as_ptr(), p, bytes.len());
        let _ = GlobalUnlock(block);
        Ok(block)
    }
}

/// CF_HDROP's bytes: a DROPFILES header, then the path in UTF-16, ending in two NULs.
pub fn drop_files(path: &Path) -> Vec<u8> {
    let header = DROPFILES { pFiles: std::mem::size_of::<DROPFILES>() as u32, fWide: true.into(), ..Default::default() };
    let mut bytes = unsafe { std::slice::from_raw_parts(&header as *const DROPFILES as *const u8, std::mem::size_of::<DROPFILES>()) }.to_vec();
    for unit in path.as_os_str().to_string_lossy().encode_utf16().chain([0, 0]) {
        bytes.extend_from_slice(&unit.to_le_bytes());
    }
    bytes
}

/// CF_DIB's bytes for top-down BGRA pixels: a BITMAPINFOHEADER, then the rows bottom-up (what
/// every app reads).
pub fn dib(w: u32, h: u32, bgra: &[u8]) -> Vec<u8> {
    let mut bytes = Vec::with_capacity(40 + bgra.len());
    for v in [40u32, w, h, 1 | (32 << 16), 0, (w * h * 4), 2835, 2835, 0, 0] {
        bytes.extend_from_slice(&v.to_le_bytes());
    }
    let row = (w * 4) as usize;
    for r in (0..h as usize).rev() {
        bytes.extend_from_slice(&bgra[r * row..(r + 1) * row]);
    }
    bytes
}

/// The image at `path` as top-down BGRA pixels (WIC: anything Windows can open).
fn decode(path: &Path) -> Result<(u32, u32, Vec<u8>)> {
    unsafe {
        let wic: IWICImagingFactory = CoCreateInstance(&CLSID_WICImagingFactory, None, CLSCTX_INPROC_SERVER)?;
        let decoder = wic.CreateDecoderFromFilename(&HSTRING::from(path.as_os_str()), None, windows::Win32::Foundation::GENERIC_READ, WICDecodeMetadataCacheOnDemand)?;
        let frame = decoder.GetFrame(0)?;
        let converter = wic.CreateFormatConverter()?;
        converter.Initialize(&frame, &GUID_WICPixelFormat32bppBGRA, WICBitmapDitherTypeNone, None, 0.0, WICBitmapPaletteTypeCustom)?;
        let (mut w, mut h) = (0, 0);
        converter.GetSize(&mut w, &mut h)?;
        let mut px = vec![0u8; (w * h * 4) as usize];
        converter.CopyPixels(std::ptr::null(), w * 4, &mut px)?;
        Ok((w, h, px))
    }
}

/// Puts the file at `path` on the clipboard, with its picture or its text when it has one.
/// COM must be initialised on the calling thread (for the picture).
pub fn copy_file(path: &Path, kind: FileKind) -> Result<()> {
    let name = path.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
    let (picture, text) = formats_for(kind, &name);
    // Read before the clipboard is opened: it stays shut for no longer than it must.
    let picture = picture.then(|| decode(path).ok()).flatten().map(|(w, h, px)| dib(w, h, &px));
    let text = text
        .then(|| std::fs::metadata(path).ok().filter(|m| m.len() <= TEXT_MAX_BYTES).and_then(|_| std::fs::read_to_string(path).ok()))
        .flatten()
        .map(|t| t.trim_start_matches('\u{feff}').encode_utf16().chain([0]).flat_map(u16::to_le_bytes).collect::<Vec<u8>>());
    unsafe {
        OpenClipboard(None)?;
        let put = (|| -> Result<()> {
            EmptyClipboard()?;
            SetClipboardData(CF_HDROP.0 as u32, Some(HANDLE(global(&drop_files(path))?.0)))?;
            if let Some(bytes) = &picture {
                SetClipboardData(CF_DIB.0 as u32, Some(HANDLE(global(bytes)?.0)))?;
            }
            if let Some(bytes) = &text {
                SetClipboardData(CF_UNICODETEXT.0 as u32, Some(HANDLE(global(bytes)?.0)))?;
            }
            Ok(())
        })();
        let _ = CloseClipboard();
        put
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn what_each_kind_copies() {
        assert_eq!(formats_for(FileKind::Image, "shot.png"), (true, false), "a picture");
        assert_eq!(formats_for(FileKind::Image, "logo.svg"), (false, true), "an SVG is text");
        assert_eq!(formats_for(FileKind::Code, "main.rs"), (false, true));
        assert_eq!(formats_for(FileKind::Document, "notes.TXT"), (false, true));
        assert_eq!(formats_for(FileKind::Document, "report.docx"), (false, false), "the file alone");
        assert_eq!(formats_for(FileKind::Pdf, "a.pdf"), (false, false));
    }

    #[test]
    fn a_dib_is_bottom_up_behind_its_header() {
        // Two rows: red on top, blue under it.
        let px = [0, 0, 255, 255, 255, 0, 0, 255];
        let d = dib(1, 2, &px);
        assert_eq!(d.len(), 40 + 8);
        assert_eq!(&d[..4], &40u32.to_le_bytes());
        assert_eq!(&d[40..44], &[255, 0, 0, 255], "the bottom row comes first");
    }

    /// On a desktop (the smoke test runs it): a picture and a text file go on the clipboard as
    /// what they show, and both as the file. `cargo test --lib platform::clipboard -- --ignored`
    #[test]
    #[ignore]
    fn copies_reach_the_clipboard() {
        use windows::Win32::System::DataExchange::IsClipboardFormatAvailable;
        unsafe {
            let _ = windows::Win32::System::Com::CoInitializeEx(None, windows::Win32::System::Com::COINIT_MULTITHREADED);
        }
        let dir = std::env::temp_dir().join(format!("winisland-clip-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let png = dir.join("dot.png");
        crate::slint_shot::save_png(png.to_str().unwrap(), 2, 2, &[255; 16]).unwrap();
        copy_file(&png, FileKind::Image).expect("copied");
        let has = |f: u16| unsafe { IsClipboardFormatAvailable(f as u32) }.is_ok();
        assert!(has(CF_DIB.0) && has(CF_HDROP.0), "the picture and the file");
        let txt = dir.join("note.txt");
        std::fs::write(&txt, "Hello from the shelf").unwrap();
        copy_file(&txt, FileKind::Document).expect("copied");
        assert_eq!(crate::platform::keys::clipboard_text().as_deref(), Some("Hello from the shelf"));
        assert!(has(CF_HDROP.0) && !has(CF_DIB.0), "the text and the file");
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn hdrop_ends_in_two_nuls() {
        let b = drop_files(Path::new(r"C:\a.png"));
        assert_eq!(&b[..4], &(std::mem::size_of::<DROPFILES>() as u32).to_le_bytes());
        assert_eq!(&b[b.len() - 4..], &[0, 0, 0, 0]);
    }
}
