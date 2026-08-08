// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Brandon Doster

//! Filename pattern expansion.
//!
//! Tokens are written as `%name`, optionally with a `{n}` width argument
//! (`%i{4}`, `%ra{8}`). Unknown tokens are left in place so a typo shows up in
//! the filename instead of silently vanishing.

use chrono::{DateTime, Datelike, Local, Timelike};
use rand::Rng;

const MONTHS_SHORT: [&str; 12] = [
    "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec",
];
const MONTHS_LONG: [&str; 12] = [
    "January", "February", "March", "April", "May", "June", "July", "August", "September",
    "October", "November", "December",
];
const DAYS_SHORT: [&str; 7] = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
const DAYS_LONG: [&str; 7] = [
    "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday",
];

const ALPHANUM: &[u8] = b"abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
const DIGITS: &[u8] = b"0123456789";
const HEX: &[u8] = b"0123456789abcdef";

/// Longest first, so `%mon2` is never read as `%mo` followed by "n2".
const TOKENS: [&str; 27] = [
    "height", "width", "mon2", "unix", "guid", "h12", "mon", "yy", "mo", "wy", "w2", "mi", "ms",
    "pm", "ra", "rn", "rx", "un", "cn", "pn", "y", "d", "w", "h", "s", "i", "t",
];

pub struct Context {
    pub now: DateTime<Local>,
    pub counter: u64,
    pub title: String,
    pub width: u32,
    pub height: u32,
    pub user_name: String,
    pub computer_name: String,
}

impl Default for Context {
    fn default() -> Self {
        Self {
            now: Local::now(),
            counter: 1,
            title: String::new(),
            width: 0,
            height: 0,
            user_name: whoami_user(),
            computer_name: hostname(),
        }
    }
}

fn whoami_user() -> String {
    std::env::var("USER")
        .or_else(|_| std::env::var("USERNAME"))
        .unwrap_or_default()
}

fn hostname() -> String {
    let raw = std::env::var("HOSTNAME")
        .or_else(|_| std::env::var("COMPUTERNAME"))
        .unwrap_or_default();
    if !raw.is_empty() {
        return raw.split('.').next().unwrap_or_default().to_string();
    }
    // macOS does not export HOSTNAME to GUI apps.
    std::process::Command::new("hostname")
        .output()
        .ok()
        .and_then(|out| String::from_utf8(out.stdout).ok())
        .map(|s| s.trim().split('.').next().unwrap_or_default().to_string())
        .unwrap_or_default()
}

fn random_from(alphabet: &[u8], length: usize) -> String {
    let mut rng = rand::rng();
    (0..length)
        .map(|_| alphabet[rng.random_range(0..alphabet.len())] as char)
        .collect()
}

fn guid() -> String {
    let mut bytes = [0u8; 16];
    rand::rng().fill(&mut bytes);
    bytes[6] = (bytes[6] & 0x0f) | 0x40; // version 4
    bytes[8] = (bytes[8] & 0x3f) | 0x80; // variant 1
    let hex: String = bytes.iter().map(|b| format!("{b:02x}")).collect();
    format!(
        "{}-{}-{}-{}-{}",
        &hex[0..8],
        &hex[8..12],
        &hex[12..16],
        &hex[16..20],
        &hex[20..32]
    )
}

