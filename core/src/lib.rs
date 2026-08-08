// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Brandon Doster

//! Everything ScreenX does that is not drawing: reading the screen, working out
//! a filename, and the settings file.
//!
//! Deliberately free of any windowing or UI dependency, so the same core serves
//! whatever draws on top of it.

pub mod capture;
pub mod naming;
pub mod settings;
