//! What this process will serve, and what it must never serve.
//!
//! The pages are named one by one; everything else has to be a *flat* file with an extension
//! this server knows, either at the root or under `/assets/`. Nothing here can produce a name
//! containing a slash, a leading dot or a `..`, so there is no path to traverse — which is why
//! the guard is a shape rather than a sanitiser. A decoder in front of us that percent-decodes
//! `%2f` only ever hands us something *more* like a path, and every such thing is refused.

/// Pages, by name. A route is a page and a page is a route: there is no directory listing, and
/// no way to ask for a file that is not on this list or a flat asset.
pub const ROUTES: [(&str, &str); 4] = [
    ("/", "index.html"),
    ("/index.html", "index.html"),
    ("/term", "term.html"),
    ("/multiterm", "multiterm.html"),
];

/// The extensions we will name a content type for, and the types.
///
/// An extension that is not here is not a file this server will hand out, which is one of the
/// two things keeping the whitelist a whitelist.
const TYPES: [(&str, &str); 7] = [
    (".html", "text/html; charset=utf-8"),
    (".css", "text/css; charset=utf-8"),
    (".js", "text/javascript; charset=utf-8"),
    (".svg", "image/svg+xml"),
    (".md", "text/markdown; charset=utf-8"),
    (".png", "image/png"),
    (".ico", "image/x-icon"),
];

/// The content type for a file name, if it is a type we serve.
pub fn content_type(name: &str) -> Option<&'static str> {
    let extension = extension_of(name);
    TYPES
        .iter()
        .find(|(kind, _)| *kind == extension)
        .map(|(_, kind)| *kind)
}

/// The lowercased extension of a name, including its dot (`a.JS` -> `.js`), or empty.
fn extension_of(name: &str) -> String {
    match name.rfind('.') {
        Some(at) => name[at..].to_lowercase(),
        None => String::new(),
    }
}

/// Map a URL path to a file under the web root, by whitelist.
///
/// Returns the relative path to read, or `None` for anything that is not a page we know or a
/// flat asset with a known extension.
pub fn file_for(path: &str) -> Option<String> {
    if let Some((_, page)) = ROUTES.iter().find(|(route, _)| *route == path) {
        return Some((*page).to_string());
    }

    let under_assets = path.starts_with("/assets/");
    let name = if under_assets {
        &path["/assets/".len()..]
    } else {
        path.trim_start_matches('/')
    };

    if name.is_empty() || name.starts_with('.') || name.contains('/') {
        return None;
    }
    content_type(name)?;

    Some(if under_assets {
        format!("assets/{name}")
    } else {
        name.to_string()
    })
}

/// Where the pages live, when nobody said.
///
/// The Python bridge found them beside itself, which stops being true the moment this is a
/// binary in `/usr/local/bin` rather than a script in `/opt/webui`. So: beside the executable
/// first (a hand-built copy), then where the image puts them, then the working directory.
pub fn default_root() -> std::path::PathBuf {
    let beside = std::env::current_exe()
        .ok()
        .and_then(|exe| exe.parent().map(|dir| dir.join("public")));

    let mut candidates: Vec<std::path::PathBuf> = Vec::new();
    if let Some(beside) = beside {
        candidates.push(beside);
    }
    candidates.push(std::path::PathBuf::from("/opt/webui/public"));
    candidates.push(std::path::PathBuf::from("public"));

    candidates
        .iter()
        .find(|dir| dir.is_dir())
        .unwrap_or(&candidates[0])
        .clone()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_pages_are_the_pages() {
        assert_eq!(file_for("/").as_deref(), Some("index.html"));
        assert_eq!(file_for("/index.html").as_deref(), Some("index.html"));
        assert_eq!(file_for("/term").as_deref(), Some("term.html"));
        assert_eq!(file_for("/multiterm").as_deref(), Some("multiterm.html"));
    }

    #[test]
    fn a_flat_asset_is_served_and_the_browser_gets_its_type() {
        assert_eq!(file_for("/app.css").as_deref(), Some("app.css"));
        assert_eq!(file_for("/app.js").as_deref(), Some("app.js"));
        assert_eq!(
            file_for("/assets/frame.svg").as_deref(),
            Some("assets/frame.svg")
        );
        assert_eq!(
            file_for("/assets/xterm-addon-fit.js").as_deref(),
            Some("assets/xterm-addon-fit.js")
        );
        assert_eq!(content_type("app.css"), Some("text/css; charset=utf-8"));
        assert_eq!(content_type("FRAME.SVG"), Some("image/svg+xml"));
        assert_eq!(
            content_type("xterm-addon-fit.js"),
            Some("text/javascript; charset=utf-8")
        );
    }

    /// Every one of these is a path the smoke test asks for by hand, with the bytes it wrote,
    /// because `urllib` normalises `..` on the way out and would have the test checking itself.
    #[test]
    fn nothing_that_leads_out_of_the_web_root_is_served() {
        for path in [
            "/../etc/passwd",
            "/assets/../../etc/passwd",
            "/assets/..%2fpasswd",
            "/etc/passwd",
            "/assets/",
            "/nope",
            "/app.css/../app.js",
            "/assets/",
            "/.",
            "/..",
            "/.env",
            "/assets/.env",
            "/index.html/../../etc/passwd",
        ] {
            assert_eq!(file_for(path), None, "{path} must be refused");
        }
    }

    #[test]
    fn an_extension_we_cannot_name_is_not_served() {
        // The web root holds the tests and a README too; neither is on the wire.
        assert_eq!(file_for("/README.md"), Some("README.md".into()));
        assert_eq!(file_for("/server.py"), None);
        assert_eq!(file_for("/Cargo.toml"), None);
        assert_eq!(file_for("/assets/xterm.js.bak"), None);
        assert_eq!(file_for("/assets"), None);
    }
}