fn expand(token: &str, arg: usize, ctx: &Context) -> String {
    let now = &ctx.now;
    let month = now.month0() as usize;
    let weekday = now.weekday().num_days_from_monday() as usize;
    match token {
        "y" => now.year().to_string(),
        "yy" => format!("{:02}", now.year() % 100),
        "mo" => format!("{:02}", now.month()),
        "mon" => MONTHS_SHORT[month].to_string(),
        "mon2" => MONTHS_LONG[month].to_string(),
        "d" => format!("{:02}", now.day()),
        "w" => DAYS_SHORT[weekday].to_string(),
        "w2" => DAYS_LONG[weekday].to_string(),
        "wy" => format!("{:02}", now.iso_week().week()),
        "h" => format!("{:02}", now.hour()),
        "h12" => format!("{:02}", now.hour12().1),
        "mi" => format!("{:02}", now.minute()),
        "s" => format!("{:02}", now.second()),
        "ms" => format!("{:03}", now.timestamp_subsec_millis()),
        "pm" => if now.hour() < 12 { "AM" } else { "PM" }.to_string(),
        "unix" => now.timestamp().to_string(),
        "i" => format!("{:0width$}", ctx.counter, width = arg.max(1)),
        "ra" => random_from(ALPHANUM, if arg == 0 { 10 } else { arg }),
        "rn" => random_from(DIGITS, if arg == 0 { 10 } else { arg }),
        "rx" => random_from(HEX, if arg == 0 { 10 } else { arg }),
        "guid" => guid(),
        "t" => ctx.title.clone(),
        "width" => ctx.width.to_string(),
        "height" => ctx.height.to_string(),
        "pn" => "ScreenX".to_string(),
        "un" => ctx.user_name.clone(),
        "cn" => ctx.computer_name.clone(),
        _ => String::new(),
    }
}

/// Strip anything no mainstream filesystem accepts, plus control characters.
pub fn sanitize(value: &str) -> String {
    let cleaned: String = value
        .chars()
        .filter(|c| !matches!(c, '\\' | '/' | ':' | '*' | '?' | '"' | '<' | '>' | '|') && !c.is_control())
        .collect();
    // Collapse runs of whitespace the way a person would write the name.
    let mut out = String::with_capacity(cleaned.len());
    let mut last_space = false;
    for c in cleaned.chars() {
        let is_space = c.is_whitespace();
        if is_space && last_space {
            continue;
        }
        out.push(if is_space { ' ' } else { c });
        last_space = is_space;
    }
    out.trim().to_string()
}

/// Read `{123}` at `bytes[i..]`, returning the value and how far it ran.
fn read_arg(bytes: &[u8], i: usize) -> (usize, usize) {
    if bytes.get(i) != Some(&b'{') {
        return (0, 0);
    }
    let mut j = i + 1;
    let mut value = 0usize;
    let mut digits = 0;
    while j < bytes.len() && bytes[j].is_ascii_digit() && digits < 3 {
        value = value * 10 + (bytes[j] - b'0') as usize;
        j += 1;
        digits += 1;
    }
    if digits > 0 && bytes.get(j) == Some(&b'}') {
        (value, j + 1 - i)
    } else {
        (0, 0)
    }
}

/// Expand a pattern into a filename stem that is safe to write to disk.
pub fn parse_name(pattern: &str, ctx: &Context) -> String {
    let bytes = pattern.as_bytes();
    let mut out = String::with_capacity(pattern.len() + 16);
    let mut i = 0;

    while i < bytes.len() {
        if bytes[i] != b'%' {
            // Push whole UTF-8 characters, not bytes.
            let ch = pattern[i..].chars().next().unwrap();
            out.push(ch);
            i += ch.len_utf8();
            continue;
        }
        let rest = &pattern[i + 1..];
        match TOKENS.iter().find(|t| rest.starts_with(**t)) {
            Some(token) => {
                let after = i + 1 + token.len();
                let (arg, arg_len) = read_arg(bytes, after);
                out.push_str(&sanitize(&expand(token, arg, ctx)));
                i = after + arg_len;
            }
            // Not a token we know: leave it visible in the name.
            None => {
                out.push('%');
                i += 1;
            }
        }
    }

    let cleaned = collapse_separators(&sanitize(&out));
    if cleaned.is_empty() {
        "capture".to_string()
    } else {
        cleaned
    }
}

/// An empty token (a blank window title) leaves doubled or trailing separators.
fn collapse_separators(value: &str) -> String {
    let mut out = String::with_capacity(value.len());
    let mut previous: Option<char> = None;
    for c in value.chars() {
        let separator = matches!(c, '-' | '_' | ' ');
        if separator && previous.map(|p| matches!(p, '-' | '_' | ' ')).unwrap_or(false) {
            continue;
        }
        out.push(c);
        previous = Some(c);
    }
    out.trim_matches(|c| matches!(c, '-' | '_' | ' ' | '.')).to_string()
}

