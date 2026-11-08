//! The commit this binary came from, as the bundle reports it (10_delivery.md §8.4 (`D22`–`D24`)).
//!
//! The values themselves are read at build time by `build.rs` and compiled in, so nothing here can
//! fail and nothing here needs a process to run: a running image has no `.git` and no `git`, and
//! the whole point is that the manifest can answer a question the container cannot look up.
//!
//! [`Build::describe`] is the one place the answer is turned into words, and it is deliberately
//! verbose about not knowing: a manifest that prints a short, confident `unknown` invites the
//! reader to assume the reader is at fault, where `this binary was built with no repository in
//! reach` tells them what happened and what to do about it.

/// What this binary was built from, as far as it could be established at build time.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Build {
    /// The commit, abbreviated — 12 characters, which is unambiguous for a repository this size.
    pub sha: Option<&'static str>,
    /// The branch it was on, which is `HEAD` on a detached checkout and says so.
    pub branch: Option<&'static str>,
    /// When the commit was made, in the same ISO form the rest of the program prints.
    pub date: Option<&'static str>,
    /// Whether the tree had uncommitted changes. `None` where no worktree was readable — which is
    /// every build inside the image, where `.git` arrives without its working tree.
    pub dirty: Option<bool>,
}

const SHA: Option<&str> = option_env!("IC_BUILD_SHA");
const BRANCH: Option<&str> = option_env!("IC_BUILD_BRANCH");
const DATE: Option<&str> = option_env!("IC_BUILD_DATE");
const DIRTY: Option<&str> = option_env!("IC_BUILD_DIRTY");

/// The three words a build script can write for a boolean, and nothing else is an answer.
fn dirty(text: Option<&str>) -> Option<bool> {
    match text {
        Some("true") | Some("1") => Some(true),
        Some("false") | Some("0") => Some(false),
        _ => None,
    }
}

impl Build {
    /// The identity compiled into the running binary.
    pub fn of_this_binary() -> Self {
        Self {
            sha: SHA,
            branch: BRANCH,
            date: DATE,
            dirty: dirty(DIRTY),
        }
    }

    /// One line for the manifest: the commit, and what is known about it beside.
    pub fn describe(&self) -> String {
        let Some(sha) = self.sha else {
            return "unknown — this binary was built with no repository in reach, so it cannot say \
                    which commit it is"
                .to_string();
        };

        let mut beside: Vec<String> = Vec::new();
        if let Some(branch) = self.branch {
            beside.push(branch.to_string());
        }
        if let Some(date) = self.date {
            beside.push(format!("committed {date}"));
        }
        if self.dirty == Some(true) {
            beside.push("built from a tree with uncommitted changes".to_string());
        }

        if beside.is_empty() {
            sha.to_string()
        } else {
            format!("{sha} ({})", beside.join(", "))
        }
    }
}

#[cfg(test)]
mod tests {
    use super::Build;

    #[test]
    fn a_commit_is_named_with_what_is_known_about_it() {
        let build = Build {
            sha: Some("e9f1a2b3c4d5"),
            branch: Some("main"),
            date: Some("2026-09-21T15:19:37+01:00"),
            dirty: Some(false),
        };
        assert_eq!(
            build.describe(),
            "e9f1a2b3c4d5 (main, committed 2026-09-21T15:19:37+01:00)"
        );
    }

    #[test]
    fn a_dirty_tree_says_so_because_the_commit_alone_would_be_misleading() {
        let build = Build {
            sha: Some("e9f1a2b3c4d5"),
            branch: Some("main"),
            date: None,
            dirty: Some(true),
        };
        assert_eq!(
            build.describe(),
            "e9f1a2b3c4d5 (main, built from a tree with uncommitted changes)"
        );
    }

    #[test]
    fn nothing_known_is_said_plainly_rather_than_guessed() {
        // The one case the manifest must never get wrong: it cannot name a commit, so it says why
        // instead of printing a bare `unknown` that the reader has to interpret.
        let build = Build {
            sha: None,
            branch: None,
            date: None,
            dirty: None,
        };
        let text = build.describe();
        assert!(text.contains("unknown"), "{text}");
        assert!(text.contains("no repository in reach"), "{text}");
    }

    #[test]
    fn a_sha_with_nothing_beside_it_is_still_a_commit() {
        let build = Build {
            sha: Some("e9f1a2b3c4d5"),
            branch: None,
            date: None,
            dirty: None,
        };
        assert_eq!(build.describe(), "e9f1a2b3c4d5");
    }

    #[test]
    fn this_binary_knows_its_own_commit_when_it_was_built_from_a_checkout() {
        // Deliberately conditional: a build from a tarball is a legitimate build and must not fail
        // its own test suite. In a checkout — which is where the tests run — the value is real, and
        // this asserts the whole path end to end: `build.rs`, the environment hand-off, and the
        // compile-time read.
        let build = Build::of_this_binary();
        if let Some(sha) = build.sha {
            assert!(
                sha.len() >= 7 && sha.chars().all(|c| c.is_ascii_hexdigit()),
                "a commit hash, not {sha:?}"
            );
            assert!(build.describe().contains(sha));
        }
    }
}
