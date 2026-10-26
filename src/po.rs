// Gettext `.po` entries, shared by the root build script (`include!`, to build the English table)
// and `i18n` (its tests). Plain functions only: no crate paths.

/// A quoted `.po` string's text, its escapes undone.
fn po_unquote(quoted: &str) -> String {
    let inner = quoted.trim().strip_prefix('"').and_then(|q| q.strip_suffix('"')).unwrap_or("");
    let mut out = String::new();
    let mut chars = inner.chars();
    while let Some(c) = chars.next() {
        match (c, c == '\\') {
            (_, true) => match chars.next() {
                Some('n') => out.push('\n'),
                Some('t') => out.push('\t'),
                Some('r') => out.push('\r'),
                Some(other) => out.push(other),
                None => {}
            },
            (c, false) => out.push(c),
        }
    }
    out
}

/// The translated entries of a `.po` file: (msgid, msgstr), in file order; an entry with a
/// msgctxt has "context\u{4}msgid" (gettext's own joint). The header (empty msgid), entries
/// with no translation yet and plural entries are left out.
pub fn parse_po(text: &str) -> Vec<(String, String)> {
    #[derive(PartialEq)]
    enum Field {
        Other,
        Ctxt,
        Id,
        Str,
    }
    let mut out = Vec::new();
    let (mut id, mut msgstr, mut skip, mut field) = (String::new(), String::new(), false, Field::Other);
    let (mut ctxt, mut seen_str) = (None::<String>, false);
    let mut finish = |id: &mut String, msgstr: &mut String, ctxt: &mut Option<String>, skip: &mut bool, seen_str: &mut bool| {
        if !id.is_empty() && !msgstr.is_empty() && !*skip {
            let key = match ctxt.take() {
                Some(c) => format!("{c}\u{4}{id}"),
                None => std::mem::take(id),
            };
            out.push((key, std::mem::take(msgstr)));
        }
        id.clear();
        msgstr.clear();
        *ctxt = None;
        *skip = false;
        *seen_str = false;
    };
    for raw in text.lines() {
        let line = raw.trim();
        let starts_entry = line.starts_with("msgctxt") || (line.starts_with("msgid ") && seen_str);
        if line.is_empty() || starts_entry {
            finish(&mut id, &mut msgstr, &mut ctxt, &mut skip, &mut seen_str);
            field = Field::Other;
        }
        if line.is_empty() || line.starts_with('#') {
            continue;
        }
        if let Some(rest) = line.strip_prefix("msgctxt ") {
            ctxt = Some(po_unquote(rest));
            field = Field::Ctxt;
        } else if line.starts_with("msgid_plural") || line.starts_with("msgstr[") {
            skip = true;
            seen_str |= line.starts_with("msgstr[");
            field = Field::Other;
        } else if let Some(rest) = line.strip_prefix("msgid ") {
            id = po_unquote(rest);
            field = Field::Id;
        } else if let Some(rest) = line.strip_prefix("msgstr ") {
            msgstr = po_unquote(rest);
            seen_str = true;
            field = Field::Str;
        } else if line.starts_with('"') {
            match field {
                Field::Ctxt => ctxt.get_or_insert_with(String::new).push_str(&po_unquote(line)),
                Field::Id => id.push_str(&po_unquote(line)),
                Field::Str => msgstr.push_str(&po_unquote(line)),
                Field::Other => {}
            }
        }
    }
    finish(&mut id, &mut msgstr, &mut ctxt, &mut skip, &mut seen_str);
    out
}
