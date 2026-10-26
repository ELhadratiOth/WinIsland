//! Files dragged onto the pill or the card (the shelf), and dragged back out.
//! Copy only, both ways: a `MOVE` would let the other side delete the original.

use std::cell::{Cell, RefCell};
use std::ffi::OsString;
use std::os::windows::ffi::OsStringExt;
use std::path::PathBuf;

use windows::core::{implement, Ref, Result, HSTRING};
use windows::Win32::Foundation::{HWND, POINTL};
use windows::Win32::System::Com::{CoTaskMemFree, IDataObject, DVASPECT_CONTENT, FORMATETC, TYMED_HGLOBAL};
use windows::Win32::System::Ole::{
    IDropSource, IDropTarget, IDropTarget_Impl, OleInitialize, RegisterDragDrop, ReleaseStgMedium, RevokeDragDrop, CF_HDROP, DROPEFFECT, DROPEFFECT_COPY,
    DROPEFFECT_NONE,
};
use windows::Win32::System::SystemServices::MODIFIERKEYS_FLAGS;
use windows::Win32::UI::Input::KeyboardAndMouse::ReleaseCapture;
use windows::Win32::UI::Shell::Common::ITEMIDLIST;
use windows::Win32::UI::Shell::{DragQueryFileW, SHCreateDataObject, SHDoDragDrop, SHParseDisplayName, HDROP};
use windows::Win32::UI::WindowsAndMessaging::{GetSystemMetrics, SM_CXDRAG, SM_CYDRAG};

use super::windows::{push_event, UiEvent};

/// Which window a drag is over.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum DropZone {
    Pill,
    Card,
}

thread_local! {
    static HOVER: Cell<Option<DropZone>> = const { Cell::new(None) };
    static DROPPED: RefCell<Vec<PathBuf>> = const { RefCell::new(Vec::new()) };
    /// A drag out of the shelf is running: its files landing on the pill or card are ours.
    static OUTGOING: Cell<bool> = const { Cell::new(false) };
}

/// OLE on the UI thread (already an STA, so this only adds OLE's drag and drop).
pub fn init() -> Result<()> {
    unsafe { OleInitialize(None) }
}

/// Makes `hwnd` take file drops; its zone is what `hover()` reports.
pub fn register(hwnd: HWND, zone: DropZone) -> Result<()> {
    let target: IDropTarget = DropTarget { zone, accepts: Cell::new(false) }.into();
    unsafe { RegisterDragDrop(hwnd, &target) }
}

/// Lets go of `hwnd`'s drop target before the window goes (a no-op if it never had one).
pub fn revoke(hwnd: HWND) {
    let _ = unsafe { RevokeDragDrop(hwnd) };
}

/// Where an accepted file drag is right now.
pub fn hover() -> Option<DropZone> {
    HOVER.with(|h| h.get())
}

/// The paths dropped since the last call, in drop order.
pub fn take_dropped() -> Vec<PathBuf> {
    DROPPED.with(|d| std::mem::take(&mut *d.borrow_mut()))
}

/// Past the system's drag distance on either axis (the mouse went down `dx`, `dy` px ago).
pub fn drag_moved(dx: i32, dy: i32) -> bool {
    let (cx, cy) = unsafe { (GetSystemMetrics(SM_CXDRAG), GetSystemMetrics(SM_CYDRAG)) };
    dx.abs() >= cx || dy.abs() >= cy
}

/// Drags `paths` out of the shelf, copy only; returns once they are dropped somewhere (or the
/// drag is cancelled). Paths that no longer parse are left out.
pub fn drag_out(paths: &[PathBuf]) -> Result<()> {
    let pidls: Vec<*mut ITEMIDLIST> = paths
        .iter()
        .filter_map(|p| {
            let mut pidl = std::ptr::null_mut();
            unsafe { SHParseDisplayName(&HSTRING::from(p.as_os_str()), None, &mut pidl, 0, None) }.ok().map(|_| pidl)
        })
        .collect();
    let result = (|| {
        if pidls.is_empty() {
            return Ok(());
        }
        let items: Vec<*const ITEMIDLIST> = pidls.iter().map(|p| *p as *const _).collect();
        unsafe {
            // Full PIDLs under the desktop, so files from different folders go together.
            let data: IDataObject = SHCreateDataObject(None, Some(&items), None)?;
            let _ = ReleaseCapture();
            OUTGOING.with(|o| o.set(true));
            let done = SHDoDragDrop(None, &data, None::<&IDropSource>, DROPEFFECT_COPY).map(|_| ());
            OUTGOING.with(|o| o.set(false));
            done
        }
    })();
    for pidl in pidls {
        unsafe { CoTaskMemFree(Some(pidl as *const _)) };
    }
    result
}

/// What a drag over the pill or card gets: a copy of files from elsewhere, offered as a copy;
/// nothing for text, a move-only drag or the shelf's own drag coming back (no hint, no drop).
pub fn drop_effect(has_files: bool, offered: DROPEFFECT, outgoing: bool) -> DROPEFFECT {
    if has_files && !outgoing {
        allowed_effect(offered)
    } else {
        DROPEFFECT_NONE
    }
}

