//! What this binary was built from, compiled in so a debug bundle can say (07_webui.md §6.2 (`W36`–`W43`)).
//!
//! A `MANIFEST.md` that says `infocompanion-webui (0.1.0)` and nothing else is a bug report nobody
//! can answer: 0.1.0 is every build this repository has ever made. The manifest has to name the
//! commit, and the commit is only knowable here — at build time, by the one tool that knows it.
//!
//! Two sources, in this order, because the two places this crate is built are not alike.
//!
//! 1. **The environment.** `IC_BUILD_*`, set by whatever is driving the build. `Build/check.sh`
//!    exports them, and so can any script that builds the image. A value of `unknown` counts as
//!    not set, so a caller can pass through a default without suppressing the next source.
//! 2. **`git` on the source tree.** A working checkout has no such variable and `git` right beside
//!    it — which is what makes a `cargo test` or a `cargo run` during development carry the same
//!    fact as the image does, and what lets the WebUI's own tests assert the rendering rule from
//!    real values.
//!
//! In the image there is a third path, and it is the one that matters: the Dockerfile copies the
//! context into a builder stage and reads `.git` out of it, because there is no repository to run
//! `git` in down there, and passes the three values to `cargo` as environment. See `Build/Dockerfile`.
//!
//! **Nothing here may fail the build.** A build with no repository — a tarball, an export, a
//! vendored copy — gets `unknown` and says so in the manifest, which is an honest answer; refusing
//! to compile is not.

use std::process::Command;

fn main() {
    for key in KEYS {
        // Without this, cargo would not rebuild when the identity changes, and an image reporting
        // the *previous* commit is worse than one reporting nothing at all.
        println!("cargo:rerun-if-env-changed={key}");
    }
    println!("cargo:rerun-if-changed=build.rs");

    emit(
        "IC_BUILD_SHA",
        from_env("IC_BUILD_SHA").or_else(|| git(&["rev-parse", "--short=12", "HEAD"])),
    );
    emit(
        "IC_BUILD_BRANCH",
        from_env("IC_BUILD_BRANCH").or_else(|| git(&["rev-parse", "--abbrev-ref", "HEAD"])),
    );
    emit(
        "IC_BUILD_DATE",
        from_env("IC_BUILD_DATE").or_else(|| git(&["show", "-s", "--format=%cI", "HEAD"])),
    );
    emit(
        "IC_BUILD_DIRTY",
        from_env("IC_BUILD_DIRTY").or_else(|| {
            // Only ever asked of a checkout with a worktree, which the image's read-only `.git` is
            // not: the context has had `Documents/`, `*.md` and the rest filtered out of it, so
            // `git status` there would report a tree that is dirty because it is *filtered*, and a
            // manifest claiming that would be a lie. No worktree, no answer.
            git(&["status", "--porcelain", "--untracked-files=no"]).map(|out| {
                if out.is_empty() {
                    "false".to_string()
                } else {
                    "true".to_string()
                }
            })
        }),
    );
}

const KEYS: [&str; 4] = [
    "IC_BUILD_SHA",
    "IC_BUILD_BRANCH",
    "IC_BUILD_DATE",
    "IC_BUILD_DIRTY",
];

/// One value, or nothing at all. `unknown` is nothing: it is what a caller writes when it has no
/// answer, and letting it through would stop the next source from being asked.
fn from_env(key: &str) -> Option<String> {
    let value = std::env::var(key).ok()?;
    let value = value.trim();
    if value.is_empty() || value.eq_ignore_ascii_case("unknown") {
        return None;
    }
    Some(value.to_string())
}

/// One `git` question, asked of the source tree this build script is running in. Silence on any
/// failure — a build script that panics takes the build with it.
fn git(args: &[&str]) -> Option<String> {
    let output = Command::new("git").args(args).output().ok()?;
    if !output.status.success() {
        return None;
    }
    let text = String::from_utf8(output.stdout).ok()?;
    let text = text.trim();
    if text.is_empty() {
        return None;
    }
    Some(text.to_string())
}

/// Hand the value to the compiler, or hand over the word `unknown` so that the value is always
/// there to read and the *reason* it is missing is decided in one place (`identity.rs`).
fn emit(key: &str, value: Option<String>) {
    println!(
        "cargo:rustc-env={key}={}",
        value.unwrap_or_else(|| "unknown".to_string())
    );
}