#[cfg(test)]
mod tests {
    use super::*;
    use chrono::TimeZone;

    fn ctx() -> Context {
        Context {
            // Saturday 4 July 2026, 09:05:03.042
            now: Local.with_ymd_and_hms(2026, 7, 4, 9, 5, 3).unwrap()
                + chrono::Duration::milliseconds(42),
            counter: 7,
            title: String::new(),
            width: 800,
            height: 600,
            user_name: "tester".into(),
            computer_name: "box".into(),
        }
    }

    #[test]
    fn expands_date_and_time() {
        let c = ctx();
        assert_eq!(parse_name("ScreenX_%y-%mo-%d_%h-%mi-%s", &c), "ScreenX_2026-07-04_09-05-03");
        assert_eq!(parse_name("%yy%mon%mon2%w%w2", &c), "26JulJulySatSaturday");
        assert_eq!(parse_name("%h12%pm-%ms", &c), "09AM-042");
    }

    #[test]
    fn longest_token_wins() {
        let c = ctx();
        assert_eq!(parse_name("%mon2", &c), "July");
        assert_eq!(parse_name("%mo", &c), "07");
        assert_eq!(parse_name("%h12", &c), "09");
        assert_eq!(parse_name("%h", &c), "09");
    }

    #[test]
    fn width_argument_pads() {
        let c = ctx();
        assert_eq!(parse_name("%i", &c), "7");
        assert_eq!(parse_name("%i{4}", &c), "0007");
        assert_eq!(parse_name("shot-%ra{6}", &c).len(), "shot-".len() + 6);
        let hex = parse_name("%rx{8}", &c);
        assert_eq!(hex.len(), 8);
        assert!(hex.chars().all(|c| c.is_ascii_hexdigit()));
    }

    #[test]
    fn unknown_tokens_survive() {
        assert_eq!(parse_name("%nope", &ctx()), "%nope");
    }

    #[test]
    fn strips_path_separators_and_reserved_characters() {
        let mut c = ctx();
        c.title = r#"a/b\c:d*e?f"g<h>i|j"#.into();
        assert_eq!(parse_name("%t", &c), "abcdefghij");
        assert_eq!(sanitize("../../etc/passwd"), "....etcpasswd");
    }

    #[test]
    fn blank_title_leaves_no_dangling_separator() {
        let c = ctx();
        assert_eq!(parse_name("shot_%t", &c), "shot");
        assert_eq!(parse_name("%t-%width x %height", &c), "800 x 600");
    }

    #[test]
    fn always_produces_something_usable() {
        let mut c = ctx();
        assert_eq!(parse_name("", &c), "capture");
        c.title = "///".into();
        assert_eq!(parse_name("%t", &c), "capture");
    }

    #[test]
    fn iso_week_matches_the_calendar() {
        // 1 January 2026 is a Thursday, so it belongs to week 1.
        let mut c = ctx();
        c.now = Local.with_ymd_and_hms(2026, 1, 1, 12, 0, 0).unwrap();
        assert_eq!(parse_name("%wy", &c), "01");
        c.now = Local.with_ymd_and_hms(2026, 1, 5, 12, 0, 0).unwrap();
        assert_eq!(parse_name("%wy", &c), "02");
    }

    #[test]
    fn guid_has_the_right_shape() {
        let value = parse_name("%guid", &ctx());
        let parts: Vec<&str> = value.split('-').collect();
        assert_eq!(parts.iter().map(|p| p.len()).collect::<Vec<_>>(), vec![8, 4, 4, 4, 12]);
        assert!(value.chars().all(|c| c.is_ascii_hexdigit() || c == '-'));
    }

    #[test]
    fn unicode_in_a_pattern_is_not_split() {
        let mut c = ctx();
        c.title = "Ünïcødé — window".into();
        assert_eq!(parse_name("%t", &c), "Ünïcødé — window");
    }
}