/// Copy when the source offers it, otherwise nothing: never a move.
pub fn allowed_effect(offered: DROPEFFECT) -> DROPEFFECT {
    if offered.0 & DROPEFFECT_COPY.0 != 0 {
        DROPEFFECT_COPY
    } else {
        DROPEFFECT_NONE
    }
}

fn hdrop_format() -> FORMATETC {
    FORMATETC { cfFormat: CF_HDROP.0, ptd: std::ptr::null_mut(), dwAspect: DVASPECT_CONTENT.0, lindex: -1, tymed: TYMED_HGLOBAL.0 as u32 }
}

fn paths_of(data: &IDataObject) -> Vec<PathBuf> {
    unsafe {
        let Ok(mut medium) = data.GetData(&hdrop_format()) else { return Vec::new() };
        let drop = HDROP(medium.u.hGlobal.0);
        let count = DragQueryFileW(drop, u32::MAX, None);
        let paths = (0..count)
            .map(|i| {
                let mut buf = vec![0u16; DragQueryFileW(drop, i, None) as usize + 1];
                let n = DragQueryFileW(drop, i, Some(&mut buf)) as usize;
                PathBuf::from(OsString::from_wide(&buf[..n]))
            })
            .collect();
        ReleaseStgMedium(&mut medium);
        paths
    }
}

fn set_hover(zone: Option<DropZone>) {
    if HOVER.with(|h| h.replace(zone)) != zone {
        push_event(UiEvent::DropHover);
    }
}

#[implement(IDropTarget)]
struct DropTarget {
    zone: DropZone,
    /// The drag carries files (text and bare images are refused).
    accepts: Cell<bool>,
}

impl DropTarget {
    /// Answers the drag with `drop_effect`; true if it is taken.
    fn answer(&self, effect: *mut DROPEFFECT) -> bool {
        let Some(e) = (unsafe { effect.as_mut() }) else { return false };
        *e = drop_effect(self.accepts.get(), *e, OUTGOING.with(|o| o.get()));
        *e != DROPEFFECT_NONE
    }
}

impl IDropTarget_Impl for DropTarget_Impl {
    fn DragEnter(&self, data: Ref<IDataObject>, _: MODIFIERKEYS_FLAGS, _: &POINTL, effect: *mut DROPEFFECT) -> Result<()> {
        let files = data.as_ref().is_some_and(|d| unsafe { d.QueryGetData(&hdrop_format()) }.is_ok());
        self.accepts.set(files);
        if self.answer(effect) {
            set_hover(Some(self.zone));
        }
        Ok(())
    }

    fn DragOver(&self, _: MODIFIERKEYS_FLAGS, _: &POINTL, effect: *mut DROPEFFECT) -> Result<()> {
        self.answer(effect);
        Ok(())
    }

    fn DragLeave(&self) -> Result<()> {
        set_hover(None);
        Ok(())
    }

    fn Drop(&self, data: Ref<IDataObject>, _: MODIFIERKEYS_FLAGS, _: &POINTL, effect: *mut DROPEFFECT) -> Result<()> {
        set_hover(None);
        if let (true, Some(d)) = (self.answer(effect), data.as_ref()) {
            let paths = paths_of(d);
            if !paths.is_empty() {
                DROPPED.with(|q| q.borrow_mut().extend(paths));
                push_event(UiEvent::Dropped);
            }
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use windows::Win32::System::Ole::{DROPEFFECT_LINK, DROPEFFECT_MOVE};

    #[test]
    fn allowed_effect_never_moves() {
        assert_eq!(allowed_effect(DROPEFFECT_COPY | DROPEFFECT_MOVE), DROPEFFECT_COPY);
        assert_eq!(allowed_effect(DROPEFFECT_COPY | DROPEFFECT_LINK), DROPEFFECT_COPY);
        assert_eq!(allowed_effect(DROPEFFECT_MOVE), DROPEFFECT_NONE);
        assert_eq!(allowed_effect(DROPEFFECT_LINK), DROPEFFECT_NONE);
    }

    #[test]
    fn only_copyable_files_from_elsewhere_are_taken() {
        assert_eq!(drop_effect(true, DROPEFFECT_COPY | DROPEFFECT_MOVE, false), DROPEFFECT_COPY);
        assert_eq!(drop_effect(true, DROPEFFECT_MOVE, false), DROPEFFECT_NONE, "a move-only drag gets no hint and no drop");
        assert_eq!(drop_effect(false, DROPEFFECT_COPY, false), DROPEFFECT_NONE, "text or a bare image");
        assert_eq!(drop_effect(true, DROPEFFECT_COPY, true), DROPEFFECT_NONE, "the shelf's own drag coming back");
    }

    #[test]
    fn drag_threshold_is_the_systems() {
        let (cx, cy) = unsafe { (GetSystemMetrics(SM_CXDRAG), GetSystemMetrics(SM_CYDRAG)) };
        assert!(!drag_moved(cx - 1, 0));
        assert!(drag_moved(cx, 0));
        assert!(drag_moved(0, -cy));
        assert!(!drag_moved(0, 0));
    }
}
