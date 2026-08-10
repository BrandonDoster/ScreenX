// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Brandon Doster

//! Links the Windows icon resource into the executable.
//!
//! The tray icon is loaded at runtime and was never the problem. Explorer,
//! Task Manager, Alt-Tab and the taskbar button read an `RT_GROUP_ICON`
//! resource out of the PE instead, and nothing was writing one — `tauri-build`
//! used to do it, and the native build has no equivalent.

fn main() {
    #[cfg(windows)]
    {
        println!("cargo:rerun-if-changed=../assets/icon.ico");
        winresource::WindowsResource::new()
            .set_icon("../assets/icon.ico")
            .compile()
            .expect("could not link the Windows icon resource");
    }
}
